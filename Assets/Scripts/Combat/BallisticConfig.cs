using UnityEngine;

// 弹道参数单一来源：子弹、预览、未来 AI 都从这里读，保证一致。
// 每套武器一份资产（普通弹、激光各建一份），prefab 上拖引用
[CreateAssetMenu(fileName = "BallisticConfig", menuName = "TankGame/Ballistic Config")]
public class BallisticConfig : ScriptableObject
{
    public float speed = 20f;
    public float lifeTime = 10f;
    public int maxBounces = 5; // 反弹上限：Laser 的销毁判定 + 预览的显示段数，必须一致
    public float bulletRadius = 0.3f; // 子弹碰撞体半径：弹道模拟（SphereCast）用，与物理子弹一致

    // 派生值：路程上限 = 速度 × 寿命，不手工维护
    public float MaxDistance => speed * lifeTime;
}
