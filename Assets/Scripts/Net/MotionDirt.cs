using UnityEngine;

// 壳移动扬尘（client 本地表现）：按帧间位移测速，超阈值播尘/停下即停。
// 壳由 SnapshotPlayer 以插值姿态驱动，本身无 Rigidbody 可读——位移法即事实：
// host 尘粒子由物理速度驱动（TankBase.UpdateDirt 读 rb.linearVelocity），
// 两者语义等价：动得快就喷、停就停，纯本地推断、零网络。
//
// 挂 Dead 坦克壳根（如 Dead Tank / Dead Tank 1），拖 FX_DirtSplatter.prefab。
// 速度阈值与 host 端一致（0.5 m/s），避免"host 在喷、壳不喷"的观感差
public class MotionDirt : MonoBehaviour
{
    [Tooltip("尘土粒子 prefab（与 host 坦克同款：Assets/Particles/FX_DirtSplatter.prefab）")]
    [SerializeField] private GameObject dirtPrefab;

    [Tooltip("粒子实例相对壳根的位置（0 = prefab 发射器已贴地）")]
    [SerializeField] private Vector3 dirtOffset;

    [Tooltip("喷尘速度阈值（m/s），与 TankBase.UpdateDirt 一致")]
    [SerializeField, Range(0.1f, 2f)] private float threshold = 0.5f;

    private ParticleSystem dirt;
    private Vector3 lastPos;
    private bool hasLast;

    // 临时诊断（定位后删）：0.2s 窗口累计"帧间位移之和"与"非零差分帧数"——
    // 区分连续运动(每帧都有位移)与阶跃运动(偶尔大跳,大部分帧差分 0)
    private float dbgTimer;
    private float dbgWinMove;
    private int dbgWinFrames;
    private int dbgWinNonZero;

    void Start()
    {
        if (dirtPrefab == null)
        {
            Debug.LogWarning($"[Dirt][{name}] 未拖 dirtPrefab——不喷尘是配置问题", this);
            return;
        }
        dirt = Instantiate(dirtPrefab, transform).GetComponent<ParticleSystem>();
        dirt.transform.localPosition = dirtOffset;
        // 压住 playOnAwake 首播，并立即清空：Stop() 默认不清粒子——若首播已发射
        // 粒子，isPlaying/isStopped 会等它们死光才翻转，滚动判定会被跳过（"尘一直不显示"）
        dirt.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        lastPos = transform.position;
        hasLast = true;
    }

    void Update()
    {
        if (dirt == null)
        {
            return;
        }
        // 位移法测速：壳由插值驱动，帧间位移平滑；用真实时间避免 timeScale 干扰
        float dt = Time.unscaledDeltaTime;
        float vSqr = hasLast && dt > 1e-4f
            ? (transform.position - lastPos).sqrMagnitude / (dt * dt)
            : 0f;
        lastPos = transform.position;
        hasLast = true;

        bool rolling = vSqr > threshold * threshold;
        // 判定用 isPlaying（Play/Stop 后立即翻转）而非 isStopped（依赖粒子死光）
        if (rolling)
        {
            if (!dirt.isPlaying)
            {
                dirt.Play();
            }
        }
        else if (dirt.isPlaying)
        {
            dirt.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }

        // 临时诊断：窗口累计（不打印每帧，避免刷屏）
        dbgWinMove += (transform.position - lastPos).magnitude;
        dbgWinFrames++;
        if (vSqr > 1e-4f)
        {
            dbgWinNonZero++;
        }
        dbgTimer += Time.unscaledDeltaTime;
        if (dbgTimer >= 0.2f)
        {
            dbgTimer = 0f;
            Debug.Log($"[Dirt][{name}] pos={transform.position:F1} 窗口位移={dbgWinMove:F2}m 差分非零帧={dbgWinNonZero}/{dbgWinFrames} vSqr={vSqr:F2} rolling={rolling} isPlaying={dirt.isPlaying}");
            dbgWinMove = 0f;
            dbgWinFrames = 0;
            dbgWinNonZero = 0;
        }
    }
}
