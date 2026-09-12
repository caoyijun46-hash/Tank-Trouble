using System.Net;
using System.Net.Sockets;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

// 联机菜单总控（挂 Menu 场景）：局域网发现流程的 UI 与状态机。
//
//   host 流程：Online → Host（读名字、点亮 LanBeacon 等待广播、显示玩家列表）
//             → Start（进 Main，NetManager 开始监听——优先 7783，被占则由 OS 分配）
//   client 流程：Online → 房间列表（LanDiscovery 刷新）→ 点条目：
//             state=游戏中 → 用房间信息里的实际端口直接进 Client 场景连接
//             state=等待中 → 每秒单播 join 请求（host 列表显示"谁在等"），
//                            轮询该 host 变"游戏中"后自动进 Client；30s 超时回列表
//
// 端口来源：游戏端口不写死——host 的端口在公告包里下发（DiscoveryProtocol v2），
// 这里从房间信息取（GameConfig.ServerPort）；手输直连没有房间信息，用默认端口。
// 为什么等待态用"轮询状态"而不是直接连接：host 菜单阶段没有游戏监听（监听由
// Main 场景的 NetManager 建立，且场景切换会重建连接）——菜单只负责"预告"，
// 真正的连接等 host 进游戏后再建立
public class OnlineMenu : MonoBehaviour
{
    [Header("面板")]
    [SerializeField] private GameObject onlinePanel; // Online 按钮展开的面板
    [SerializeField] private GameObject hostPanel;   // Host 按钮展开的子面板（等待中）

    [Header("输入")]
    [SerializeField] private TMP_InputField roomNameInput;   // 房间名（host 广播）
    [SerializeField] private TMP_InputField playerNameInput; // 玩家名（广播展示 + join 携带）

    [Header("列表（Room.prefab / Player.prefab 实例化到 Content）")]
    [SerializeField] private RectTransform roomListContent;
    [SerializeField] private RectTransform playerListContent;
    [SerializeField] private GameObject roomItemPrefab;
    [SerializeField] private GameObject playerItemPrefab;

    [Header("组件引用")]
    [SerializeField] private LanBeacon menuBeacon;   // Menu 场景信标（默认 inactive）
    [SerializeField] private LanDiscovery discovery;

    [Header("场景名")]
    [SerializeField] private string mainScene = "Main";
    [SerializeField] private string clientScene = "Client";

    private UdpClient joinSock;      // 等待房主开始期间的单播 socket（懒建）
    private string waitingIp;        // 等待中的 host IP（空 = 不在等待）
    private float waitTimer;
    private float joinResendTimer;
    private const float WaitTimeout = 30f;

    // 被点击的条目（等待期改文本提示；列表在等待期冻结不刷新）
    private Button waitingButton;
    private TMP_Text waitingText;

    void Start()
    {
        if (discovery != null)
        {
            discovery.RoomsChanged += RefreshRooms;
            RefreshRooms();
        }
        if (menuBeacon != null)
        {
            menuBeacon.PlayerJoined += OnPlayerJoined;
        }
        if (hostPanel != null)
        {
            hostPanel.SetActive(false);
        }
    }

    void OnDestroy()
    {
        if (discovery != null)
        {
            discovery.RoomsChanged -= RefreshRooms;
        }
        if (menuBeacon != null)
        {
            menuBeacon.PlayerJoined -= OnPlayerJoined;
        }
        CloseJoinSock();
    }

    void Update()
    {
        if (string.IsNullOrEmpty(waitingIp) || discovery == null)
        {
            return;
        }
        // 目标变"游戏中" → 用当前公告里的实际端口连接
        if (discovery.TryGetRoom(waitingIp, out var room) && room.playing)
        {
            ConnectAndLoad(waitingIp, room.port);
            return;
        }
        waitTimer += Time.unscaledDeltaTime;
        if (waitTimer > WaitTimeout)
        {
            Debug.Log("[OnlineMenu] 等待房主开始超时，回到房间列表");
            ExitWaiting();
            RefreshRooms();
            return;
        }
        // 每秒重发 join（丢包容忍；host 按 IP 去重，不会重复显示）
        joinResendTimer += Time.unscaledDeltaTime;
        if (joinResendTimer >= 1f)
        {
            joinResendTimer = 0f;
            SendJoin();
        }
    }

    // ---------- 按钮入口（场景里接 onClick） ----------

    public void OnClickOnline()
    {
        if (onlinePanel != null)
        {
            onlinePanel.SetActive(true);
        }
    }

    public void OnClickHost()
    {
        GameConfig.RoomName = roomNameInput != null ? roomNameInput.text : "";
        GameConfig.PlayerName = playerNameInput != null ? playerNameInput.text : "";
        if (playerListContent != null)
        {
            ClearList(playerListContent); // 新等待会话：清掉上次的玩家列表
        }
        if (menuBeacon != null)
        {
            menuBeacon.gameObject.SetActive(true); // 等待态开始广播（含收 join）
        }
        if (hostPanel != null)
        {
            hostPanel.SetActive(true);
        }
    }

    public void StartHost()
    {
        GameConfig.Mode = GameMode.Online; // 显式设置：防菜单里跑过 VsAI/Double 的残留
        SceneManager.LoadScene(mainScene);
    }

    /// <summary>Online Panel 的 Exit Online：收起联机面板；若正在等待（client 等房主/
    /// host 等玩家）一并取消，不留后台广播或挂起的等待态</summary>
    public void ExitOnline()
    {
        CancelWaiting();
        if (hostPanel != null)
        {
            hostPanel.SetActive(false);
        }
        if (onlinePanel != null)
        {
            onlinePanel.SetActive(false);
        }
    }

    /// <summary>Host Panel 的 Exit Host：取消开房等待（停广播、清玩家列表），回 Online Panel</summary>
    public void ExitHost()
    {
        CancelWaiting();
        if (hostPanel != null)
        {
            hostPanel.SetActive(false);
        }
    }

    /// <summary>手输 IP 直连（备用入口，可另接一个按钮）。端口 0 = 用场景序列化
    /// 默认端口——没有房间信息可拿，host 若被占用退到了 OS 分配则此路连不上（可接受）</summary>
    public void JoinManualIp(string ip)
    {
        if (!string.IsNullOrEmpty(ip))
        {
            ConnectAndLoad(ip, 0);
        }
    }

    // ---------- host：等待玩家列表 ----------

    void OnPlayerJoined(string ip, string playerName)
    {
        if (playerItemPrefab == null || playerListContent == null)
        {
            return;
        }
        var item = Instantiate(playerItemPrefab, playerListContent);
        if (item.GetComponentInChildren<TMP_Text>() is { } label)
        {
            label.text = string.IsNullOrEmpty(playerName) ? ip : playerName;
        }
    }

    // ---------- client：房间列表 ----------

    void RefreshRooms()
    {
        if (roomListContent == null || roomItemPrefab == null || discovery == null)
        {
            return;
        }
        if (!string.IsNullOrEmpty(waitingIp))
        {
            return; // 等待房主期间冻结列表（被点条目正作为"等待中"提示用）
        }
        ClearList(roomListContent);
        foreach (var r in discovery.Rooms)
        {
            var item = Instantiate(roomItemPrefab, roomListContent);
            var label = item.GetComponentInChildren<TMP_Text>();
            if (label != null)
            {
                string roomName = string.IsNullOrEmpty(r.roomName) ? "(未命名房间)" : r.roomName;
                label.text = $"{roomName}（{r.hostName}） {(r.playing ? "游戏中" : "等待中")}";
            }
            if (item.GetComponentInChildren<Button>() is { } button)
            {
                string ip = r.ip;
                bool playing = r.playing;
                ushort port = r.port;
                button.onClick.AddListener(() => JoinRoom(ip, playing, port, button, label));
            }
        }
    }

    void JoinRoom(string ip, bool playing, ushort port, Button button, TMP_Text label)
    {
        if (playing)
        {
            ConnectAndLoad(ip, port); // 游戏中：用公告里的实际端口直接进 Client 场景连接
            return;
        }
        // 等待中：进入等待态，复用被点条目作提示（不新增 UI 元素）
        waitingIp = ip;
        waitTimer = 0f;
        joinResendTimer = 0f;
        waitingButton = button;
        waitingText = label;
        if (waitingText != null)
        {
            waitingText.text = "等待房主开始…";
        }
        if (waitingButton != null)
        {
            waitingButton.interactable = false;
        }
        SendJoin(); // 立即发第一包
    }

    void ExitWaiting()
    {
        waitingIp = null;
        waitingButton = null;
        waitingText = null;
        CloseJoinSock();
    }

    // 取消一切"等待中"状态：client 的等待（退等待 + 解冻列表）与 host 的等待
    // （停 Menu 信标广播 + 清玩家列表）。Exit 按钮与将来其他打断路径共用
    void CancelWaiting()
    {
        if (!string.IsNullOrEmpty(waitingIp))
        {
            ExitWaiting();
            RefreshRooms(); // 列表在等待期被冻结，退出后重建
        }
        if (menuBeacon != null && menuBeacon.gameObject.activeSelf)
        {
            menuBeacon.gameObject.SetActive(false); // 停广播（OnDisable 关 socket）
        }
        if (playerListContent != null)
        {
            ClearList(playerListContent);
        }
    }

    void ConnectAndLoad(string ip, ushort port)
    {
        GameConfig.ServerIp = ip;
        GameConfig.ServerPort = port; // 0 = 用场景序列化默认端口（手输直连）
        GameConfig.Mode = GameMode.Online;
        Debug.Log($"[OnlineMenu] 连接 {ip}:{(port == 0 ? "默认端口" : port.ToString())} → {clientScene}");
        SceneManager.LoadScene(clientScene);
    }

    // ---------- join 单播 ----------

    void SendJoin()
    {
        if (string.IsNullOrEmpty(waitingIp))
        {
            return;
        }
        try
        {
            joinSock ??= new UdpClient(); // 发送用：系统分配临时端口，无需 bind
            byte[] packet = DiscoveryProtocol.BuildJoin(GameConfig.PlayerName);
            // join 走独立端口 7782（与公告广播 7781 分离，单播投递才确定）
            joinSock.Send(packet, packet.Length,
                new IPEndPoint(IPAddress.Parse(waitingIp), DiscoveryProtocol.JoinPort));
        }
        catch (SocketException e)
        {
            Debug.LogWarning($"[OnlineMenu] join 发送失败（{waitingIp}）：{e.Message}");
        }
    }

    void CloseJoinSock()
    {
        joinSock?.Close();
        joinSock = null;
    }

    // ---------- 工具 ----------

    // 先脱离父级再 Destroy：Destroy 帧末才生效，直接 Destroy 会让新旧条目
    // 在同一帧并存于布局（视觉重叠一帧）
    static void ClearList(RectTransform content)
    {
        for (int i = content.childCount - 1; i >= 0; i--)
        {
            var child = content.GetChild(i);
            child.SetParent(null);
            Destroy(child.gameObject);
        }
    }
}
