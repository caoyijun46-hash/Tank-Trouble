using UnityEngine;
using UnityEngine.UIElements;

// ============================================================
// 计分 HUD 总控：按 GameConfig.Mode 激活对应 HUD 面板并刷新比分。
//
//   面板架构（场景层级）：
//     UI（挂本组件，仅作总控，不需要 PanelRenderer）
//       ├─ MainAIUI      PanelRenderer → MainAI.uxml    （1vAI：右槽=莱很卡）
//       ├─ MainDoubleUI  PanelRenderer → MainDouble.uxml（双人：右槽=玩家2）
//       └─ PauseUI       PanelRenderer → Pause.uxml     （本组件不管）
//   每张面板自带固定文档，谁激活由本组件二选一；未来的暂停面板
//   走同一套"父 UI 总控 + 子面板"结构，只是由别的控制器负责。
//
//   时序纪律：PanelRenderer 在物体启用的瞬间（OnEnable）加载 uxml 并
//   触发 reload 回调，而根元素只能从回调参数拿（它没有 UIDocument 那种
//   rootVisualElement 属性）→ 必须先 RegisterUIReloadCallback 再
//   SetActive(true)，否则错过首次加载就永远绑不上。
//   reload 会重建整棵 VisualElement 树，旧 Label 引用失效，所以绑定
//   一律放回调里做。
//
//   文本前缀（"玩家1："）从 uxml 模板解析，改名字不动代码。
//
//   执行序在 PauseController 之后：它要先注册完全部面板的 reload 回调，
//   本组件再激活 HUD 面板，首次 reload 回调才不落空。
// ============================================================
[DefaultExecutionOrder(-100)]
public class ScoreHud : MonoBehaviour
{
    [Tooltip("1vAI 模式的 HUD 面板（MainAIUI 物体）")]
    [SerializeField] private GameObject vsAIPanel;

    [Tooltip("双人模式的 HUD 面板（MainDoubleUI 物体）")]
    [SerializeField] private GameObject doublePanel;

    private PanelRenderer activePanel; // 当前启用的面板（OnDestroy 注销用）
    private Label scoreP1;   // 左槽：阵营 0（玩家 1）
    private Label scoreP2;   // 右槽：阵营 1（AI 或玩家 2）
    private string prefixP1;
    private string prefixP2;

    void Awake()
    {
        // 两玩家槽面板（MainDoubleUI）服务一切"对手是人"的模式：Double 与 Online；
        // 只有 1vAI（右槽=莱很卡）用单机 AI 面板
        bool twoPlayers = GameConfig.Mode != GameMode.VsAI;
        Activate(vsAIPanel, !twoPlayers);
        Activate(doublePanel, twoPlayers);
    }

    // on=false 只做幂等关闭；on=true 先注册回调再启用（见头部时序纪律）
    void Activate(GameObject panel, bool on)
    {
        panel.SetActive(false);
        if (!on)
        {
            return;
        }
        activePanel = panel.GetComponent<PanelRenderer>();
        if (activePanel == null)
        {
            Debug.LogError($"ScoreHud: {panel.name} 上没有 PanelRenderer", this);
            return;
        }
        activePanel.RegisterUIReloadCallback(OnUIReload);
        panel.SetActive(true);
    }

    void Start()
    {
        if (GameManager.Instance != null)
        {
            GameManager.Instance.ScoresUpdated += Refresh;
        }
        Refresh();
    }

    void OnDestroy()
    {
        if (GameManager.Instance != null)
        {
            GameManager.Instance.ScoresUpdated -= Refresh;
        }
        if (activePanel != null)
        {
            activePanel.UnregisterUIReloadCallback(OnUIReload);
        }
    }

    // reload 后整棵树是新的：重新查两个 Label，并从模板文本记下前缀
    void OnUIReload(PanelRenderer renderer, VisualElement root, int version)
    {
        scoreP1 = root?.Q<Label>("ScoreP1");
        scoreP2 = root?.Q<Label>("ScoreP2");
        prefixP1 = PrefixOf(scoreP1);
        prefixP2 = PrefixOf(scoreP2);
        Refresh();
    }

    // 模板 "玩家1：0" → 前缀 "玩家1："；label 缺失（不该发生的中间态）给空串
    static string PrefixOf(Label label)
    {
        if (label == null)
        {
            return "";
        }
        int i = label.text.LastIndexOf('：');
        return i >= 0 ? label.text.Substring(0, i + 1) : label.text;
    }

    void Refresh()
    {
        if (GameManager.Instance == null)
        {
            return;
        }
        if (scoreP1 != null)
        {
            scoreP1.text = prefixP1 + GameManager.Instance.GetScore(GameManager.TeamOne);
        }
        if (scoreP2 != null)
        {
            scoreP2.text = prefixP2 + GameManager.Instance.GetScore(GameManager.TeamTwo);
        }
    }
}
