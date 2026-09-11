using System.Text;

// 局域网发现协议（独立于 UTP 游戏传输层）：发现走自己的 UdpClient / 发现端口，
// "发现"与"传输"解耦——游戏同步链路(7783)完全不知道它的存在。
//
// 两种包（全部小端字节，名字 UTF-8，≤32 字节，超长截断）：
//   公告 Announce（host 每秒广播 → 255.255.255.255:7781）：
//     magic(4 "tklr") + ver(1) + type(1=0) + state(1: 0=等待中 1=游戏中)
//     + roomLen(1) + roomName + playerLen(1) + playerName
//   加入请求 Join（client 单播 → host:7781，等待房主开始时每秒重发）：
//     magic(4) + ver(1) + type(1=1) + nameLen(1) + playerName
//
// 读端防御：长度不足 / magic 不符 / 版本不符 / 未知 type / 名字长度越界 → false 丢弃。
// 这里用 System.BitConverter 手拼字节：方法不跨调用共享游标，不存在 UTP
// DataStreamReader 的 ref 陷阱；所有解析都在单函数内完成
public static class DiscoveryProtocol
{
    public const int Port = 7781;          // 公告广播端口（host 发 / client 收）
    // join 单播走独立端口：公告与 join 同端口时，一台机器上会有多个 socket
    // （host 的 beacon、各进程的 discovery）bind 同一端口，单播的投递归属
    // 不确定——join 可能被不含该逻辑的 socket 抢收。端口分离后每端口单 socket，
    // 单播必达（同机双 Editor 测试与真机行为一致）
    public const int JoinPort = 7782;
    public const byte Ver = 1;
    public const byte TypeAnnounce = 0;
    public const byte TypeJoin = 1;
    public const int MaxNameBytes = 32;    // 名字字节上限（截断保护）

    static readonly byte[] Magic = { (byte)'t', (byte)'k', (byte)'l', (byte)'r' };

    // ---------- 公告 ----------

    public static byte[] BuildAnnounce(bool playing, string roomName, string playerName)
    {
        byte[] room = EncodeName(roomName);
        byte[] player = EncodeName(playerName);
        var buf = new byte[8 + room.Length + 1 + player.Length];
        int i = 0;
        WriteMagic(buf, ref i);
        buf[i++] = Ver;
        buf[i++] = TypeAnnounce;
        buf[i++] = playing ? (byte)1 : (byte)0;
        buf[i++] = (byte)room.Length;
        room.CopyTo(buf, i);
        i += room.Length;
        buf[i++] = (byte)player.Length;
        player.CopyTo(buf, i);
        return buf;
    }

    public static bool TryParseAnnounce(byte[] data, int len, out bool playing, out string roomName, out string playerName)
    {
        playing = false;
        roomName = playerName = "";
        if (len < 8 || !HasMagic(data, len) || data[4] != Ver || data[5] != TypeAnnounce)
        {
            return false;
        }
        int i = 6;
        playing = data[i++] != 0;
        if (!TryReadName(data, len, ref i, out roomName))
        {
            return false;
        }
        return TryReadName(data, len, ref i, out playerName);
    }

    // ---------- 加入请求 ----------

    public static byte[] BuildJoin(string playerName)
    {
        byte[] player = EncodeName(playerName);
        var buf = new byte[7 + player.Length];
        int i = 0;
        WriteMagic(buf, ref i);
        buf[i++] = Ver;
        buf[i++] = TypeJoin;
        buf[i++] = (byte)player.Length;
        player.CopyTo(buf, i);
        return buf;
    }

    public static bool TryParseJoin(byte[] data, int len, out string playerName)
    {
        playerName = "";
        if (len < 7 || !HasMagic(data, len) || data[4] != Ver || data[5] != TypeJoin)
        {
            return false;
        }
        int i = 6;
        return TryReadName(data, len, ref i, out playerName);
    }

    // ---------- 工具 ----------

    static void WriteMagic(byte[] buf, ref int i)
    {
        Magic.CopyTo(buf, i);
        i += Magic.Length;
    }

    static bool HasMagic(byte[] data, int len)
    {
        if (len < Magic.Length)
        {
            return false;
        }
        for (int i = 0; i < Magic.Length; i++)
        {
            if (data[i] != Magic[i])
            {
                return false;
            }
        }
        return true;
    }

    // 读 1B 长度 + 该长度的 UTF-8 名字（长度越界→ false；名可能为空）
    static bool TryReadName(byte[] data, int len, ref int i, out string name)
    {
        name = "";
        if (i >= len)
        {
            return false;
        }
        int n = data[i++];
        if (i + n > len)
        {
            return false;
        }
        if (n > 0)
        {
            name = Encoding.UTF8.GetString(data, i, n);
        }
        i += n;
        return true;
    }

    // UTF-8 编码 + 按字节截断到上限（截断可能切坏多字节序列，解码端容错即可）
    static byte[] EncodeName(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return System.Array.Empty<byte>();
        }
        byte[] b = Encoding.UTF8.GetBytes(s);
        if (b.Length <= MaxNameBytes)
        {
            return b;
        }
        var cut = new byte[MaxNameBytes];
        System.Array.Copy(b, cut, MaxNameBytes);
        return cut;
    }
}
