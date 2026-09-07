

// 阶段 2 上行命令载荷：客户端"操作意图" → 主机执行。
// 与快照(SnapshotProtocol)相反的方向：快照是状态（我在哪），命令是意图（我想怎么动）。
//
// 命令按性质分两类，走不同管道（接收端不区分管道，只有发送端换 BeginSend 重载）：
//   Move（连续状态）：按键按住不放，20Hz 定频发最新值。丢了无感——主机沿用上一条，
//       下一条马上覆盖。→ 默认不可靠管道。因不可靠流可能乱序，必须带自增 seq，
//       主机只应用比已用更新的包，迟到的旧包直接弃（否则坦克会被旧输入"倒退"）。
//   Fire（一次性事件）：按下那一刻发生一次。丢了=这次攻击没了。→ 可靠管道
//       （ReliableSequencedPipelineStage：重传直到确认）。可靠流有序，seq 仅调试用。
//
// 消息字节布局（与快照同一 UDP 流，靠 type 分派；手写读写，无 JSON）：
//   [0]     type  byte   0=快照(见 SnapshotProtocol) 1=命令
//   [1]     kind  byte   0=Move 1=Fire
//   [2..5]  seq   uint   发送端自增（Move：乱序丢弃基准；Fire：调试）
//   Move 额外: [6..9] moveX float 转向轴 [-1,1]（正=右转）
//              [10..13] moveY float 油门 [-1,1]（正=前进，倒车为负）
//   Fire 无额外字段：主机执行时坦克当前武器/朝向是主机世界已知状态，不必随命令带
public enum CommandKind : byte
{
    Move = 0,
    Fire = 1,
}

public struct CommandData
{
    public CommandKind kind;
    public uint seq;
    public float moveX; // kind=Move 时有效
    public float moveY; // kind=Move 时有效
}

public static class CommandProtocol
{
    public const byte TypeCommand = 1;
    public const int FireSize = 6;  // type+kind+seq
    public const int MoveSize = 14; // + moveX/moveY

    // writer 必须 ref 传递！DataStreamWriter 是 struct，按值传进方法后写入推进的
    // 是"拷贝"的位置游标 → EndSend 发出 0 字节空包。这是联机历史根因，勿再犯
    public static void WriteMove(ref Unity.Collections.DataStreamWriter w, uint seq, float moveX, float moveY)
    {
        w.WriteByte(TypeCommand);
        w.WriteByte((byte)CommandKind.Move);
        w.WriteUInt(seq);
        w.WriteFloat(moveX);
        w.WriteFloat(moveY);
    }

    public static void WriteFire(ref Unity.Collections.DataStreamWriter w, uint seq)
    {
        w.WriteByte(TypeCommand);
        w.WriteByte((byte)CommandKind.Fire);
        w.WriteUInt(seq);
    }

    // 变长防御：DataStreamReader.Length 是"剩余未读字节"——先保证公共头 6B，
    // 读到 kind 后再按 kind 校验各自的附加长度。读越界会在流内部抛异常，
    // 逐段判长度返回 false 让调用方丢弃更安全（阶段 1 的 NRE 教训）
    public static bool TryRead(Unity.Collections.DataStreamReader r, out CommandData cmd)
    {
        cmd = default;
        if (r.Length < 6) // 所有命令最少 type+kind+seq
        {
            return false;
        }
        if (r.ReadByte() != TypeCommand)
        {
            return false;
        }
        byte kindRaw = r.ReadByte();
        if (kindRaw != (byte)CommandKind.Move && kindRaw != (byte)CommandKind.Fire)
        {
            return false; // 未知 kind：协议演进期的安全丢弃
        }
        cmd.kind = (CommandKind)kindRaw;
        cmd.seq = r.ReadUInt();
        if (cmd.kind == CommandKind.Move)
        {
            if (r.Length < 8) // move 还需 moveX/moveY
            {
                return false;
            }
            cmd.moveX = r.ReadFloat();
            cmd.moveY = r.ReadFloat();
        }
        return true;
    }
}
