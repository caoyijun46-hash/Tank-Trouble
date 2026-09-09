// "可被上行命令驱动"的契约（命令面）：NetManager 收到 client 命令后喂给当前
// 化身执行，不关心化身是什么——实现者：Tank（联机 1v1 的 client 化身，IsRemote
// 模式下 ApplyCommand 写输入缓存）。
//
// 注意：接口只留命令面。姿态采集不走它——块 2 起由 NetSyncEntity 注册表统一
// 读 transform（所有实体同构），CurrentPose 已删除
public interface ISyncHost
{
    // Host：执行客户端上行命令（Move 写缓存 / Fire 积压触发，语义在实现者内）
    void ApplyCommand(in CommandData cmd);
}
