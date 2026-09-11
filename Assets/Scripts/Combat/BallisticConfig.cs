using UnityEngine;

// 弹道参数单一来源：子弹、预览、未来 AI 都从这里读，保证一致。
// 每套武器一份资产（普通弹、激光、导弹各一份），prefab 上拖引用。
// 追踪弹扩展字段只有 Missile 资产填写，其余弹道资产留默认即可
[CreateAssetMenu(fileName = "BallisticConfig", menuName = "TankGame/Ballistic Config")]
public class BallisticConfig : ScriptableObject
{
    public float speed = 20f;
    public float lifeTime = 10f;
    public int maxBounces = 5; // 反弹上限：Laser 的销毁判定 + 预览的显示段数，必须一致
    public float bulletRadius = 0.3f; // 子弹碰撞体半径：弹道模拟（SphereCast）用，与物理子弹一致

    [Header("追踪弹扩展（仅 Missile 使用——填在 Ballistic_Missile.asset）")]
    [Tooltip("转向速率（度/秒）")]
    public float turnSpeed = 360f;
    [Tooltip("路径重算间隔（秒）：目标在动，周期性重规划")]
    public float repathInterval = 0.3f;
    [Tooltip("到达路径点的判定距离")]
    public float arriveDistance = 0.6f;
    [Tooltip("导弹体积半径（格数）")]
    public int unitRadius = 0;
    [Tooltip("找回网格的最大搜索半径（格数）")]
    public int maxRecoverRadius = 10;

    // 派生值：路程上限 = 速度 × 寿命，不手工维护
    public float MaxDistance => speed * lifeTime;
}
