using UnityEngine;

// 坦克手感数值（玩家/AI 共用一份）：移动/旋转速度、射击冷却、弹池大小。
// 拖到 Tank.prefab / Tank 1.prefab / Enemy.prefab 的 TankBase 组件上
[CreateAssetMenu(fileName = "UnitConfig", menuName = "TankGame/Unit Config")]
public class UnitConfig : ScriptableObject
{
    [Header("移动手感")]
    public float moveSpeed = 20f;
    public float rotateSpeed = 240f;

    [Header("射击")]
    public float coolDown = 0.5f;   // 开火冷却（秒）
    public int maxBullets = 5;      // 子弹池上限（普通弹复用池）
}
