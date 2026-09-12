using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

// host 侧局域网信标：每秒向 255.255.255.255:7781 广播公告包，并在独立端口
// 7782 接收 client 的加入请求（等待阶段，OnlineMenu 用来显示"谁在等"）。
//
// 为什么收发用两个 socket：公告是广播（多收件人不挑 socket），join 是单播——
// 单播到"一机多 socket bind 同端口"时投递归属不确定（会被同机其它 7781 socket
// 抢收）。发送 socket 不 bind（源端口随机，接收端按源地址取 IP 不依赖端口），
// 接收 socket 独占 7782 → 每端口每机一个 socket，单播必达
//
// 挂两处（字段 playing 区分阶段）：
//   Menu 场景（playing=false，等待中）：默认 inactive，OnlineMenu 进入等待时激活
//   Main 场景（playing=true，游戏中）：常驻，OnEnable 自判"联机 host"否则自禁用
//     ——单机 / 客户端进程不会误广播
public class LanBeacon : MonoBehaviour
{
    [Tooltip("公告状态：false=菜单等待中，true=游戏中（可中途加入）")]
    [SerializeField] private bool playing;

    /// <summary>收到加入请求（按来源 IP 去重后触发一次）：(ip, 玩家名)</summary>
    public event System.Action<string, string> PlayerJoined;

    private UdpClient sendSock;   // 只发公告（不 bind）
    private UdpClient recvSock;   // 只收 join（bind 7782）
    private float sendTimer;
    private readonly Dictionary<string, string> joined = new Dictionary<string, string>();
    private IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);

    void OnEnable()
    {
        // 游戏中（Main 场景实例）只做联机 host 的信标；单机 / 非 host 不广播
        if (playing && (NetManager.Instance == null || !NetManager.Instance.IsHost))
        {
            enabled = false;
            return;
        }
        try
        {
            sendSock = new UdpClient(); // 不 bind：内核分配源端口
            sendSock.EnableBroadcast = true;

            recvSock = new UdpClient();
            // ReuseAddress 防御性保留：真机正常无冲突，双 Editor 里 host 进程可能
            // 与别的实例并存（如两个 Editor 都进过等待态）
            recvSock.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            recvSock.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryProtocol.JoinPort));
            recvSock.Client.Blocking = false;
        }
        catch (SocketException)
        {
            Cleanup();
            enabled = false;
        }
    }

    void OnDisable()
    {
        Cleanup();
        joined.Clear();
    }

    void Cleanup()
    {
        sendSock?.Close();
        sendSock = null;
        recvSock?.Close();
        recvSock = null;
    }

    void Update()
    {
        if (sendSock == null || recvSock == null)
        {
            return;
        }

        // 1) 收 join（等待阶段显示玩家列表用；游戏中收到也无害，无人订阅即可）
        while (recvSock.Available > 0)
        {
            byte[] data;
            try
            {
                data = recvSock.Receive(ref remote);
            }
            catch (SocketException)
            {
                break; // 非阻塞竞态/网络抖动：本帧放弃剩余
            }
            if (DiscoveryProtocol.TryParseJoin(data, data.Length, out string name))
            {
                string ip = remote.Address.ToString();
                if (!joined.ContainsKey(ip))
                {
                    joined[ip] = name;
                    PlayerJoined?.Invoke(ip, name);
                }
            }
        }

        // 2) 每秒广播一次公告（unscaled：菜单 timeScale 恒 1，但保持统一纪律）
        sendTimer += Time.unscaledDeltaTime;
        if (sendTimer < 1f)
        {
            return;
        }
        sendTimer = 0f;
        // 游戏端口随公告下发：等待态还没监听（0）；游戏态播 NetManager 实际端口——
        // 它是"优先 7783、被占则 OS 分配"，实际值只有 NetManager 知道。
        // bind 全部失败（未监听）时不播：房间里"可加入"不能是假的
        ushort gamePort = 0;
        if (playing)
        {
            if (NetManager.Instance == null || !NetManager.Instance.IsListening)
            {
                return;
            }
            gamePort = NetManager.Instance.ActualPort;
        }
        byte[] packet = DiscoveryProtocol.BuildAnnounce(playing, gamePort, GameConfig.RoomName, GameConfig.PlayerName);
        try
        {
            sendSock.Send(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryProtocol.Port));
        }
        catch (SocketException)
        {
            // 广播失败（网卡瞬断等）：下个周期再试，静默
        }
    }
}
