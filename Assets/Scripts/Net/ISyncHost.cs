

// 主机世界"被遥控模拟对象"的契约：NetManager 不关心遥控的是调试 cube 还是真坦克，
// 只依赖这个接口做两件事——每 tick 采集姿态广播、把上行命令喂给对象执行。
// 实现者：NetSyncObject（联调载体，命令驱动运动学）、RemoteTank（真坦克）。
//
// 为什么 NetManager 不直接 [SerializeField] 接口：Unity Inspector 只能序列化
// 具体类/组件引用，不能拖 interface——字段类型放宽为 MonoBehaviour，
// 运行时 as ISyncHost 转换（拖错组件会在 Start 报错提示）
public interface ISyncHost
{
    // Host 每 tick 采集（广播进快照）
    (float x, float z, float yaw) CurrentPose();

    // Host：执行客户端上行命令
    void ApplyCommand(in CommandData cmd);
}
