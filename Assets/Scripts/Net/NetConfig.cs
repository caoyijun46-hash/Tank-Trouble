using UnityEngine;

// 联机手感/网络行为参数（Assets/Config/NetConfig.asset 唯一一份，多组件共用）：
// 手感旋钮集中在一处——快照率与插值深度是"延迟 vs 平滑"的主旋钮，此前散落在
// 场景（快照率）与 4 个壳 prefab（缓冲深度），调一次要改多处且易漏。
//
// 消费方：NetManager(快照率/壳超时)、SnapshotPlayer(插值深度)、
// ClientInputSimulator(输入上行频率)、LanDiscovery(房间过期)。
// 组件内兜底常量仅供缺配不崩，不是配置源（与 UnitConfig 等同一纪律）
[CreateAssetMenu(fileName = "NetConfig", menuName = "TankGame/Net Config")]
public class NetConfig : ScriptableObject
{
    [Header("同步")]
    [Tooltip("快照间隔（秒）：0.03 = 33Hz。越小越跟手但流量/抖动成本越高")]
    [Range(0.01f, 0.2f)] public float snapshotInterval = 0.03f;

    [Tooltip("客户端插值缓冲深度（快照帧数）：延迟预算旋钮——越深越抗抖动、画面越滞后")]
    [Range(1, 10)] public int interpBufferDepth = 3;

    [Header("容错")]
    [Tooltip("壳超时清理（秒）：此间无快照即删。Despawn 可靠几乎不丢，兜底防断线残壳")]
    [Range(1f, 10f)] public float shellTimeout = 3f;

    [Tooltip("client 上行输入发送频率（秒）：0.05 = 20Hz")]
    [Range(0.02f, 0.2f)] public float inputSendInterval = 0.05f;

    [Tooltip("局域网房间过期时间（秒）：host 每秒广播，超时无包视为掉线")]
    [Range(1f, 10f)] public float discoveryExpire = 3f;
}
