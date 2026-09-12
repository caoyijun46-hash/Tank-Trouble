using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

// client 侧局域网发现：bind 7781 收 host 的公告包，维护房间列表
// （3 秒无更新视为过期——host 每秒广播，掉线即自动消失）。
//
// 房间状态变化（新/消失/名字或阶段变化——含 waiting→playing）都会触发
// RoomsChanged：OnlineMenu 借此刷新列表；"等待房主开始"的 client 借此
// 检测到目标变 playing 后自动连接。
//
// 同机双进程测试与 LanBeacon 一样必须 ReuseAddress（双方都 bind 7781）
public class LanDiscovery : MonoBehaviour
{
    public struct Room
    {
        public string ip;        // 由包的来源地址得到（host 无需自报 IP）
        public string roomName;
        public string hostName;
        public bool playing;     // false=等待中 true=游戏中
        public ushort port;      // 游戏实际端口（公告携带；host 还没监听时为 0）
        public float lastSeen;
    }

    [Tooltip("联机参数（Assets/Config/NetConfig.asset）：房间过期时间在此")]
    [SerializeField] private NetConfig netConfig;
    private float expireSeconds; // OnEnable 从 netConfig 缓存

    /// <summary>房间列表有变化（新增/过期/状态变化）时触发</summary>
    public event System.Action RoomsChanged;

    private UdpClient sock;
    private IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
    private readonly Dictionary<string, Room> rooms = new Dictionary<string, Room>();
    private readonly List<Room> snapshot = new List<Room>();

    /// <summary>当前有效房间快照（按 IP 去重，最新状态）</summary>
    public IReadOnlyList<Room> Rooms => snapshot;

    void OnEnable()
    {
        expireSeconds = netConfig != null ? netConfig.discoveryExpire : 3f; // 兜底仅供缺配不崩
        try
        {
            sock = new UdpClient();
            sock.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            sock.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryProtocol.Port));
            sock.Client.Blocking = false;
        }
        catch (SocketException)
        {
            sock = null;
            enabled = false;
        }
    }

    void OnDisable()
    {
        sock?.Close();
        sock = null;
        rooms.Clear();
        snapshot.Clear();
    }

    void Update()
    {
        if (sock == null)
        {
            return;
        }
        bool changed = false;
        float now = Time.unscaledTime;

        // 收公告（非阻塞轮询，主循环内消费）
        while (sock.Available > 0)
        {
            byte[] data;
            try
            {
                data = sock.Receive(ref remote);
            }
            catch (SocketException)
            {
                break;
            }
            if (!DiscoveryProtocol.TryParseAnnounce(data, data.Length,
                    out bool playing, out ushort gamePort, out string roomName, out string hostName))
            {
                continue;
            }
            string ip = remote.Address.ToString();
            if (rooms.TryGetValue(ip, out var old))
            {
                if (old.playing != playing || old.roomName != roomName || old.hostName != hostName
                    || old.port != gamePort)
                {
                    changed = true;
                }
            }
            else
            {
                changed = true; // 新房间
            }
            rooms[ip] = new Room
            {
                ip = ip,
                roomName = roomName,
                hostName = hostName,
                playing = playing,
                port = gamePort,
                lastSeen = now,
            };
        }

        // 过期清理（host 掉线/退出）
        List<string> dead = null;
        foreach (var kv in rooms)
        {
            if (now - kv.Value.lastSeen > expireSeconds)
            {
                (dead ??= new List<string>()).Add(kv.Key);
            }
        }
        if (dead != null)
        {
            foreach (string ip in dead)
            {
                rooms.Remove(ip);
            }
            changed = true;
        }

        if (changed)
        {
            RebuildSnapshot();
            RoomsChanged?.Invoke();
        }
    }

    void RebuildSnapshot()
    {
        snapshot.Clear();
        foreach (var kv in rooms)
        {
            snapshot.Add(kv.Value);
        }
    }

    /// <summary>取某 IP 房间的当前状态（等待中的 client 轮询用）；不存在返回 false</summary>
    public bool TryGetRoom(string ip, out Room room) => rooms.TryGetValue(ip, out room);
}
