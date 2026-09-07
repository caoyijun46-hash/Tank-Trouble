using UnityEngine;

// 联机：远端玩家坦克在主机世界的化身。
// Tank（本机 InputSystem）/ TankAI（AI 决策）的输入来源都在各自 Update 里写，
// 它没有本地输入源——输入来自客户端上行命令：NetManager 收命令 →
// ISyncHost.ApplyCommand 喂进来（moveInput 缓存持续生效，fire 直接开火）。
//
// 主机权威：坦克只在本端存在并模拟（真物理/吃子弹/死亡由主机裁决），
// 客户端是快照播放器不模拟它。玩家自己的坦克将来走预测（阶段 3），不吃插值
public class RemoteTank : TankBase, ISyncHost
{
    void Start()
    {
        Init();
    }

    void FixedUpdate()
    {
        Move(); // 与 Tank 同构：rb 物理驱动（linear/angular velocity）
    }

    // ---- ISyncHost ----

    public (float x, float z, float yaw) CurrentPose()
    {
        Vector3 p = transform.position;
        return (p.x, p.z, transform.eulerAngles.y);
    }

    public void ApplyCommand(in CommandData cmd)
    {
        if (cmd.kind == CommandKind.Move)
        {
            // 命令缓存"按住不放"：Move 每帧读它，直到下一条命令覆盖（或松开=0 停车）
            moveInput = new Vector2(cmd.moveX, cmd.moveY);
        }
        else if (cmd.kind == CommandKind.Fire)
        {
            Fire(); // 冷却由 TankBase 内部把关：连点不空发，与 Tank/TankAI 同源
        }
    }
}
