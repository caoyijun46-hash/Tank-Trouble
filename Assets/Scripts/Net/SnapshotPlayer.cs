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

    private readonly InterpBuffer buffer = new InterpBuffer();
    private bool hasRenderPose;
    private float localY; // 快照无 y 轴（实体都在 y=0 平面），壳高度固定为预制体初始 y

    public byte SyncId { get; private set; }
    public bool Bound { get; private set; }

    void Awake()
    {
        buffer.Depth = bufferDepth;
        localY = transform.position.y;
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

    /// <summary>NetManager 实体帧分发：把本对象姿态推进插值缓冲</summary>
    public void Push(uint seq, float hostTime, float x, float z, float yaw)
    {
        buffer.Push(new SnapshotData
        {
            seq = seq,
            hostTime = hostTime,
            x = x,
            z = z,
            yaw = yaw,
        });
    }

    void LateUpdate()
    {
        // 每帧从缓冲取应渲染姿态；缓冲不足（开局/断流）冻结在最后位置
        if (buffer.GetPose(out var p))
        {
            transform.position = new Vector3(p.x, localY, p.z);
            transform.rotation = Quaternion.Euler(0f, p.yaw, 0f);
            hasRenderPose = true;
        }
        else if (!hasRenderPose)
        {
            // 首帧无数据：把对象挪到视野外的原点待命，避免闪现在场景默认位置
            transform.position = new Vector3(0f, -100f, 0f);
        }
    }
}
