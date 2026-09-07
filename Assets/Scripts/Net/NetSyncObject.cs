using UnityEngine;

// 联机调试期同步载体（真坦克接入前可整个删除，接口见 ISyncHost）。
// Host/Client 两侧职责不同：
//   Host：模拟本体在本地——被客户端上行命令驱动（NetManager 收 Move 命令 →
//       ApplyCommand 缓存输入 → 这里每帧积分前进/转向）。真坦克替换后这侧退役
//       （RemoteTank 顶替：命令喂给 TankBase，物理 Move 驱动）。
//   Client：快照播放器——没有自己的状态，只有 InterpBuffer，NetManager
//       推快照进来，每帧从缓冲取姿态覆盖 transform。这侧将来挂在坦克显示壳上，
//       播放逻辑不变——播放器渲染什么不关心对象里跑的是什么逻辑
public class NetSyncObject : MonoBehaviour, ISyncHost
{
    [Header("命令驱动运动学（Host 端模拟用；演示值——真坦克手感来自 UnitConfig）")]
    [SerializeField] private float moveSpeed = 12f;   // 前进/后退速率
    [SerializeField] private float rotateSpeed = 240f; // 转向速率（度/秒）

    [Header("客户端插值参数")]
    [Tooltip("缓冲深度（快照帧数）：延迟预算旋钮——越深越抗抖动、画面越滞后")]
    [SerializeField, Range(1, 10)] private int bufferDepth = 3;

    private readonly InterpBuffer buffer = new InterpBuffer();
    private bool hasRenderPose;
    private Vector2 moveInput; // 最近一条 Move 命令的缓存（按住不放语义：命令间持续生效）

    void Awake()
    {
        buffer.Depth = bufferDepth;
    }

    void Update()
    {
        if (NetManager.Instance != null && NetManager.Instance.IsHost)
        {
            // Host：命令驱动的模拟本体。输入是 [-1,1] 轴值，直接积分：
            //   forward × 油门 × 速度 = 位移（转向用 eulerAngles，跨 0/360 由
            //   客户端插值的 LerpAngle 负责——发送端不做归一化）
            float dt = Time.deltaTime;
            transform.position += transform.forward * (moveInput.y * moveSpeed * dt);
            transform.rotation *= Quaternion.Euler(0f, moveInput.x * rotateSpeed * dt, 0f);
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

    // Host 侧 NetManager 每 tick 采集：模拟本体是 transform 本身，直接读真实姿态
    public (float x, float z, float yaw) CurrentPose()
    {
        Vector3 p = transform.position;
        return (p.x, p.z, transform.eulerAngles.y);
    }

    // Host 侧 NetManager 把上行命令交给模拟对象执行
    public void ApplyCommand(in CommandData cmd)
    {
        if (cmd.kind == CommandKind.Move)
        {
            moveInput = new Vector2(cmd.moveX, cmd.moveY);
        }
        // Fire：假对象无武器语义，NetManager 侧日志证明送达即可；真坦克接入时在此开火
    }

    // Client 侧 NetManager 推送快照
    public void PushSnapshot(in SnapshotData snap) => buffer.Push(snap);
}
