
// 实体帧载荷（块 2 起由单对象快照换代而来）：主机世界里所有已注册实体的一帧状态。
// 一帧 N 个实体 → 客户端每个实体槽独立插值（槽 = InterpBuffer，各自按窗口推进）。
// 时间戳是插值进度的基准（两端时钟速率一致，只差起点偏移，
// 所以插值用"窗口时间跨度"推进即可，不需要绝对时钟同步）
public struct SnapshotData
{
    public uint seq;      // 主机发帧计数（调试/统计用）
    public float hostTime; // 主机 Time.unscaledTime（秒）
    public float x, z;    // 位置（XZ 平面；所有同步实体都在 y=0 平面运动）
    public float yaw;     // 绕 Y 朝向（度）
}

// 消息字节布局（手写读写，无 JSON——体积小、无 GC、字节序可控）：
//   [0]        type: byte  0=实体帧（本文件） 1=命令 2=迷宫参数 3=实体出生 4=实体销毁
//   [1..4]     seq: uint
//   [5..8]     hostTime: float
//   [9]        count: byte  本帧实体数（≤255）
//   [10..]     每实体 13B: id(1) + x(4) + z(4) + yaw(4)
//
// 注意：id 是实体在 host 注册表的身份，跨帧稳定；对象出生/销毁走独立的
// Spawn/Despawn 可靠事件（EntityProtocol），帧只承载姿态——快照是状态流可丢帧，
// Spawn/Despawn 是一次性事件丢不得，可靠性按数据类型选
public static class SnapshotProtocol
{
    public const byte TypeSnapshot = 0;
    public const int FrameHeaderSize = 10;      // type+seq+hostTime+count
    public const int EntityBodySize = 13;       // id+x/z/yaw
    public static int FrameSize(int count) => FrameHeaderSize + EntityBodySize * count;

    // writer 必须 ref 传递！DataStreamWriter 是 struct，按值传进方法后，
    // 方法内 Write 推进的是"拷贝"的写入位置——底层 buffer 共享（字节写进去了）
    // 但调用方的 position 仍是 0 → EndSend 按 position 发出 0 字节空包 →
    // 对端收到空 Data（default reader，一读就 NRE）。这是整个联机 bug 的根因。
    public static void WriteFrameHeader(ref Unity.Collections.DataStreamWriter w, uint seq, float hostTime, byte count)
    {
        w.WriteByte(TypeSnapshot);
        w.WriteUInt(seq);
        w.WriteFloat(hostTime);
        w.WriteByte(count);
    }

    public static void WriteEntity(ref Unity.Collections.DataStreamWriter w, byte id, float x, float z, float yaw)
    {
        w.WriteByte(id);
        w.WriteFloat(x);
        w.WriteFloat(z);
        w.WriteFloat(yaw);
    }

    // 读帧头：先判最小长度（10B）再验 type；长度不足直接 false，不在流上硬读——
    // 读越界会在 DataStreamReader 内部炸 NRE，返回 false 让调用方丢弃更安全。
    // 注意：reader 必须 ref 传递！DataStreamReader 是 struct，按值传进方法后推进的
    // 是"拷贝"的游标——头与实体是分函数分段读的，按值传会让后续 TryReadEntity
    // 永远从 0 重读（把 type/seq/hostTime 当实体数据）。"reader 按值没事"只对
    // 单函数内一次读完的协议成立；跨函数共享游标一律 ref（与 writer 同理）
    public static bool TryReadFrameHeader(ref Unity.Collections.DataStreamReader r, out uint seq, out float hostTime, out byte count)
    {
        seq = 0;
        hostTime = 0f;
        count = 0;
        if (r.Length < FrameHeaderSize)
        {
            return false;
        }
        if (r.ReadByte() != TypeSnapshot)
        {
            return false;
        }
        seq = r.ReadUInt();
        hostTime = r.ReadFloat();
        count = r.ReadByte();
        return true;
    }

    // 读单个实体条目（调用方先读头、按 count 循环调用；每条前再判剩余长度，
    // 半包/截断帧走到哪丢到哪，不炸）。ref 原因同上：跨函数共享读游标
    public static bool TryReadEntity(ref Unity.Collections.DataStreamReader r, out byte id, out float x, out float z, out float yaw)
    {
        id = 0;
        x = z = yaw = 0f;
        if (r.Length < EntityBodySize)
        {
            return false;
        }
        id = r.ReadByte();
        x = r.ReadFloat();
        z = r.ReadFloat();
        yaw = r.ReadFloat();
        return true;
    }
}
