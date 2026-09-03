using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

// ============================================================
// 暂停总控：HUD 的暂停按钮 + PauseUI 面板按钮都归它管。
//
//   面板架构（场景层级，PauseController 与 ScoreHud 都挂 UI 父物体）：
//     UI（仅作总控，无 PanelRenderer）
//       ├─ MainAIUI      PanelRenderer → MainAI.uxml（右上角 name=Pause 按钮）
//       ├─ MainDoubleUI  PanelRenderer → MainDouble.uxml
//       └─ PauseUI       PanelRenderer → Pause.uxml（遮罩 + Menu/Restart/Continue）
//
//   为什么不关 HUD：每个 PanelRenderer 是独立事件域，上层 panel 消费掉
//   点击后不会传给下层 → PauseUI 的全屏遮罩挡住一切，HUD 看得见但点不到；
//   前提是 PauseUI 叠层在 HUD 之上（Inspector 的 Sort Order，见接线步骤）。
//   游戏真正暂停靠 Time.timeScale = 0（物理/AI/道具计时全冻），UI 点击
//   不受 timeScale 影响所以按钮照常响应。
//
//   绑定纪律：PanelRenderer 的 root 只能从 reload 回调拿，且每次 reload
//   重建整棵树 → 按钮绑定一律放 OnUIReload 里按 name 查询，天然幂等。
//   本组件必须比 ScoreHud 先 Awake（先把回调注册好，HUD 面板随后才被
//   激活触发 reload），所以执行序排在 ScoreHud 之前。
// ============================================================
[DefaultExecutionOrder(-200)]
public class PauseController : MonoBehaviour
{
    [Tooltip("暂停面板物体（PauseUI，含 PanelRenderer → Pause.uxml）")]
    [SerializeField] private GameObject pausePanel;

    [Tooltip("主菜单场景名（需在 Build Settings 注册）")]
    [SerializeField] private string menuSceneName = "Menu";

    void Awake()
    {
        // 收编 UI 下全部面板（含 inactive 的 PauseUI）：谁被激活谁触发 reload
        foreach (PanelRenderer p in GetComponentsInChildren<PanelRenderer>(true))
        {
            p.RegisterUIReloadCallback(OnUIReload);
        }
    }

    void OnDestroy()
    {
        foreach (PanelRenderer p in GetComponentsInChildren<PanelRenderer>(true))
        {
            p.UnregisterUIReloadCallback(OnUIReload);
        }
    }

    // reload 重建整棵树 → 重新绑定；Query 不到的元素（别的面板的）自然跳过
    void OnUIReload(PanelRenderer renderer, VisualElement root, int version)
    {
        if (root == null)
        {
            return;
        }
        Bind(root, "Pause", Open);      // HUD 右上角：打开暂停
        Bind(root, "Continue", Continue); // 继续游戏
        Bind(root, "Restart", Restart);   // 重新开始（重置计分板）
        Bind(root, "Menu", GoMenu);       // 回主菜单
    }

    static void Bind(VisualElement root, string name, Action action)
    {
        if (root.Q<Button>(name) is { } button)
        {
            button.clicked += action;
        }
    }

    void Open()
    {
        // 结算定格（RoundFrozen）期间禁开暂停：定格只有 3 秒且到点自动
        // 重开，重开协程恢复 timeScale 会与暂停打架——出现"遮罩挂着但
        // 世界已运行"的半暂停，所以这期间直接忽略暂停请求
        if (GameManager.Instance != null && GameManager.Instance.RoundFrozen)
        {
            return;
        }
        pausePanel.SetActive(true);
        Time.timeScale = 0f;
    }

    void Continue()
    {
        pausePanel.SetActive(false);
        Time.timeScale = 1f;
    }

    // 整场重来：先关面板，GameManager.ResetMatch 自己会恢复 timeScale
    void Restart()
    {
        pausePanel.SetActive(false);
        if (GameManager.Instance != null)
        {
            GameManager.Instance.ResetMatch();
        }
    }

    void GoMenu()
    {
        Time.timeScale = 1f; // Menu 场景没有 GameManager 兜底，先恢复再说
        SceneManager.LoadScene(menuSceneName);
    }
}
