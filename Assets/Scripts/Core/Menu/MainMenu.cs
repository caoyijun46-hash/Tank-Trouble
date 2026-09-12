using UnityEngine;
using UnityEngine.SceneManagement;

// 主菜单：把"AI / 双人"按钮接到对应游戏模式，再加载游戏场景。
// 编辑器接线：
//   1. Menu 场景建空物体 "Menu Manager"，挂本组件（gameSceneName 默认 Main）
//   2. 选中 "AI" 按钮 → OnClick() 加一项 → 拖入 Menu Manager → 选 StartVsAI
//   3. "Double" 按钮同理选 StartDouble
//   4. 前置条件：Menu 与 Main 都要加入 Build Settings（否则 LoadScene 报错）
public class MainMenu : MonoBehaviour
{
    [Tooltip("游戏场景名（需在 Build Settings 注册）")]
    [SerializeField] private string gameSceneName = "Main";

    public void StartVsAI()
    {
        GameConfig.Mode = GameMode.VsAI;
        LoadGame();
    }

    public void StartDouble()
    {
        GameConfig.Mode = GameMode.Double;
        LoadGame();
    }

    // Main Panel 的 Exit：退出游戏。属于主菜单通用功能（与联机无关）——
    // 编辑器里停止 Play 便于调试，Build 后走 Application.Quit
    public void QuitGame()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    void LoadGame()
    {
        SceneManager.LoadScene(gameSceneName);
    }
}
