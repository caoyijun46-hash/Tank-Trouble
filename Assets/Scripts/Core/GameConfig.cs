// 菜单 → 游戏场景的跨场景配置：静态字段在场景加载之间存活。
// 主菜单按钮先写 Mode 再 LoadScene("Main")，GameManager.SpawnRound 读取。
// 默认 VsAI：直接从 Main 场景开 Play（不走菜单）也成立。
public enum GameMode { VsAI = 0, Double = 1 }

public static class GameConfig
{
    public static GameMode Mode = GameMode.VsAI;
}
