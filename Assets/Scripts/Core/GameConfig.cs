// 菜单 → 游戏场景的跨场景配置：静态字段在场景加载之间存活。
// 主菜单按钮先写 Mode 再 LoadScene("Main")，GameManager.SpawnRound 读取。
// 默认 Online：直接 Play Main = 联机 host（当前联机调试期的默认入口；菜单
// Online 按钮后置）。注意 Mode 是静态残留——从菜单跑过 VsAI/Double 后再
// 直接 Play Main 会是单机，需重新 Play 或经菜单进入才回 Online。
public enum GameMode { VsAI = 0, Double = 1, Online = 2 }

public static class GameConfig
{
    public static GameMode Mode = GameMode.Online;

    // ---- 联机（局域网发现流程写入，跨场景存活） ----
    /// <summary>client 要连的 host IP：菜单里从发现列表/手输得到。空 = 用场景 Inspector 默认值</summary>
    public static string ServerIp;

    /// <summary>client 要连的 host 游戏端口：从房间信息（公告携带）得到。0 = 用场景 Inspector
    /// 默认值（手输直连）。host 端口"优先 7783、被占则 OS 分配"，不能写死</summary>
    public static ushort ServerPort;

    /// <summary>房间名（host 在发现广播里带上，client 列表显示）</summary>
    public static string RoomName;

    /// <summary>玩家名（host 广播展示 + 加入请求携带）</summary>
    public static string PlayerName;
}
