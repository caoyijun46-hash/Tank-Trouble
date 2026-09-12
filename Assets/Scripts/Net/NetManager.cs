using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using Unity.Networking.Transport;

public enum NetRole { Host, Client }

// 阶段 1 传输管理：一个脚本管 Host/Client 两种角色。
//
// 模型：主机权威 + 实体注册表状态同步（块 2 起）+ 命令上行（块 3 起）。
//   Host   = 世界模拟本体：玩法对象挂 NetSyncEntity 自动进注册表（OnEnable/OnDisable），
//            每 snapshotInterval 采集全部实体组一帧广播（默认不可靠管道：帧自描述，
//            丢一帧下帧全量纠正，无需重传）；对象出生/销毁走独立 Spawn/Despawn
//            可靠事件（壳的创建/删除是一次性事实，丢不得）；client 上行命令
//            喂给当前化身（GameManager 联机分支注册的 Tank）
//   Client = 快照播放器：收 Spawn → 按 typeKey 实例化壳 prefab（SnapshotPlayer）；
//            收实体帧 → 分发各槽插值缓冲 → 平滑渲染；收 Despawn → 删壳；
//            输入源（ClientInputSimulator）读键盘 → SendCommand 上行
//
// 联机状态门：Awake 判 GameConfig.Mode != Online 则整组件休眠（不设 Instance、
// 不建 driver）——单机玩法零联机开销。
//
// 重写背景：旧实现长期"握手成功但 Data 空载荷 NRE"，与同场景下验证通过的
// MinimalBroadcast 对照无法定位差异，决定整体重写——内部结构严格照抄
// MinimalBroadcast.cs 的已验证写法（NetworkDriver.Create() 无参 / 初始化在
// Start / Accept while 循环 + NativeList 存连接 / Client PopEvent 循环 /
// BeginSend 三参+错误码判断+EndSend），不再引入它没有的任何模式。
//
// 引擎模型（Unity Transport，每帧轮询）：driver.ScheduleUpdate().Complete()
// 把底层收发包推进完，然后 Accept/PopEvent 取事件——包到达不打断游戏（非阻塞轮询）
public class NetManager : MonoBehaviour
{
    public static NetManager Instance { get; private set; }

    [Header("角色")]
    [SerializeField] private NetRole role = NetRole.Host;

    [Header("连接")]
    [SerializeField] private string serverIp = "127.0.0.1"; // 同机测试用回环
    [SerializeField] private ushort port = 7778; // 与参考脚本同端口（7777 实测常被占）

    [Header("同步")]
    [Tooltip("联机参数（Assets/Config/NetConfig.asset：快照率/插值深度/壳超时/输入频率）——手感旋钮单一来源，不设组件侧第二套字段")]
    [SerializeField] private NetConfig netConfig;
    private float snapshotInterval; // Start 从 netConfig 缓存（热路径不查资产；兜底常量仅供缺配不崩）
    private float shellTimeout;
    private ISyncHost avatar; // 联机化身：client 玩家的车（1v1 单化身，GameManager 联机分支注册）

    // 对局事件下行出口（Client 端收 host 广播后触发；UI 组件判空订阅）。
    // host 端不收这些 type（对局事件只 host→client），事件在 host 永不触发。
    // RoundEnd 里的 winner 字段已解析但暂不外传——胜利横幅后置，届时再扩事件
    public event System.Action<int, int> ScoreSynced; // 比分 (score1, score2)：RoundEnd/RoundStart 共用
    public event System.Action<bool> PauseSynced;     // host 暂停状态（true=暂停中）
    public event System.Action SessionEnded;          // host 结束会话（点 Menu）或连接意外断开（兜底）

    [Header("Client 实体壳")]
    [Tooltip("壳 prefab 表，下标 = typeKey（与玩法 prefab 上 NetSyncEntity.typeKey 对齐）：0=坦克(Dead Tank) 1=坦克1(Dead Tank 1) 2=道具 3=子弹 4=激光 5=导弹")]
    [SerializeField] private SnapshotPlayer[] shellPrefabs;
    [Tooltip("实例化壳的父容器（场景里一个空物体）")]
    [SerializeField] private Transform shellRoot;
    private FixedMapCamera clientCam; // 惰性查找缓存：结算特写执行（RoundEnd/Start 带机位）

    [Header("迷宫同构（联机调试期）")]
    [Tooltip("Host：勾上后按 R 键随机重开一张迷宫并广播（GameManager 换局接入前的联调驱动，接入后删）")]
    [SerializeField] private bool debugRebuildKeyR;

    public bool IsHost => role == NetRole.Host;

    /// <summary>Host：游戏端口是否已监听成功。bind 双档（偏好端口→OS 分配）都失败时
    /// 保持 false——LanBeacon 据此停播，不让房间"假出现"</summary>
    public bool IsListening { get; private set; }

    /// <summary>Host：实际游戏端口（偏好端口或 OS 分配的临时端口）。公告包携带它，
    /// client 从房间信息取；只在 IsListening 后有效</summary>
    public ushort ActualPort { get; private set; }

    private NetworkDriver driver;
    private NetworkPipeline reliable;                  // 可靠管道：事件类上行命令（Fire）专用
    private NativeList<NetworkConnection> serverConns; // Host：所有已接入客户端（不设单连接槽）
    private NetworkConnection connection;              // Client：到主机的连接
    private bool connected;                            // Client：握手完成后才允许发命令（可靠发送依赖连接状态机）
    private float tickTimer;                           // 距上次广播的累计时间
    private uint seq;                                  // 主机发帧计数（进快照载荷）
    private MapParamsData? latestMapParams;            // Host：最近一次 MapGenerated 的参数缓存（accept 补发用）
    private MapSpawner clientMapSpawner;               // Client：收参数后重建迷宫（惰性查找缓存）

    // ---- 实体注册表（Host 专属）：注册顺序即采集/补发顺序 ----
    private readonly List<NetSyncEntity> entities = new List<NetSyncEntity>();
    private readonly bool[] idUsed = new bool[256];    // byte id 占用表：注册分配首个空闲、注销归还。
                                                       // 不用自增计数器——换局轮回溢出会撞上仍存活的旧 id

    // ---- 实体槽表（Client 专属）：id → 壳实例 ----
    private struct ShellSlot
    {
        public SnapshotPlayer shell;
        public float lastSeen; // Time.unscaledTime：超时兜底删壳
    }
    private readonly Dictionary<byte, ShellSlot> slots = new Dictionary<byte, ShellSlot>();

    void Awake()
    {
        // 联机状态门：只有 GameMode.Online 才启用网络。单机（菜单 VsAI/Double）
        // 下整组件休眠、不设 Instance——场景里的 NetSyncEntity 判空静默跳过，
        // 零联机开销，单机玩法不受任何影响
        if (GameConfig.Mode != GameMode.Online)
        {
            enabled = false;
            return;
        }
        Instance = this;
        // Host 端每张迷宫生成（首局 Awake 自动 / RegenerateRandom 重开）都进这里：
        // 缓存参数供 accept 补发 + 立即广播给已在线的客户端。
        // 注意时序：MapSpawner 执行序 10，其 Awake 生成发生在本组件的 Start（driver
        // 创建）之前——那时只能缓存不能发送，靠 accept 补发兜住首局参数
        MapSpawner.MapGenerated += OnMapGenerated;
    }

    // 创建/绑定/连接放 Start 而非 Awake（与已验证的 MinimalBroadcast 对齐）：
    // Awake 阶段场景仍在加载，重操作可能产生大卡顿帧，连接建立时机不稳定
    void Start()
    {
        // 联机参数缓存进本地字段（tick/超时是每帧路径，不查资产）；兜底防缺配崩溃
        snapshotInterval = netConfig != null ? netConfig.snapshotInterval : 0.03f;
        shellTimeout = netConfig != null ? netConfig.shellTimeout : 3f;
        driver = NetworkDriver.Create();
        // 可靠管道：命令事件（Fire）与实体生命周期（Spawn/Despawn）走它
        reliable = driver.CreatePipeline(typeof(ReliableSequencedPipelineStage));
        if (IsHost)
        {
            // 先建连接列表再 bind：bind 失败（端口被占）也要能安全走完生命周期
            serverConns = new NativeList<NetworkConnection>(4, Allocator.Persistent);
            // 双档绑定：偏好端口（场景序列化，默认 7783——netstat 好认、编辑器直连
            // 调试可用、手输直连有默认端口）失败 → 端口 0 让 OS 从临时端口范围分配
            // （相当于几千大的天然"端口池"，冲突基本消除）。
            // 失败后重试安全：UTP 失败时已关 socket 且 Bound 保持 false，可再 Bind
            if (driver.Bind(NetworkEndpoint.AnyIpv4.WithPort(port)) != 0)
            {
                if (driver.Bind(NetworkEndpoint.AnyIpv4.WithPort(0)) != 0)
                {
                    // 偏好端口与临时端口都拿不到（socket 耗尽等）：本机无法开房。
                    // IsListening 保持 false → LanBeacon 停播，房间不会"假出现"
                    return;
                }
            }
            driver.Listen();
            IsListening = true;
            ActualPort = driver.GetLocalEndpoint().Port;
        }
        else
        {
            // 客户端不 bind——内核自动分配临时端口，握手包带源端口出发。
            // 菜单联机流程会写 GameConfig.ServerIp / ServerPort（IP 与端口都来自
            // 发现公告——host 端口被占时会退到 OS 分配，写死端口连不上）；
            // 空/0 = 场景序列化默认值，仅直开调试与手输直连用
            if (!string.IsNullOrEmpty(GameConfig.ServerIp))
            {
                serverIp = GameConfig.ServerIp;
            }
            if (GameConfig.ServerPort != 0)
            {
                port = GameConfig.ServerPort;
            }
            connection = driver.Connect(NetworkEndpoint.Parse(serverIp, port));
        }
    }

    void OnDestroy()
    {
        MapSpawner.MapGenerated -= OnMapGenerated;
        if (Instance == this)
        {
            Instance = null;
        }
        // IsCreated 防御：组件在 Start 前被销毁（如禁用态场景卸载）时 driver/列表未建
        if (driver.IsCreated)
        {
            driver.Dispose();
        }
        if (serverConns.IsCreated)
        {
            serverConns.Dispose();
        }
    }

    void Update()
    {
        driver.ScheduleUpdate().Complete();
        if (IsHost)
        {
            HostUpdate();
        }
        else
        {
            ClientUpdate();
        }
    }

    // ---------- Host：接客户端 + 周期广播快照 ----------

    void HostUpdate()
    {
        // 联调驱动：按 R 随机重开迷宫并广播（换局联动由 GameManager 接入后接管，届时删此段）
        if (debugRebuildKeyR && Input.GetKeyDown(KeyCode.R))
        {
            // RegenerateRandom 内部随机新参数 → Generate → MapGenerated 事件 → 自动广播
            var spawner = FindAnyObjectByType<MapSpawner>();
            if (spawner != null)
            {
                spawner.RegenerateRandom();
            }
        }

        // 新连接轮询：UTP 2.x 握手由驱动内部完成，Accept 取出已就绪连接。
        // while 一次取完本帧全部新连接并全收进列表——单连接槽会丢后续客户端
        NetworkConnection c;
        while ((c = driver.Accept()) != default)
        {
            serverConns.Add(c);
            // 补发当前迷宫参数：client 可能在本局生成之后才连上（首局 Awake 生成
            // 早于 driver 创建，当时广播不出去）——同构参数不能靠"等下一帧快照纠正"
            BroadcastMapParams();
            // 补发存量实体 Spawn：新连上的 client 必须先知道"世界里有谁"才能建壳，
            // 否则实体帧里的 id 没有槽可推（spawn 是可靠事件，错过只能在这补）
            for (int e = 0; e < entities.Count; e++)
            {
                SendSpawnTo(c, entities[e]);
            }
        }

        // 消费已接入连接的事件（阶段 2 新增：客户端上行命令在这里收）。
        // 阶段 1 不收是因为快照纯下行（我发你收）；现在客户端会主动发命令
        for (int i = 0; i < serverConns.Length; i++)
        {
            if (!serverConns[i].IsCreated)
            {
                continue;
            }
            NetworkEvent.Type evt;
            while ((evt = serverConns[i].PopEvent(driver, out var stream)) != NetworkEvent.Type.Empty)
            {
                if (evt == NetworkEvent.Type.Data)
                {
                    // 快照不会发向主机（它是下行消息），主机只可能收到命令——
                    // 仍走 TryRead 的长度/type 防御，协议演进期安全
                    if (CommandProtocol.TryRead(stream, out var cmd))
                    {
                        OnCommand(serverConns[i], cmd);
                    }
                }
                else if (evt == NetworkEvent.Type.Disconnect)
                {
                    serverConns[i] = default; // 正常断开能收尾；进程强退仍会留幽灵（见交接文档）
                }
            }
        }

        // 固定节拍广播（unscaled：暂停菜单/慢放不影响网络节奏）
        tickTimer += Time.unscaledDeltaTime;
        if (tickTimer >= snapshotInterval)
        {
            tickTimer = 0f;
            if (entities.Count > 0)
            {
                // 时间戳必须与广播节拍同基准：节拍走 unscaledDeltaTime（真实
                // 33Hz，不受慢放/暂停影响），hostTime 就用 unscaled——否则慢放
                // （timeScale=0.12）期间快照真实间隔 ~30ms 但时间戳只前进几 ms，
                // 客户端插值进度按时间戳差推进会失真
                uint frame = seq++;
                float hostTime = Time.unscaledTime;
                byte count = (byte)entities.Count;
                int payloadSize = SnapshotProtocol.FrameSize(count);
                for (int i = 0; i < serverConns.Length; i++)
                {
                    if (!serverConns[i].IsCreated)
                    {
                        continue; // 失效连接跳过（不 Poll 连接事件，断开由发送错误码体现）
                    }
                    // 与 MinimalBroadcast 完全同形态：三参 BeginSend + 错误码判断 + EndSend。
                    // 发送失败静默跳过：帧自描述，对端下一条全量纠正，无需重传
                    int err = driver.BeginSend(serverConns[i], out var w, payloadSize);
                    if (err == 0)
                    {
                        SnapshotProtocol.WriteFrameHeader(ref w, frame, hostTime, count);
                        for (int e = 0; e < entities.Count; e++)
                        {
                            var (x, z, yaw) = entities[e].Pose();
                            SnapshotProtocol.WriteEntity(ref w, entities[e].SyncId, x, z, yaw,
                                entities[e].PowerByte);
                        }
                        driver.EndSend(w);
                    }
                }
            }
            // 注册表空（玩家/AI 出生前、清场间隙）：本 tick 跳过发送（tick 节奏照常）
        }
    }

    // Host：上行命令分发。命令已由协议层完成解析，喂给当前联机化身执行——
    //   Tank(IsRemote)：Move 写输入缓存 / Fire 积压触发（真武器由主机世界裁决）
    // Fire 细节在对象内（Tank.Fire 自带冷却），这里不做 kind 判断——
    // 未知 kind 也喂给对象（对象侧按需忽略），协议演进不堵在传输层。
    // 1v1 单化身：不按连接区分来源；多连接路由（化身表）留待后续
    void OnCommand(NetworkConnection from, in CommandData cmd)
    {
        avatar?.ApplyCommand(cmd);
    }

    /// <summary>注册联机化身（client 玩家的车）。GameManager 联机分支每局生成化身
    /// 后调用；换局清场后新化身重新注册（旧车已销毁，命令自然落空）。判空调用：
    /// 单机（无 Instance）静默</summary>
    public void RegisterRemoteAvatar(ISyncHost tank)
    {
        avatar = tank;
    }

    // ---------- Host：迷宫参数广播（迷宫同构） ----------

    // MapSpawner 每张迷宫生成完成（首局 Awake / RegenerateRandom 重开）都会进来：
    //   缓存参数供 accept 补发 + 立即广播给已在线的客户端
    void OnMapGenerated(int cols, int rows, int seed, float loopMin, float loopMax)
    {
        latestMapParams = new MapParamsData
        {
            cols = cols, rows = rows, seed = seed,
            loopMin = loopMin, loopMax = loopMax,
        };
        BroadcastMapParams();
    }

    // 向所有已接入连接广播迷宫参数。可靠性按数据类型选：快照丢一帧下条全量
    // 纠正（不可靠），迷宫参数错一帧 = 整局世界错（一次性状态）→ 走 reliable
    void BroadcastMapParams()
    {
        // Awake 时序防御：首局 MapSpawner（执行序 10）生成早于本组件 Start
        // （driver/serverConns 创建）——此时只能缓存，accept 补发兜住首局参数
        if (!driver.IsCreated || !serverConns.IsCreated || !latestMapParams.HasValue)
        {
            return;
        }
        var p = latestMapParams.Value;
        const int payloadSize = 21;
        for (int i = 0; i < serverConns.Length; i++)
        {
            if (!serverConns[i].IsCreated)
            {
                continue;
            }
            // 与 Client 发 Fire 同形态：带管道四参 BeginSend + 错误码判断 + EndSend。
            // 发送失败不重试：下一触发点（重开/新连接补发）会再覆盖，无累积错误
            int err = driver.BeginSend(reliable, serverConns[i], out var w, payloadSize);
            if (err == 0)
            {
                MapParamsProtocol.WriteMapParams(ref w, p);
                driver.EndSend(w);
            }
        }
    }

    // ---------- Host：对局事件广播（比分/暂停） ----------

    // 三条发送 API 由玩法/UI 层调用（GameManager 判局末/开局、PauseController
    // 开/关暂停），判空调用即可——单机无本组件（Mode!=Online 休眠不设 Instance）
    // 自然静默。可靠性：一次性裁决丢不得 → reliable。
    // 写法照 BroadcastMapParams 直接循环（DataStreamWriter 是 ref struct，
    // 不能进 lambda/闭包，重复三次换可读性）

    // 结算特写机位随 RoundEnd 下发：camPoint 为 host 相机将 Focus 的阵亡点
    //（host 判定，client 只执行——FixedMapCamera.Focus 盯点）
    public void HostSendRoundEnd(int winner, int s1, int s2, bool hasCamPoint, float camX, float camZ)
    {
        if (!IsHost || !driver.IsCreated || !serverConns.IsCreated)
        {
            return;
        }
        for (int i = 0; i < serverConns.Length; i++)
        {
            if (!serverConns[i].IsCreated)
            {
                continue;
            }
            int err = driver.BeginSend(reliable, serverConns[i], out var w, RoundProtocol.RoundEndSize);
            if (err == 0)
            {
                RoundProtocol.WriteRoundEnd(ref w, winner, s1, s2, hasCamPoint, camX, camZ);
                driver.EndSend(w);
            }
        }
    }

    public void HostSendRoundStart(int s1, int s2)
    {
        if (!IsHost || !driver.IsCreated || !serverConns.IsCreated)
        {
            return;
        }
        for (int i = 0; i < serverConns.Length; i++)
        {
            if (!serverConns[i].IsCreated)
            {
                continue;
            }
            int err = driver.BeginSend(reliable, serverConns[i], out var w, RoundProtocol.RoundStartSize);
            if (err == 0)
            {
                RoundProtocol.WriteRoundStart(ref w, s1, s2);
                driver.EndSend(w);
            }
        }
    }

    public void HostSendPause(bool paused)
    {
        if (!IsHost || !driver.IsCreated || !serverConns.IsCreated)
        {
            return;
        }
        for (int i = 0; i < serverConns.Length; i++)
        {
            if (!serverConns[i].IsCreated)
            {
                continue;
            }
            int err = driver.BeginSend(reliable, serverConns[i], out var w, RoundProtocol.PauseSize);
            if (err == 0)
            {
                RoundProtocol.WritePause(ref w, paused);
                driver.EndSend(w);
            }
        }
    }

    // host 结束会话（回主菜单前调用）：client 收到立即回菜单。
    // 注意 driver 发包在下一次 ScheduleUpdate 才真正送出——调用方（PauseController）
    // 必须延迟一小段时间再切场景，否则同帧销毁 driver 会把消息吞掉
    public void HostSendSessionEnd()
    {
        if (!IsHost || !driver.IsCreated || !serverConns.IsCreated)
        {
            return;
        }
        for (int i = 0; i < serverConns.Length; i++)
        {
            if (!serverConns[i].IsCreated)
            {
                continue;
            }
            int err = driver.BeginSend(reliable, serverConns[i], out var w, RoundProtocol.SessionEndSize);
            if (err == 0)
            {
                RoundProtocol.WriteSessionEnd(ref w, RoundProtocol.SessionEndToMenu);
                driver.EndSend(w);
            }
        }
    }

    // ---------- Host：实体注册表（Spawn/Despawn + 采集源） ----------

    // NetSyncEntity.OnEnable 调：进注册表（每 tick 采集进帧）+ 广播 Spawn。
    // driver 未建（首局 GameManager.Start 生成坦克可能早于本组件 Start 建 driver）
    // 时只入表不发送——client 连上时 accept 补发遍历表逐个补 Spawn（与迷宫参数同款时序处理）
    public bool HostRegisterEntity(NetSyncEntity e)
    {
        if (!IsHost)
        {
            return false;
        }
        if (entities.Contains(e))
        {
            return false;
        }
        byte? id = AllocId();
        if (id == null)
        {
            return false;
        }
        e.SyncId = id.Value;
        entities.Add(e);
        BroadcastSpawn(e);
        return true;
    }

    // NetSyncEntity.OnDisable 调：出表 + 归还 id + 广播 Despawn。
    // 池化子弹 InitPool 的 Instantiate→SetActive(false) 会触发"注册后立即注销"
    // 一次（瞬时噪音，client 端壳无姿态在视野外销毁，无视觉影响——不为它做抑制）
    // despawn reason 跟随击杀标记：Die 走过的实体 → client 放爆炸；清场/池化 → 静默
    public void HostUnregisterEntity(NetSyncEntity e)
    {
        if (!IsHost)
        {
            return;
        }
        if (!entities.Remove(e))
        {
            return; // 非注册态注销（含池化瞬时注册又停用的竞态边缘）静默
        }
        idUsed[e.SyncId] = false;
        byte reason = e.Killed ? EntityProtocol.DespawnKilled : EntityProtocol.DespawnNormal;
        BroadcastDespawn(e.SyncId, reason);
    }

    byte? AllocId()
    {
        for (int i = 0; i < idUsed.Length; i++)
        {
            if (!idUsed[i])
            {
                idUsed[i] = true;
                return (byte)i;
            }
        }
        return null;
    }

    void BroadcastSpawn(NetSyncEntity e)
    {
        if (!driver.IsCreated)
        {
            return; // 首局竞态：driver 未建发不了，accept 补发兜底
        }
        for (int i = 0; i < serverConns.Length; i++)
        {
            SendSpawnTo(serverConns[i], e);
        }
    }

    void BroadcastDespawn(byte id, byte reason)
    {
        if (!driver.IsCreated)
        {
            return; // 场景卸载边缘：driver 已销毁发不了（client 壳随场景也没了）
        }
        for (int i = 0; i < serverConns.Length; i++)
        {
            if (!serverConns[i].IsCreated)
            {
                continue;
            }
            // 一次性事件丢不得 → reliable
            int err = driver.BeginSend(reliable, serverConns[i], out var w, EntityProtocol.DespawnSize);
            if (err == 0)
            {
                EntityProtocol.WriteDespawn(ref w, id, reason);
                driver.EndSend(w);
            }
        }
    }

    // 单条连接的 Spawn 发送（BroadcastSpawn 与 accept 补发共用）
    void SendSpawnTo(NetworkConnection conn, NetSyncEntity e)
    {
        if (!conn.IsCreated)
        {
            return;
        }
        var (x, z, yaw) = e.Pose();
        int err = driver.BeginSend(reliable, conn, out var w, EntityProtocol.SpawnSize);
        if (err == 0)
        {
            EntityProtocol.WriteSpawn(ref w, e.SyncId, e.TypeKey, x, z, yaw);
            driver.EndSend(w);
        }
    }

    // ---------- Client：接收快照 → 推给同步对象 ----------

    void ClientUpdate()
    {
        if (!connection.IsCreated)
        {
            return; // 未连接/已断开：不消费陈旧句柄的事件（历史上空 Data 给无效 reader 就崩在这）
        }
        NetworkEvent.Type cmd;
        while ((cmd = connection.PopEvent(driver, out var stream)) != NetworkEvent.Type.Empty)
        {
            if (cmd == NetworkEvent.Type.Connect)
            {
                connected = true;
            }
            else if (cmd == NetworkEvent.Type.Data)
            {
                // Data 读取照抄 MinimalBroadcast：事件一到立即从 stream 读内容
                // （先看长度再逐字段读，不做任何前置状态访问）。
                // type 分派：DataStreamReader 按值拷贝共享底层 buffer 但游标独立——
                // probe 只读 1B type，switch 后把未消费的原始 stream 交给对应
                // 协议解析（各 Try 函数自带 type 校验，风格统一）
                // 空包防御先行：Length 0 直接丢（阶段 1 空包 NRE 的历史根因场景）
                var probe = stream;
                if (probe.Length == 0)
                {
                    break;
                }
                switch (probe.ReadByte())
                {
                    case SnapshotProtocol.TypeSnapshot:
                        HandleFrame(stream);
                        break;
                    case MapParamsProtocol.TypeMapParams:
                        if (MapParamsProtocol.TryReadMapParams(stream, out var mapParams))
                        {
                            RebuildMaze(mapParams);
                        }
                        break;
                    case EntityProtocol.TypeSpawn:
                        if (EntityProtocol.TryReadSpawn(stream, out var sId, out var typeKey,
                                out var sX, out var sZ, out var sYaw))
                        {
                            HandleSpawn(sId, typeKey, sX, sZ, sYaw);
                        }
                        break;
                    case EntityProtocol.TypeDespawn:
                        if (EntityProtocol.TryReadDespawn(stream, out var dId, out var dReason))
                        {
                            HandleDespawn(dId, dReason);
                        }
                        break;
                    case RoundProtocol.TypeRoundEnd:
                        if (RoundProtocol.TryReadRoundEnd(stream, out _, out var rs1, out var rs2,
                                out var hasCam, out var camX, out var camZ))
                        {
                            ScoreSynced?.Invoke(rs1, rs2);
                            ApplyRoundEndCam(hasCam, camX, camZ);
                        }
                        break;
                    case RoundProtocol.TypeRoundStart:
                        if (RoundProtocol.TryReadRoundStart(stream, out var s1, out var s2))
                        {
                            ScoreSynced?.Invoke(s1, s2);
                            ApplyRoundStartCam();
                        }
                        break;
                    case RoundProtocol.TypePause:
                        if (RoundProtocol.TryReadPause(stream, out var paused))
                        {
                            PauseSynced?.Invoke(paused);
                        }
                        break;
                    case RoundProtocol.TypeSessionEnd:
                        if (RoundProtocol.TryReadSessionEnd(stream, out _))
                        {
                            SessionEnded?.Invoke();
                        }
                        break;
                }
            }
            else if (cmd == NetworkEvent.Type.Disconnect)
            {
                connected = false;
                connection = default;
                SessionEnded?.Invoke(); // 兜底：host 强退时 client 也回菜单，不卡在冻结画面
            }
        }
        PruneShells();  // 超时兜底必须每帧跑（事件循环无事件时也要清残壳）
    }

    // ---------- Client：迷宫重建（迷宫同构） ----------

    // 收到 host 的决策层参数 → 本地执行层用同一组参数生成 → 两端几何一致。
    // 重复调用安全：MapSpawner.Generate 自带旧墙清理 + 网格推迟重建（hadOldWalls
    // 路径）；覆盖的是 Awake 时自随机的首张图（联调期不抑制，闪变一帧无碍）
    void RebuildMaze(in MapParamsData p)
    {
        if (clientMapSpawner == null)
        {
            clientMapSpawner = FindAnyObjectByType<MapSpawner>();
        }
        if (clientMapSpawner == null)
        {
            return;
        }
        clientMapSpawner.Generate(p.cols, p.rows, p.seed, p.loopMin, p.loopMax);
    }

    // ---------- Client：实体帧分发 / 壳生成与销毁 ----------

    // 实体帧：读头一次 + count 个实体条目。槽存在 → 推进插值并刷新 lastSeen；
    // 槽缺失（开局 spawn(可靠)与首帧(不可靠)跨管道存在乱序窗口，帧可能先到）→
    // 静默丢本对象——spawn 可靠必达，壳建好后从后续帧起播，开局几帧无碍
    void HandleFrame(Unity.Collections.DataStreamReader stream)
    {
        if (!SnapshotProtocol.TryReadFrameHeader(ref stream, out var seq, out var hostTime, out var count))
        {
            return;
        }
        for (int i = 0; i < count; i++)
        {
            if (!SnapshotProtocol.TryReadEntity(ref stream, out var id, out var x, out var z, out var yaw, out var power))
            {
                break; // 截断帧：已读的照常推进，剩余丢弃（不炸，长度防御纪律）
            }
            if (slots.TryGetValue(id, out var slot))
            {
                slot.shell.Push(seq, hostTime, x, z, yaw, power);
                slot.lastSeen = Time.unscaledTime;
                slots[id] = slot;
            }
        }
    }

    // Spawn：host 世界对象出生 → 按 typeKey 实例化对应壳 → 先摆出生姿态
    // （Prime 立即可见，不等插值攒够两帧）→ 绑定 id 进槽表
    void HandleSpawn(byte id, byte typeKey, float x, float z, float yaw)
    {
        if (slots.ContainsKey(id))
        {
            return;
        }
        if (shellPrefabs == null || typeKey >= shellPrefabs.Length || shellPrefabs[typeKey] == null)
        {
            return;
        }
        SnapshotPlayer shell = Instantiate(shellPrefabs[typeKey],
            shellRoot != null ? shellRoot : transform);
        shell.Prime(x, z, yaw);
        shell.Bind(id);
        slots[id] = new ShellSlot { shell = shell, lastSeen = Time.unscaledTime };
    }

    void HandleDespawn(byte id, byte reason)
    {
        if (!slots.TryGetValue(id, out var slot))
        {
            return; // 重复/未知 despawn 静默（壳可能已被超时清理或从未 Spawn）
        }
        slots.Remove(id);
        if (slot.shell == null)
        {
            return;
        }
        // 击杀演出跟随 despawn reason：被击杀（host Die 标记）才放爆炸；
        // 换局清场/池化 reason=普通 → 静默删壳（与 host 死因分流一致，无清场烟花）。
        // 爆炸 prefab 按壳配对（SnapshotPlayer.explosionPrefab，阵营分色在壳资产上）
        if (reason == EntityProtocol.DespawnKilled)
        {
            var boom = slot.shell.ExplosionPrefab;
            if (boom != null)
            {
                Instantiate(boom, slot.shell.transform.position, Quaternion.identity);
            }
            AudioManager.PlayCrash(); // client 端爆炸音（静态判空：无 AudioManager 静默）
        }
        Destroy(slot.shell.gameObject);
    }

    // 超时兜底：壳长时间无快照（断线/despawn 丢失的极端）则删——despawn 走可靠
    // 管道几乎不丢，此逻辑防残壳堆积。每帧调用（壳少，遍历开销可忽略）
    void PruneShells()
    {
        if (slots.Count == 0)
        {
            return;
        }
        float now = Time.unscaledTime;
        List<byte> expired = null;
        foreach (var kv in slots)
        {
            if (now - kv.Value.lastSeen > shellTimeout)
            {
                if (expired == null)
                {
                    expired = new List<byte>(4);
                }
                expired.Add(kv.Key);
            }
        }
        if (expired == null)
        {
            return;
        }
        foreach (byte id in expired)
        {
            HandleDespawn(id, EntityProtocol.DespawnNormal); // 本地超时清理：无击杀演出
        }
    }

    // ---------- Client：结算特写机位执行（host 裁决，client 复刻） ----------

    // RoundEnd 到达：host 相机正 Focus 阵亡点（FixedMapCamera.Focus 内部 y=45 固定，
    // 传 xz 即可）。相机缓动走 unscaledDeltaTime，host 慢放下两端镜头同速趋近。
    // 无机位点（camFlag=0，host 自己也没特写）→ 不动相机
    void ApplyRoundEndCam(bool hasCamPoint, float camX, float camZ)
    {
        if (!hasCamPoint)
        {
            return;
        }
        var cam = FindClientCam();
        if (cam != null)
        {
            cam.Focus(new Vector3(camX, 0f, camZ));
        }
    }

    // RoundStart 到达：host 新局回全图 → client 同步回（清掉特写状态）
    void ApplyRoundStartCam()
    {
        var cam = FindClientCam();
        if (cam != null)
        {
            cam.WatchFullMap();
        }
    }

    FixedMapCamera FindClientCam()
    {
        if (clientCam == null)
        {
            clientCam = FindAnyObjectByType<FixedMapCamera>();
        }
        return clientCam; // 场景没摆相机：静默（UI 侧也可缺）
    }

    // 上行命令唯一发送入口（输入源 = 临时键盘模拟器，将来 = 远端坦克真实输入适配器，
    // 产生方不关心管道——按 kind 在这选：Move=不可靠状态流，Fire=可靠事件流）
    public bool SendCommand(in CommandData cmd)
    {
        if (!connection.IsCreated || !connected)
        {
            return false; // 握手未完成：可靠发送依赖连接状态机（MinimalReliable 验证结论）
        }
        if (cmd.kind == CommandKind.Move)
        {
            // 三参 BeginSend = 默认不可靠管道，与快照广播同通道语义
            int err = driver.BeginSend(connection, out var w, CommandProtocol.MoveSize);
            if (err != 0)
            {
                return false; // 失败静默跳过：下一条节拍会再试，状态流天然容错
            }
            CommandProtocol.WriteMove(ref w, cmd.seq, cmd.moveX, cmd.moveY);
            driver.EndSend(w);
            return true;
        }
        if (cmd.kind == CommandKind.Fire)
        {
            int err = driver.BeginSend(reliable, connection, out var w, CommandProtocol.FireSize);
            if (err != 0)
            {
                return false;
            }
            CommandProtocol.WriteFire(ref w, cmd.seq);
            driver.EndSend(w);
            return true;
        }
        return false;
    }
}
