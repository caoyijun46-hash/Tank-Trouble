using UnityEngine;

public abstract class BulletBase : MonoBehaviour
{
    [SerializeField] protected BallisticConfig config;
    protected Rigidbody rb;

    protected float Speed => config != null ? config.speed : 20f;
    protected float LifeTime => config != null ? config.lifeTime : 10f;
    protected int MaxBounces => config != null ? config.maxBounces : 5;

    // 物理真实运动方向：旋转锁定时物理反弹只改 velocity 不改 forward，预测弹道必须用它
    public Vector3 VelocityDirection => rb != null ? rb.linearVelocity.normalized : transform.forward;

    protected void init()
    {
        rb = GetComponent<Rigidbody>();
    }

    protected void move()
    {
        rb.linearVelocity = transform.forward * Speed;
    }

    protected virtual void OnCollisionEnter(Collision collision)
    {
        if (collision.transform.CompareTag("Tank"))
        {
            Destroy(collision.gameObject);
            DestoryBullet();
        }
    }

    protected abstract void DestoryBullet();
}
