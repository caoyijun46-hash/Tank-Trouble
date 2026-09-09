// 菜单 → 游戏场景的跨场景配置：静态字段在场景加载之间存活。
// 主菜单按钮先写 Mode 再 LoadScene("Main")，GameManager.SpawnRound 读取。
// 默认 Online：直接 Play Main = 联机 host（当前联机调试期的默认入口；菜单
// Online 按钮后置）。注意 Mode 是静态残留——从菜单跑过 VsAI/Double 后再
// 直接 Play Main 会是单机，需重新 Play 或经菜单进入才回 Online。
public enum GameMode { VsAI = 0, Double = 1, Online = 2 }

public static class GameConfig
{
    public static GameMode Mode = GameMode.Online;
}
