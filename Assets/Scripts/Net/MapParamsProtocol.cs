// 阶段 2.5 迷宫同构载荷：host 生成一张迷宫后，把决策层参数广播给 client——
//  client 用同一组 (cols, rows, seed, loopMin, loopMax) 调 MazeData.Generate，
//  纯函数同参必同图 → 两端世界几何一致（墙/地板/网格坐标可互相参照）。
//
// 消息方向与快照同向（host → client），但语义是一次性状态而非连续流：
//  丢一帧快照可由下一条全量纠正，迷宫参数错一帧 = 整局世界错 → 走可靠管道
//  （可靠流有序，无丢帧概念；seq 无需携带）
//
// 消息字节布局（与快照/命令同一 UDP 流，靠 type 分派；手写读写，无 JSON）：
//   [0]     type    byte  0=快照 1=命令 2=迷宫参数
//   [1..4]  cols    int   决策层列数
//   [5..8]  rows    int   决策层行数
//   [9..12] seed    int   迷宫随机种子
//   [13..16] loopMin float 补开洞比例下界（MazeConfig.loopMin）
//   [17..20] loopMax float 补开洞比例上界（MazeConfig.loopMax）
public struct MapParamsData
{
    public int cols;
    public int rows;
    public int seed;
    public float loopMin;
    public float loopMax;
}

public static class MapParamsProtocol
{
    public const byte TypeMapParams = 2;
    public const int MapParamsSize = 21; // type+cols+rows+seed+loopMin+loopMax

    // writer 必须 ref 传递！DataStreamWriter 是 struct，按值传进方法后写入推进的
    // 是"拷贝"的位置游标 → EndSend 发出 0 字节空包。这是联机历史根因，勿再犯
    public static void WriteMapParams(ref Unity.Collections.DataStreamWriter w, in MapParamsData p)
    {
        w.WriteByte(TypeMapParams);
        w.WriteInt(p.cols);
        w.WriteInt(p.rows);
        w.WriteInt(p.seed);
        w.WriteFloat(p.loopMin);
        w.WriteFloat(p.loopMax);
    }

    // 长度防御照抄 CommandProtocol：数据不足 21B 直接 false，不在流上硬读——
    // 读越界会在 DataStreamReader 内部炸 NRE，返回 false 让调用方丢弃更安全
    public static bool TryReadMapParams(Unity.Collections.DataStreamReader r, out MapParamsData p)
    {
        p = default;
        if (r.Length < MapParamsSize)
        {
            return false;
        }
        if (r.ReadByte() != TypeMapParams)
        {
            return false;
        }
        p.cols = r.ReadInt();
        p.rows = r.ReadInt();
        p.seed = r.ReadInt();
        p.loopMin = r.ReadFloat();
        p.loopMax = r.ReadFloat();
        return true;
    }
}
