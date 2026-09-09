using UnityEngine;

// 实体注册组件（块 2）：挂在 host 玩法 prefab 根上（Tank 1/Enemy/Item/Bullet/Missile），
// 声明"这个物体要跨端同步"。挂上即自动进注册表，无它不挂 = 不同步
// （墙/地板/粒子特效不挂——迷宫由 MapSpawner 同构生成，不占注册表）。
//
// 生命周期用 OnEnable/OnDisable 而非 Start：池化子弹(Bullet)靠 SetActive 复用，
// Start 只跑一次、复用不触发；OnEnable/OnDisable 每次激活/停用都成对触发，
// 恰好覆盖 出生/死亡/换局清场/池化休眠 全部增删路径。
//   Instantiate(激活) → OnEnable 注册(广播 Spawn)；SetActive(false)/Destroy → 注销(广播 Despawn)
//
// 单机（场景无 NetManager）静默跳过——挂与不挂都不影响单机玩法
public class NetSyncEntity : MonoBehaviour
{
    [Tooltip("client 端壳 prefab 表索引（NetManager.shellPrefabs 对齐）：0=坦克 1=道具 2=子弹 3=导弹")]
    [SerializeField] private byte typeKey;

    /// <summary>host 注册表分配的稳定身份（快照帧/Spawn/Despawn 共用；未注册时无效）</summary>
    public byte SyncId { get; set; }

    public byte TypeKey => typeKey;

    // 采集/出生广播的姿态源：直接读自身 transform（所有同步实体都在 y=0 平面，
    // 坦克由物理推进、道具自转、子弹直线/寻路——对注册表一律只是 x/z/yaw）
    public (float x, float z, float yaw) Pose()
    {
        Vector3 p = transform.position;
        return (p.x, p.z, transform.eulerAngles.y);
    }

    void OnEnable()
    {
        NetManager.Instance?.HostRegisterEntity(this);
    }

    void OnDisable()
    {
        NetManager.Instance?.HostUnregisterEntity(this);
    }
}
