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

    [Tooltip("常驻暂停按钮面板（PauseButtonUI，场景保持 inactive，启动时由本组件激活）")]
    [SerializeField] private GameObject pauseButtonUI;

    [Tooltip("主菜单场景名（需在 Build Settings 注册）")]
    [SerializeField] private string menuSceneName = "Menu";

    // 暂停权只属于 host：联机 client 无暂停入口（不激活暂停按钮、拦截一切暂停动作，
    // 只把 PauseMenuUI 当 host 暂停的只读遮罩）。单机无 NetManager → 视为可暂停
    bool CanPause => NetManager.Instance == null || NetManager.Instance.IsHost;

    void Awake()
    {
        // 收编 UI 下全部面板（含 inactive 的 PauseUI/PauseButtonUI）：谁被激活谁触发 reload
        foreach (PanelRenderer p in GetComponentsInChildren<PanelRenderer>(true))
        {
            p.RegisterUIReloadCallback(OnUIReload);
        }
    }

    void Start()
    {
        // 暂停按钮激活放 Start 而非 Awake：本组件执行序(-200)早于 NetManager.Awake，
        // Awake 阶段判 CanPause 会误读 Instance==null（把 client 当 host）
        // client 端（!CanPause）不激活暂停按钮——联机暂停权在 host。
        // 回调在 Awake 已全部注册，此刻激活不会错过首次 reload
        if (CanPause && pauseButtonUI != null)
        {
            pauseButtonUI.SetActive(true);
        }

        // client 端：host 的暂停广播 → 显示只读遮罩（纯呈现，不动 timeScale——
        // 壳无新快照自然冻结，硬同步有断线致永久卡死的风险）。host 端不收该事件
        if (NetManager.Instance != null)
        {
            NetManager.Instance.PauseSynced += ShowRemotePause;
        }
    }

    void OnDestroy()
    {
        if (NetManager.Instance != null)
        {
            NetManager.Instance.PauseSynced -= ShowRemotePause;
        }
        foreach (PanelRenderer p in GetComponentsInChildren<PanelRenderer>(true))
        {
            p.UnregisterUIReloadCallback(OnUIReload);
        }
    }

    // host 暂停广播 → client 显示 PauseMenuUI 只读遮罩；继续 → 关闭。
    // 不碰 timeScale：client 画面因无新快照自然冻结（unscaled 插值缓冲耗尽停在端点）
    void ShowRemotePause(bool paused)
    {
        pausePanel.SetActive(paused);
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

        BindVolume(root); // 音量滑杆（音频收口也走本回调）

        // client 端 PauseMenuUI 只作只读遮罩：把操作按钮隐藏，避免"点了没反应"的误导
        if (!CanPause)
        {
            HideButton(root, "Continue");
            HideButton(root, "Restart");
            HideButton(root, "Menu");
        }
    }

    static void HideButton(VisualElement root, string name)
    {
        if (root.Q<Button>(name) is { } button)
        {
            button.style.display = DisplayStyle.None;
        }
    }

    // 点击音必须绑进 clicked 回调本身、与动作同点触发：
    // Restart/Continue 的动作会立刻 SetActive(false) 关掉面板——若音效挂在
    // ClickEvent 树传播上（root 捕获），面板一禁用事件就传不到（实测只有
    // 不关面板的 Menu 按钮响）。先播音再执行动作。
    static void Bind(VisualElement root, string name, Action action)
    {
        if (root.Q<Button>(name) is { } button)
        {
            button.clicked += () =>
            {
                AudioManager.PlayClick();
                action();
            };
        }
    }

    // 暂停面板音量条（0-100）：先回读存档位置再监听拖动（赋值会触发一次
    // ChangeEvent，幂等无碍）；两条通道独立持久化
    static void BindVolume(VisualElement root)
    {
        if (root.Q<Slider>("BGM") is { } bgm)
        {
            bgm.value = AudioManager.BgmVolume * 100f;
            bgm.RegisterCallback<ChangeEvent<float>>(e => AudioManager.SetBgmVolume(e.newValue / 100f));
        }
        if (root.Q<Slider>("SFX") is { } sfx)
        {
            sfx.value = AudioManager.SfxVolume * 100f;
            sfx.RegisterCallback<ChangeEvent<float>>(e => AudioManager.SetSfxVolume(e.newValue / 100f));
        }
    }

    void Open()
    {
        // 暂停权只属 host（client 无按钮，此处防御性拦截）
        if (!CanPause)
        {
            return;
        }
        // 结算定格（RoundFrozen）期间禁开暂停：定格只有 3 秒且到点自动
        // 重开，重开协程恢复 timeScale 会与暂停打架——出现"遮罩挂着但
        // 世界已运行"的半暂停，所以这期间直接忽略暂停请求
        if (GameManager.Instance != null && GameManager.Instance.RoundFrozen)
        {
            return;
        }
        pausePanel.SetActive(true);
        Time.timeScale = 0f;
        NetManager.Instance?.HostSendPause(true); // 判空：单机无 NetManager 静默
    }

    void Continue()
    {
        if (!CanPause)
        {
            return;
        }
        pausePanel.SetActive(false);
        Time.timeScale = 1f;
        NetManager.Instance?.HostSendPause(false);
    }

    // 整场重来：先关面板，GameManager.ResetMatch 自己会恢复 timeScale
    void Restart()
    {
        if (!CanPause)
        {
            return;
        }
        pausePanel.SetActive(false);
        if (GameManager.Instance != null)
        {
            GameManager.Instance.ResetMatch();
        }
    }

    void GoMenu()
    {
        if (!CanPause)
        {
            return;
        }
        Time.timeScale = 1f; // Menu 场景没有 GameManager 兜底，先恢复再说
        SceneManager.LoadScene(menuSceneName);
    }
}
