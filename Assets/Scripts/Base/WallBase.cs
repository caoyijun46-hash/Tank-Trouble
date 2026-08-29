using UnityEngine;

/// <summary>
/// 墙壁基类：可被子弹伤害的障碍物。
/// 不可破坏的墙（如钢墙）覆写 TakeDamage 为空实现即可。
/// </summary>
public abstract class WallBase : MonoBehaviour
{
    [SerializeField] protected int hp = 3;

    public virtual void TakeDamage(int damage)
    {
        hp -= damage;
        if (hp <= 0) DestroyWall();
    }

    protected virtual void DestroyWall()
    {
        Destroy(gameObject);
    }
}
