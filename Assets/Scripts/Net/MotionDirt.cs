using UnityEngine;

// 壳移动扬尘（client 本地表现）：按帧间位移测速，超阈值播尘/停下即停。
// 壳由 SnapshotPlayer 以插值姿态驱动，本身无 Rigidbody 可读——位移法即事实：
// host 尘粒子由物理速度驱动（TankBase.UpdateDirt 读 rb.linearVelocity），
// 两者语义等价：动得快就喷、停就停，纯本地推断、零网络。
//
// 挂 Dead 坦克壳根（如 Dead Tank / Dead Tank 1），拖 FX_DirtSplatter.prefab。
// 喷尘阈值从 UnitConfig.dirtSpeedThreshold 读——与 host 端 TankBase.UpdateDirt
// 同一份配置源，两端不会各配一套导致"host 在喷、壳不喷"的观感差
public class MotionDirt : MonoBehaviour
{
    [Tooltip("尘土粒子 prefab（与 host 坦克同款：Assets/Particles/FX_DirtSplatter.prefab）")]
    [SerializeField] private GameObject dirtPrefab;

    [Tooltip("粒子实例相对壳根的位置（0 = prefab 发射器已贴地）")]
    [SerializeField] private Vector3 dirtOffset;

    [Tooltip("坦克手感配置（Assets/Config/UnitConfig.asset）：喷尘阈值与 host 端同源读取")]
    [SerializeField] private UnitConfig unitConfig;
    private float thresholdSqr; // Start 从 unitConfig 缓存（平方，免每帧开方）

    private ParticleSystem dirt;
    private Vector3 lastPos;
    private bool hasLast;

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
        // 阈值与 host 端同源；兜底仅供缺配不崩（不是第二配置源）
        float t = unitConfig != null ? unitConfig.dirtSpeedThreshold : 0.5f;
        thresholdSqr = t * t;
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

        bool rolling = vSqr > thresholdSqr;
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
    }
}
