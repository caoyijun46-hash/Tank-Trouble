using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// ============================================================
// 音频总控：DontDestroyOnLoad 全局单例，prefab 双场景放置。
//
//   形态：AudioManager 做成 prefab（拖齐 click/fire/crash/bgm + 可配的
//   bgmSceneName），Menu 与 Main 场景各放一个该 prefab 的实例：
//     - 先加载的实例成为全局（DontDestroyOnLoad，AudioSource 不被场景卸载）
//     - 后加载的实例直接自毁——两个实例引用同源（同一 prefab），
//       没有"独有信息"要交接，所以不需要任何 Adopt/补给机制
//     - 任一场景单独 Play（编辑器直进 Main 调试）都有完整声音
//   切场景瞬间的 UI 点击音不中断：AudioSource 挂在全局物体上，
//   不随任何场景卸载。
//
//   BGM 规则：SceneManager.activeSceneChanged 驱动——切到场景名等于
//   bgmSceneName（默认 "Main"）就循环播，否则停。显式场景判断，
//   不做"拖没拖 clip"的隐式推断。暂停菜单（timeScale=0）不暂停 BGM：
//   AudioSource 播放本就不受 timeScale 影响。
//
//   音量：SFX/BGM 两条通道独立（各自 AudioSource 的 volume），暂停面板
//   的两个 Slider 经 SetSfxVolume/SetBgmVolume 写入并 PlayerPrefs 持久化。
//
//   点击音分工：
//     Menu（UGUI）：本组件 Start 时给场景内全部 Button 挂 onClick（场景
//     加载一次性遍历，低频）；Main（UI Toolkit）的点击音与音量 Slider 由
//     PauseController.OnUIReload 统一收口（见该文件），这里不再注册
//     PanelRenderer 回调，避免多个脚本抢占 reload 注册。
//
//   静态 API 一律判空：无全局实例时静默跳过（放 prefab 前不炸）。
// ============================================================
public class AudioManager : MonoBehaviour
{
    public static AudioManager Instance { get; private set; }

    [Header("音效（SFX 通道）")]
    [SerializeField] private AudioClip clickClip; // UI 点击
    [SerializeField] private AudioClip fireClip;  // 坦克开火
    [SerializeField] private AudioClip crashClip; // 坦克死亡爆炸

    [Header("背景音乐（切到 bgmSceneName 的场景才播）")]
    [SerializeField] private AudioClip bgmClip;
    [Tooltip("播放 BGM 的场景名（当前只有 Main）")]
    [SerializeField] private string bgmSceneName = "Main";

    private AudioSource sfxSource;
    private AudioSource bgmSource;

    // 音量 0-1（静态：TankBase/UI 无需拿 Instance 也能读初值）
    public static float SfxVolume { get; private set; } = 0.42f;
    public static float BgmVolume { get; private set; } = 0.42f;
    private const string SfxKey = "audio.sfxVolume";
    private const string BgmKey = "audio.bgmVolume";

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            // 后加载的场景实例：引用与全局同源（同一 prefab），无事可交，自毁
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
        SceneManager.activeSceneChanged += OnSceneChanged;

        sfxSource = gameObject.AddComponent<AudioSource>();
        sfxSource.playOnAwake = false;
        bgmSource = gameObject.AddComponent<AudioSource>();
        bgmSource.playOnAwake = false;
        bgmSource.loop = true;

        // 恢复上次音量并应用（之后 Set*Volume 随时覆盖）
        SfxVolume = PlayerPrefs.GetFloat(SfxKey, 0.42f);
        BgmVolume = PlayerPrefs.GetFloat(BgmKey, 0.42f);
        sfxSource.volume = SfxVolume;
        bgmSource.volume = BgmVolume;
    }

    void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
            SceneManager.activeSceneChanged -= OnSceneChanged;
        }
    }

    void Start()
    {
        ApplyBgm(SceneManager.GetActiveScene().name); // 首场景（可能直进 Main）
        HookUguiButtons();
    }

    // 场景切换驱动 BGM + 给新场景的 UGUI 按钮挂点击音。
    // 挂载不能只放 Start：若全局实例由 Main 场景创建（编辑器直进 Main），
    // 回 Menu 后按钮是场景新加载的对象，Start 只在全局创建时跑过一次——
    // 每次切场景后重挂（新场景的按钮从没挂过，不会重复 AddListener）
    void OnSceneChanged(Scene prev, Scene next)
    {
        ApplyBgm(next.name);
        HookUguiButtons();
    }

    void ApplyBgm(string sceneName)
    {
        bool wantBgm = sceneName == bgmSceneName && bgmClip != null;
        if (wantBgm)
        {
            if (bgmSource.clip != bgmClip)
            {
                bgmSource.clip = bgmClip;
            }
            if (!bgmSource.isPlaying)
            {
                bgmSource.Play();
            }
        }
        else if (bgmSource.isPlaying)
        {
            bgmSource.Stop();
        }
    }

    // ---------- 播放 API（静态） ----------

    public static void PlayClick() => PlayClip(Instance != null ? Instance.clickClip : null);

    public static void PlayFire() => PlayClip(Instance != null ? Instance.fireClip : null);

    public static void PlayCrash() => PlayClip(Instance != null ? Instance.crashClip : null);

    static void PlayClip(AudioClip clip)
    {
        if (clip == null || Instance == null)
        {
            return; // 没拖引用或没全局实例 → 静默（放 prefab 前不炸）
        }
        Instance.sfxSource.PlayOneShot(clip);
    }

    // ---------- 音量（UI Slider 0-100 → 这里 0-1） ----------

    public static void SetSfxVolume(float v)
    {
        SfxVolume = Mathf.Clamp01(v);
        PlayerPrefs.SetFloat(SfxKey, SfxVolume);
        if (Instance != null)
        {
            Instance.sfxSource.volume = SfxVolume;
        }
    }

    public static void SetBgmVolume(float v)
    {
        BgmVolume = Mathf.Clamp01(v);
        PlayerPrefs.SetFloat(BgmKey, BgmVolume);
        if (Instance != null)
        {
            Instance.bgmSource.volume = BgmVolume;
        }
    }

    // ---------- UGUI 点击音（Menu 场景） ----------

    // AudioManager 物体在 Canvas 外，按钮不是它的子物体 → 用全场景查找；
    // 只在场景加载时跑一次，低频操作没问题
    void HookUguiButtons()
    {
        if (clickClip == null)
        {
            return;
        }
        foreach (Button btn in FindObjectsByType<Button>(FindObjectsInactive.Include))
        {
            btn.onClick.AddListener(PlayClick);
        }
    }
}
