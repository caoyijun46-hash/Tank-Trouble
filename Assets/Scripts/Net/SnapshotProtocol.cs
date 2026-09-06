

// 阶段 1 快照载荷：主机世界里一个移动对象的位置/朝向 + 主机时间戳。
// 时间戳是插值进度的基准（两端时钟速率一致，只差起点偏移，
// 所以插值用"窗口时间跨度"推进即可，不需要绝对时钟同步）
public struct SnapshotData
{
    public uint seq;      // 主机发帧计数（调试/统计用）
    public float hostTime; // 主机 Time.time（秒）
    public float x, z;    // 位置（XZ 平面；假对象不做 y 变化）
    public float yaw;     // 绕 Y 朝向（度）
}

// 消息字节布局（手写读写，无 JSON——体积小、无 GC、字节序可控）：
//   [0]        type: byte（0=snapshot；未来扩展 1=命令 2=事件）
//   [1..4]     seq: uint
//   [5..8]     hostTime: float
//   [9..20]    x, z, yaw: float × 3
public static class SnapshotProtocol
{
    public const byte TypeSnapshot = 0;

    // 关键：writer 必须 ref 传递！DataStreamWriter 是 struct，按值传进方法后，
    // 方法内 Write 推进的是"拷贝"的写入位置——底层 buffer 共享（字节写进去了）
    // 但调用方的 position 仍是 0 → EndSend 按 position 发出 0 字节空包 →
    // 对端收到空 Data（default reader，一读就 NRE）。这是整个联机 bug 的根因。
    public static void WriteSnapshot(ref Unity.Collections.DataStreamWriter w, in SnapshotData s)
    {
        w.WriteByte(TypeSnapshot);
        w.WriteUInt(s.seq);
        w.WriteFloat(s.hostTime);
        w.WriteFloat(s.x);
        w.WriteFloat(s.z);
        w.WriteFloat(s.yaw);
    }

    // 阶段 1 只有快照一种消息：type 不对直接判失败（调用方丢弃）。
    // 长度防御：数据不足 21 字节（空包/半包）直接判失败，不在流上硬读——
    // 读越界会在 DataStreamReader 内部炸 NRE，返回 false 让调用方丢弃更安全
    public const int SnapshotSize = 21;

    public static bool TryReadSnapshot(Unity.Collections.DataStreamReader r, out SnapshotData s)
    {
        s = default;
        if (r.Length < SnapshotSize)
        {
            return false;
        }
        if (r.ReadByte() != TypeSnapshot)
        {
            return false;
        }
        s.seq = r.ReadUInt();
        s.hostTime = r.ReadFloat();
        s.x = r.ReadFloat();
        s.z = r.ReadFloat();
        s.yaw = r.ReadFloat();
        return true;
    }
}
