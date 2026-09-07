using UnityEngine;
using Unity.Collections;
using Unity.Networking.Transport;

// 阶段 2 前哨验证：UTP 可靠管道（ReliableSequencedPipelineStage）最小收发闭环。
//
// 为什么需要它：阶段 1 只验证了默认不可靠管道（快照自描述，丢帧下帧全量纠正，
// 不需要重传）。命令上行不行——命令丢一条就是一次操作没了，必须可靠送达。
// UTP 内置可靠管道，但整套 API 还没碰过，先双 Editor 验证清楚再接入真坦克，
// 避免重蹈阶段 1（格式对但按值传 writer 发空包）那种弯路。
//
// 验证什么：
//   1. CreatePipeline / BeginSend(pipe, ...) / 对端 PopEvent 收可靠消息——API 形态
//   2. 可靠语义：Host 收 seq 应严格连续（无丢/重/乱序），违规立即打 Warning
//   3. Host 每条 echo 回 Client（也走可靠管道），Client 对照 已发/已收 echo 是否闭合
//
// 局限（诚实的边界）：127.0.0.1 回环不丢包，"可靠 vs 不可靠"本机表现一样——
// 这里验证的是 API 与顺序语义；真正的丢包重传要等真实网络（手机热点）观察。
//
// 命令载荷 = seq(4B) 单字段：验证顺序足够。真实坦克命令格式（类型/参数）等接入时设计。
// 假设：单一客户端（expectedSeq 以"新连接从 0 开始"为基准，多客户端并测会误报）
public class MinimalReliable : MonoBehaviour
{
    [Header("角色")]
    [SerializeField] private NetRole role = NetRole.Host;

    [Header("连接")]
    [SerializeField] private string serverIp = "127.0.0.1";
    [SerializeField] private ushort port = 7780; // 独立端口：避开 NetManager 的 7778

    [Header("命令")]
    [Tooltip("客户端发送间隔（秒）：0.05 = 20Hz，模拟真实操作频率")]
    [SerializeField, Range(0.05f, 1f)] private float cmdInterval = 0.2f;

    private NetworkDriver driver;
    private NetworkPipeline reliable;                  // ReliableSequenced 管道：命令专用
    private NativeList<NetworkConnection> serverConns; // Host：已接入客户端
    private NetworkConnection connection;              // Client：到主机的连接
    private bool connected;                            // Client：握手完成后才发命令
    private float cmdTimer;                            // Client：距上条命令的累计时间
    private uint sendSeq;                              // Client：下一条命令序号（从 0 起）
    private int sentTotal;                             // Client：累计已发
    private int echoCount;                             // Client：累计收到 echo
    private float logTimer;                            // 心跳累计（放每帧路径——历史 bug 教训）
    private int recvCount;                             // Host：本心跳收命令数
    private uint expectedSeq;                          // Host：下一条应到的序号（连续性校验基准）
    private int seqViolation;                          // Host：序号异常次数（应为 0）

    void Start()
    {
        driver = NetworkDriver.Create();
        // 关键新 API：创建可靠管道。发送方 BeginSend 带 pipe 即走可靠；
        // 接收方无感知——ACK/去重/排序由驱动内部完成，PopEvent 读法同快照
        reliable = driver.CreatePipeline(typeof(ReliableSequencedPipelineStage));

        if (role == NetRole.Host)
        {
            serverConns = new NativeList<NetworkConnection>(4, Allocator.Persistent);
            if (driver.Bind(NetworkEndpoint.AnyIpv4.WithPort(port)) != 0)
            {
                Debug.LogError($"[Reliable] Bind 失败：端口 {port} 被占用，换个端口试");
                return;
            }
            driver.Listen();
            Debug.Log($"[Reliable] 主机监听 :{port}");
        }
        else
        {
            connection = driver.Connect(NetworkEndpoint.Parse(serverIp, port));
            Debug.Log($"[Reliable] 客户端连接 {serverIp}:{port}");
        }
    }

    void OnDestroy()
    {
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
        if (role == NetRole.Host)
        {
            HostUpdate();
        }
        else
        {
            ClientUpdate();
        }
    }

    // ---------- Host：收命令 → 校验连续性 → echo 回客户端 ----------

    void HostUpdate()
    {
        NetworkConnection c;
        while ((c = driver.Accept()) != default)
        {
            // 单客户端假设：新连接 = 新测试会话，序号从 0 重新校验（多客户端会误报）
            expectedSeq = 0;
            serverConns.Add(c);
            Debug.Log($"[Reliable] 客户端接入：{c}");
        }

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
                    if (stream.Length < 4)
                    {
                        continue; // 载荷非法：防御性丢弃（不该发生，发生说明格式写错）
                    }
                    uint seq = stream.ReadUInt();
                    recvCount++;
                    if (seq != expectedSeq)
                    {
                        seqViolation++;
                        Debug.LogWarning($"[Reliable] seq 异常：收到 {seq}，期望 {expectedSeq}（累计 {seqViolation} 次）");
                    }
                    expectedSeq = seq + 1;

                    // echo 同一序号回去：验证可靠管道双向可用
                    int err = driver.BeginSend(reliable, serverConns[i], out var w, 4);
                    if (err == 0)
                    {
                        w.WriteUInt(seq);
                        driver.EndSend(w);
                    }
                }
                else if (evt == NetworkEvent.Type.Disconnect)
                {
                    Debug.Log($"[Reliable] 客户端断开：{serverConns[i]}");
                    serverConns[i] = default; // 标记失效即跳过（幽灵连接问题见交接文档）
                }
            }
        }
        LogHeartbeat();
    }

    // ---------- Client：握手完成后按节拍发命令，收 echo 计数 ----------

    void ClientUpdate()
    {
        if (!connection.IsCreated)
        {
            return;
        }
        NetworkEvent.Type evt;
        while ((evt = connection.PopEvent(driver, out var stream)) != NetworkEvent.Type.Empty)
        {
            if (evt == NetworkEvent.Type.Connect)
            {
                connected = true;
                Debug.Log("[Reliable] 已连接主机，开始按节拍发命令");
            }
            else if (evt == NetworkEvent.Type.Data)
            {
                // echo 载荷 = 主机收下并回传的同一序号；计数即可对照已发/已回
                if (stream.Length >= 4)
                {
                    stream.ReadUInt();
                    echoCount++;
                }
            }
            else if (evt == NetworkEvent.Type.Disconnect)
            {
                connected = false;
                connection = default;
                Debug.Log("[Reliable] 与主机断开");
            }
        }

        // 握手完成才累计命令节拍：连接状态机未就绪时发可靠消息没有意义
        // （确认机制无从建立；这也是真实游戏"进场后才能操作"的映射）
        if (connected)
        {
            cmdTimer += Time.unscaledDeltaTime;
            if (cmdTimer >= cmdInterval)
            {
                cmdTimer = 0f;
                int err = driver.BeginSend(reliable, connection, out var w, 4);
                if (err == 0)
                {
                    w.WriteUInt(sendSeq);
                    driver.EndSend(w);
                    sendSeq++;
                    sentTotal++;
                }
                // 失败静默跳过：下一条节拍会再试，不在单条上纠缠
            }
        }
        LogHeartbeat();
    }

    // 心跳：每秒报一次收发对照。放每帧路径累计（放 tick 段内会失真——历史 bug）
    void LogHeartbeat()
    {
        logTimer += Time.unscaledDeltaTime;
        if (logTimer < 1f)
        {
            return;
        }
        logTimer = 0f;
        if (role == NetRole.Host)
        {
            Debug.Log($"[Reliable][Host] 每秒收 {recvCount} 条命令，seq 连续（违规 {seqViolation} 次）");
            recvCount = 0;
        }
        else
        {
            Debug.Log($"[Reliable][Client] 已发 {sentTotal} 条，收到 echo {echoCount} 条，未闭合 {sentTotal - echoCount}");
        }
    }
}
