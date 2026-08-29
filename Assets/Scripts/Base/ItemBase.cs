using UnityEngine;

/// <summary>
/// 道具基类：玩家坦克触碰后触发效果并自毁。
/// 子类约定：覆写 ApplyEffect 实现具体效果；玩家坦克需挂 "Player" Tag。
/// </summary>
public abstract class ItemBase : MonoBehaviour
{
    protected abstract void ApplyEffect(VehicleBase collector);

    protected virtual void OnTriggerEnter(Collider other)
    {
        if (other.TryGetComponent<VehicleBase>(out var vehicle) && other.CompareTag("Player"))
        {
            ApplyEffect(vehicle);
            Destroy(gameObject);
        }
    }
}
