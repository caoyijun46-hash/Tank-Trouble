
// 对局进程事件（块 4a）：host 权威的胜负/开局/暂停状态 → client 呈现。
// 这些是"裁决 + 状态提示"，不是世界姿态——走可靠管道的一次性事件：
//   丢一条 = 比分不对/遮罩不关，需要重传（可靠）；与快照(不可靠状态流)相反。
// client 不自行判定任何东西：比分来自 host，暂停提示来自 host。
//
// 比分带全量（非增量）：host 是唯一写者，全量天然可对账。
//
// 消息字节布局（同一 UDP 流 type 分派；手写读写，无 JSON）：
//   RoundEnd    type=5  [1..4]winner(-1=平局) [5..8]score1 [9..12]score2
//                       [13]camFlag(1=有阵亡点) [14..17]camX [18..21]camZ   = 22B
//   RoundStart  type=6  [1..4]score1 [5..8]score2                            =  9B
//   PauseState  type=7  [1]paused(byte 0/1)                                  =  2B
//   SessionEnd  type=8  [1]reason(0=回主菜单)  —— host 结束会话（点 Menu），
//                       client 收到立即回菜单；意外断线由 Disconnect 兜底        =  2B
//
// RoundEnd 的 cam 字段 = host 结算特写机位（FixedMapCamera.Focus 盯最后阵亡点，
// 无阵亡点则 host 回全图 → camFlag=0，client 忽略坐标）。相机是"演出指令"：
// client 端不自己决定机位，host 裁决 → client 执行
public static class RoundProtocol
{
    public const byte TypeRoundEnd = 5;
    public const byte TypeRoundStart = 6;
    public const byte TypePause = 7;
    public const byte TypeSessionEnd = 8;
    public const int RoundEndSize = 22;
    public const int RoundStartSize = 9;
    public const int PauseSize = 2;
    public const int SessionEndSize = 2;

    public const byte SessionEndToMenu = 0; // reason：回主菜单（当前唯一）

    // writer 必须 ref 传递！DataStreamWriter 是 struct，按值传进方法后写入推进的
    // 是"拷贝"的位置游标 → EndSend 发出 0 字节空包。这是联机历史根因，勿再犯
    public static void WriteRoundEnd(ref Unity.Collections.DataStreamWriter w, int winner, int score1, int score2,
        bool hasCamPoint, float camX, float camZ)
    {
        w.WriteByte(TypeRoundEnd);
        w.WriteInt(winner);
        w.WriteInt(score1);
        w.WriteInt(score2);
        w.WriteByte(hasCamPoint ? (byte)1 : (byte)0);
        w.WriteFloat(camX);
        w.WriteFloat(camZ);
    }

    public static void WriteRoundStart(ref Unity.Collections.DataStreamWriter w, int score1, int score2)
    {
        w.WriteByte(TypeRoundStart);
        w.WriteInt(score1);
        w.WriteInt(score2);
    }

    public static void WritePause(ref Unity.Collections.DataStreamWriter w, bool paused)
    {
        w.WriteByte(TypePause);
        w.WriteByte(paused ? (byte)1 : (byte)0);
    }

    public static void WriteSessionEnd(ref Unity.Collections.DataStreamWriter w, byte reason)
    {
        w.WriteByte(TypeSessionEnd);
        w.WriteByte(reason);
    }

    // 长度防御照抄 CommandProtocol：不足最小长度直接 false，不在流上硬读——
    // 读越界会在 DataStreamReader 内部炸 NRE，返回 false 让调用方丢弃更安全
    public static bool TryReadRoundEnd(Unity.Collections.DataStreamReader r, out int winner, out int score1, out int score2,
        out bool hasCamPoint, out float camX, out float camZ)
    {
        winner = 0;
        score1 = score2 = 0;
        hasCamPoint = false;
        camX = camZ = 0f;
        if (r.Length < RoundEndSize)
        {
            return false;
        }
        if (r.ReadByte() != TypeRoundEnd)
        {
            return false;
        }
        winner = r.ReadInt();
        score1 = r.ReadInt();
        score2 = r.ReadInt();
        hasCamPoint = r.ReadByte() != 0;
        camX = r.ReadFloat();
        camZ = r.ReadFloat();
        return true;
    }

    public static bool TryReadRoundStart(Unity.Collections.DataStreamReader r, out int score1, out int score2)
    {
        score1 = score2 = 0;
        if (r.Length < RoundStartSize)
        {
            return false;
        }
        if (r.ReadByte() != TypeRoundStart)
        {
            return false;
        }
        score1 = r.ReadInt();
        score2 = r.ReadInt();
        return true;
    }

    public static bool TryReadPause(Unity.Collections.DataStreamReader r, out bool paused)
    {
        paused = false;
        if (r.Length < PauseSize)
        {
            return false;
        }
        if (r.ReadByte() != TypePause)
        {
            return false;
        }
        paused = r.ReadByte() != 0;
        return true;
    }

    public static bool TryReadSessionEnd(Unity.Collections.DataStreamReader r, out byte reason)
    {
        reason = SessionEndToMenu;
        if (r.Length < SessionEndSize)
        {
            return false;
        }
        if (r.ReadByte() != TypeSessionEnd)
        {
            return false;
        }
        reason = r.ReadByte();
        return true;
    }
}
