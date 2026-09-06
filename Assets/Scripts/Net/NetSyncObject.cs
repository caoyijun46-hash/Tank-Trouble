using UnityEngine;

// 阶段 1 的同步载体：一个沿圆形轨迹自走的假对象。
//   Host：世界本体在这端——直接把自身摆到轨迹上（真实模拟方）
//   Client：快照播放器——没有自己的状态，只有 InterpBuffer，NetManager
//           推快照进来，每帧从缓冲取姿态覆盖 transform
// 刻意不接真实坦克：先证明"模拟→序列化→传输→插值→渲染"管道正确，
// 再接玩法时问题范围才不会混在一起（网络 bug vs 坦克逻辑 bug）
public class NetSyncObject : MonoBehaviour
{
    [Header("圆形轨迹参数（两端数学一致，便于肉眼对比插值正确性）")]
    [SerializeField] private float radius = 5f;
    [SerializeField] private float circleSpeed = 0.4f; // 圈/秒

    [Header("客户端插值参数")]
    [Tooltip("缓冲深度（快照帧数）：延迟预算旋钮——越深越抗抖动、画面越滞后")]
    [SerializeField, Range(1, 10)] private int bufferDepth = 3;

    private readonly InterpBuffer buffer = new InterpBuffer();
    private bool hasRenderPose;

    void Awake()
    {
        buffer.Depth = bufferDepth;
    }

    void Update()
    {
        if (NetManager.Instance != null && NetManager.Instance.IsHost)
        {
            // Host：自己是模拟本体，直接摆到轨迹上
            var (x, z, yaw) = ComputePose(Time.time);
            transform.position = new Vector3(x, transform.position.y, z);
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        }
    }

    void LateUpdate()
    {
        if (NetManager.Instance == null || NetManager.Instance.IsHost)
        {
            return;
        }
        // Client：从缓冲取姿态；缓冲不足时（开局/断流）冻结不动
        if (buffer.GetPose(out var p))
        {
            transform.position = new Vector3(p.x, transform.position.y, p.z);
            transform.rotation = Quaternion.Euler(0f, p.yaw, 0f);
            hasRenderPose = true;
        }
        else if (!hasRenderPose)
        {
            // 首帧无数据：把对象挪到视野外的原点待命，避免闪现在场景默认位置
            transform.position = new Vector3(0f, -100f, 0f);
        }
    }

    // Host 侧 NetManager 每 tick 采集
    public (float x, float z, float yaw) CurrentPose() => ComputePose(Time.time);

    // Client 侧 NetManager 推送快照
    public void PushSnapshot(in SnapshotData snap) => buffer.Push(snap);

    // 圆形轨迹：位置 = 圆上一点，朝向 = 切线方向（随动，方便观察插值）
    (float x, float z, float yaw) ComputePose(float time)
    {
        float deg = time * circleSpeed * 360f;
        float rad = deg * Mathf.Deg2Rad;
        float x = Mathf.Sin(rad) * radius;
        float z = Mathf.Cos(rad) * radius;
        float yaw = Mathf.Atan2(Mathf.Cos(rad), -Mathf.Sin(rad)) * Mathf.Rad2Deg;
        return (x, z, yaw);
    }
}
