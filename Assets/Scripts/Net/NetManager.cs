using UnityEngine;
using Unity.Collections;
using Unity.Networking.Transport;

public enum NetRole { Host, Client }

// 阶段 1 传输管理：一个脚本管 Host/Client 两种角色。
//
// 模型：主机权威 + 状态同步。
//   Host   = 世界模拟本体：每 snapshotInterval 采集 syncObject 姿态 → 广播快照
//            （默认不可靠管道：快照自描述，丢一帧下帧全量纠正，无需重传）
//   Client = 快照播放器：收快照 → 推给 syncObject 的插值缓冲 → 平滑渲染
//
// 重写背景：旧实现长期"握手成功但 Data 空载荷 NRE"，与同场景下验证通过的
// MinimalBroadcast 对照无法定位差异，决定整体重写——内部结构严格照抄
// MinimalBroadcast.cs 的已验证写法（NetworkDriver.Create() 无参 / 初始化在
// Start / Accept while 循环 + NativeList 存连接 / Client PopEvent 循环 /
// BeginSend 三参+错误码判断+EndSend），不再引入它没有的任何模式。
// 唯一有意的偏离：心跳计时放在每帧调用路径上累计（照抄会把 logTimer 放进
// tick 段内——历史 bug：非 tick 帧不累计，18 秒才打印一次）。
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
    [Tooltip("快照间隔（秒）：20Hz = 0.05")]
    [SerializeField, Range(0.01f, 0.2f)] private float snapshotInterval = 0.05f;
    [Tooltip("同步对象（Host 采集 / Client 接收推送）")]
    [SerializeField] private NetSyncObject syncObject;

    public bool IsHost => role == NetRole.Host;

    private NetworkDriver driver;
    private NativeList<NetworkConnection> serverConns; // Host：所有已接入客户端（不设单连接槽）
    private NetworkConnection connection;              // Client：到主机的连接
    private float tickTimer;                           // 距上次广播的累计时间
    private float logTimer;                            // 心跳日志累计时间
    private uint seq;                                  // 主机发帧计数（进快照载荷，调试/统计用）
    private int sentCount;                             // Host：本心跳周期内成功广播包数
    private int recvCount;                             // Client：本心跳周期内收到快照数

    void Awake()
    {
        Instance = this;
    }

    // 创建/绑定/连接放 Start 而非 Awake（与已验证的 MinimalBroadcast 对齐）：
    // Awake 阶段场景仍在加载，重操作可能产生大卡顿帧，连接建立时机不稳定
    void Start()
    {
        driver = NetworkDriver.Create();
        if (IsHost)
        {
            // 先建连接列表再 bind：bind 失败（端口被占）也要能安全走完生命周期
            serverConns = new NativeList<NetworkConnection>(4, Allocator.Persistent);
            if (driver.Bind(NetworkEndpoint.AnyIpv4.WithPort(port)) != 0)
            {
                Debug.LogError($"[Net] Bind 失败：端口 {port} 可能被其它进程占用，换个端口试");
                return;
            }
            driver.Listen();
            Debug.Log($"[Net] 主机监听 :{port}");
        }
        else
        {
            // 客户端不 bind——内核自动分配临时端口，握手包带源端口出发。
            // Parse(ip, port) 连接路径经 MinimalBroadcast 验证可工作
            connection = driver.Connect(NetworkEndpoint.Parse(serverIp, port));
            Debug.Log($"[Net] 客户端连接 {serverIp}:{port}");
        }
    }

    void OnDestroy()
    {
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
        // 新连接轮询：UTP 2.x 握手由驱动内部完成，Accept 取出已就绪连接。
        // while 一次取完本帧全部新连接并全收进列表——单连接槽会丢后续客户端
        NetworkConnection c;
        while ((c = driver.Accept()) != default)
        {
            serverConns.Add(c);
            Debug.Log($"[Net] 客户端接入：{c}");
        }

        // 固定节拍广播（unscaled：暂停菜单/慢放不影响网络节奏）
        tickTimer += Time.unscaledDeltaTime;
        if (tickTimer >= snapshotInterval)
        {
            tickTimer = 0f;
            if (syncObject != null)
            {
                // 本 tick 只采一次姿态，向所有客户端广播同一份快照
                var (x, z, yaw) = syncObject.CurrentPose();
                var snap = new SnapshotData
                {
                    seq = seq++,
                    // 时间戳必须与广播节拍同基准：节拍走 unscaledDeltaTime（真实
                    // 20Hz，不受慢放/暂停影响），hostTime 就用 unscaled——否则慢放
                    // （timeScale=0.12）期间快照真实间隔 50ms 但时间戳只前进 6ms，
                    // 客户端插值进度按时间戳差推进会失真
                    hostTime = Time.unscaledTime,
                    x = x,
                    z = z,
                    yaw = yaw,
                };
                const int payloadSize = 21; // type(1)+seq(4)+hostTime(4)+x/z/yaw(12)
                for (int i = 0; i < serverConns.Length; i++)
                {
                    if (!serverConns[i].IsCreated)
                    {
                        continue; // 失效连接跳过（不 Poll 连接事件，断开由发送错误码体现）
                    }
                    // 与 MinimalBroadcast 完全同形态：三参 BeginSend + 错误码判断 + EndSend。
                    // 发送失败静默跳过：快照自描述，对端下一条全量纠正，无需重传
                    int err = driver.BeginSend(serverConns[i], out var w, payloadSize);
                    if (err == 0)
                    {
                        SnapshotProtocol.WriteSnapshot(ref w, snap);
                        driver.EndSend(w);
                        sentCount++;
                    }
                }
            }
            // syncObject 未拖引用：无姿态可采，本 tick 跳过发送（tick 节奏照常）
        }

        // 心跳：必须放每帧路径上累计（不能在 tick 段内——非 tick 帧不累计会失真）
        LogHeartbeat();
    }

    // 心跳：每秒报一次收发计数（联调定位用：计数不动 = 断在哪一段一目了然）。
    // 由 Host/Client 的每帧路径调用，内部按真实时间累计，正好每秒打印一次
    void LogHeartbeat()
    {
        logTimer += Time.unscaledDeltaTime;
        if (logTimer < 1f)
        {
            return;
        }
        logTimer = 0f;
        if (IsHost)
        {
            Debug.Log($"[Net][Host] 每秒广播 {sentCount} 包（已接入 {serverConns.Length} 客户端）");
            sentCount = 0;
        }
        else
        {
            Debug.Log($"[Net][Client] 每秒收到 {recvCount} 包，syncObject {(syncObject == null ? "未拖引用!" : "已挂")}");
            recvCount = 0;
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
                Debug.Log("[Net] 已连接主机");
            }
            else if (cmd == NetworkEvent.Type.Data)
            {
                // Data 读取照抄 MinimalBroadcast：事件一到立即从 stream 读内容
                // （TryReadSnapshot 先看长度再逐字段读，不做任何前置状态访问）
                if (SnapshotProtocol.TryReadSnapshot(stream, out var snap))
                {
                    recvCount++;
                    syncObject?.PushSnapshot(snap);
                }
            }
            else if (cmd == NetworkEvent.Type.Disconnect)
            {
                Debug.Log("[Net] 与主机断开");
                connection = default;
            }
        }
        LogHeartbeat(); // 每帧路径（连接建立后）
    }
}
