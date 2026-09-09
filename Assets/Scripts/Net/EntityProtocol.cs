
// 实体生命周期载荷（块 2）：host 世界对象出生/销毁的一次性事件。
// 快照帧只传姿态，传不了"这个 id 是什么/还活着吗"——壳的创建与销毁靠这两个事件：
//   Spawn：host 注册表给新对象分配 id 时发 → client 按 typeKey 实例化对应壳 prefab
//   Despawn：对象注销（销毁/池化停用）时发 → client 删壳
// 可靠性：一次性事件丢不得（丢了壳永不出现/永远残留），走可靠管道；
//   与快照（不可靠状态流）相反——可靠性按数据类型选
//
// typeKey = client 端壳 prefab 表索引（NetManager.shellPrefabs），host 端
//   玩法 prefab 上 NetSyncEntity.typeKey 与之对齐。默认约定：
//   0=坦克（玩家/AI 共用 Dead Tank） 1=道具(Dead Item) 2=子弹(Dead Bullet) 3=导弹(Dead Missile)
//
// 消息字节布局（与快照同一 UDP 流，靠 type 分派；手写读写，无 JSON）：
//   Spawn   [0] type=3  [1] id  [2] typeKey  [3..14] x/z/yaw float×3      = 15B
//   Despawn [0] type=4  [1] id                                            =  2B
public static class EntityProtocol
{
    public const byte TypeSpawn = 3;
    public const byte TypeDespawn = 4;
    public const int SpawnSize = 15;
    public const int DespawnSize = 2;

    // writer 必须 ref 传递！DataStreamWriter 是 struct，按值传进方法后写入推进的
    // 是"拷贝"的位置游标 → EndSend 发出 0 字节空包。这是联机历史根因，勿再犯
    public static void WriteSpawn(ref Unity.Collections.DataStreamWriter w, byte id, byte typeKey,
        float x, float z, float yaw)
    {
        w.WriteByte(TypeSpawn);
        w.WriteByte(id);
        w.WriteByte(typeKey);
        w.WriteFloat(x);
        w.WriteFloat(z);
        w.WriteFloat(yaw);
    }

    public static void WriteDespawn(ref Unity.Collections.DataStreamWriter w, byte id)
    {
        w.WriteByte(TypeDespawn);
        w.WriteByte(id);
    }

    // 长度防御照抄 CommandProtocol：不足最小长度直接 false，不在流上硬读——
    // 读越界会在 DataStreamReader 内部炸 NRE，返回 false 让调用方丢弃更安全
    public static bool TryReadSpawn(Unity.Collections.DataStreamReader r, out byte id, out byte typeKey,
        out float x, out float z, out float yaw)
    {
        id = 0;
        typeKey = 0;
        x = z = yaw = 0f;
        if (r.Length < SpawnSize)
        {
            return false;
        }
        if (r.ReadByte() != TypeSpawn)
        {
            return false;
        }
        id = r.ReadByte();
        typeKey = r.ReadByte();
        x = r.ReadFloat();
        z = r.ReadFloat();
        yaw = r.ReadFloat();
        return true;
    }

    public static bool TryReadDespawn(Unity.Collections.DataStreamReader r, out byte id)
    {
        id = 0;
        if (r.Length < DespawnSize)
        {
            return false;
        }
        if (r.ReadByte() != TypeDespawn)
        {
            return false;
        }
        id = r.ReadByte();
        return true;
    }
}
