using UnityEngine;

// 壳播放组件（块 2）：挂在 client 端显示壳 prefab（Assets/Prefabs/Dead/，已摘玩法脚本）根上。
// client 是"快照播放器"——壳没有自己的状态/模拟，只有插值缓冲：
//   NetManager 收 Spawn 事件 → 实例化壳 → Bind(id)；
//   每帧收到实体帧 → Push(帧头时间戳 + 本对象姿态) → 这里 LateUpdate 取姿态渲染。
//
// 与 NetSyncObject 的关系：本组件是它 client 侧职责（缓冲+渲染）的独立版，
// 砍掉了 host 端运动学模拟与 ISyncHost 命令面——壳上只有渲染，无任何第二本账。
// 壳上不能有 Rigidbody/玩法脚本（有刚体物理会顶掉这里的 transform 写入）。
//
// yaw 是周期量，插值必须走 InterpBuffer 内的 LerpAngle（350°→10° 走 20° 而不是绕 340°）
public class SnapshotPlayer : MonoBehaviour
{
    [Header("插值参数")]
    [Tooltip("缓冲深度（快照帧数）：延迟预算旋钮——越深越抗抖动、画面越滞后")]
    [SerializeField, Range(1, 10)] private int bufferDepth = 3;

    [Header("死亡演出")]
    [Tooltip("本壳对应的击杀爆炸 prefab（与 host 原型阵营分色配对：如 Dead Tank→Explosion_Red、Dead Tank 1→Explosion_Orange）。留空 = 无爆炸（子弹/道具壳）")]
    [SerializeField] private GameObject explosionPrefab;
    public GameObject ExplosionPrefab => explosionPrefab;

    private readonly InterpBuffer buffer = new InterpBuffer();
    private bool hasRenderPose;
    private float localY; // 快照无 y 轴（实体都在 y=0 平面），壳高度固定为预制体初始 y

    // 武器状态（离散、不插值）：每帧快照的最新值；Laser 时驱动瞄准预览线。
    // 所有坦克壳都画自己的线——与单机/本地双人"同屏可见对方瞄准线"的哲学一致
    public byte LastPower { get; private set; }
    private AimLinePreview aimPreview; // 惰性缓存（子物体 Aim Line 上的预览组件）
    private bool aimSearched;

    public byte SyncId { get; private set; }
    public bool Bound { get; private set; }

    void Awake()
    {
        buffer.Depth = bufferDepth;
        localY = transform.position.y;
        // 初始禁画：壳上的 AimLinePreview 默认 enabled 会自己每帧画线（host 端由
        // SetPower 出生时先 Show(false) 压住）。壳出生 power=Normal，ApplyPower 只在
        // 变化时驱动——这里必须先显式关一次，等快照 power 变化再开
        aimPreview = GetComponentInChildren<AimLinePreview>();
        aimSearched = true;
        if (aimPreview != null)
        {
            aimPreview.Show(false);
        }
    }

    /// <summary>Spawn 事件后由 NetManager 调用，绑定注册表 id</summary>
    public void Bind(byte id)
    {
        SyncId = id;
        Bound = true;
    }

    /// <summary>出生即摆到 spawn 姿态（host 的准确事实，直接渲染）：
    /// 不等插值攒够两帧——否则出生瞬间会闪到视野外待命再跳回来</summary>
    public void Prime(float x, float z, float yaw)
    {
        transform.position = new Vector3(x, localY, z);
        transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        hasRenderPose = true;
    }

    /// <summary>NetManager 实体帧分发：姿态进插值缓冲；power 是离散状态直接用最新值</summary>
    public void Push(uint seq, float hostTime, float x, float z, float yaw, byte power)
    {
        buffer.Push(new SnapshotData
        {
            seq = seq,
            hostTime = hostTime,
            x = x,
            z = z,
            yaw = yaw,
            power = power,
        });
        ApplyPower(power);
    }

    // 武器变化驱动瞄准预览线：与 host 端 TankBase.SetPower 开关逻辑同源
    //（Laser 持有才画、发射/换武器即关）。查找一次后锁定（含 null：子弹/道具
    // 壳无预览组件不再查）
    void ApplyPower(byte power)
    {
        if (power == LastPower)
        {
            return;
        }
        LastPower = power;
        if (!aimSearched)
        {
            aimPreview = GetComponentInChildren<AimLinePreview>();
            aimSearched = true;
        }
        if (aimPreview != null)
        {
            aimPreview.Show(power == (byte)Power.Laser);
        }
    }

    void LateUpdate()
    {
        // 每帧从缓冲取应渲染姿态；缓冲不足（开局/断流）冻结在最后位置
        if (buffer.GetPose(out var p))
        {
            transform.position = new Vector3(p.x, localY, p.z);
            transform.rotation = Quaternion.Euler(0f, p.yaw, 0f);
            hasRenderPose = true;
            DbgLog();
        }
        else if (!hasRenderPose)
        {
            // 首帧无数据：把对象挪到视野外的原点待命，避免闪现在场景默认位置
            transform.position = new Vector3(0f, -100f, 0f);
        }
    }

    // 临时诊断（阶跃定位后删）：打印插值窗口内部状态
    private float dbgTimer;
    void DbgLog()
    {
        dbgTimer += Time.unscaledDeltaTime;
        if (dbgTimer < 0.3f)
        {
            return;
        }
        dbgTimer = 0f;
        Debug.Log($"[IP][{name}] span={buffer.DbgSpan:F4} prog={buffer.DbgProgress:F2} queue={buffer.DbgQueueCount} frozen={buffer.DbgFrozen} pos={transform.position:F1}");
    }
}
