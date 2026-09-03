using UnityEngine;

public class Laser : BulletBase
{
    private int currentBounce = 0;
    private TrailRenderer trail;

    void Awake()
    {
        init();
        trail = GetComponent<TrailRenderer>();
        Destroy(gameObject, LifeTime);
    }

    void OnEnable()
    {
        move();
    }

    protected override void DestoryBullet()
    {
        trail.emitting = false;
        trail.time *= 3;
        MeshRenderer renderer = GetComponent<MeshRenderer>();
        if (renderer)
        {
            renderer.enabled = false;
        }
        Collider collider = GetComponent<Collider>();
        if (collider)
        {
            collider.enabled = false;
        }
        rb.linearVelocity = Vector3.zero;
        Destroy(gameObject, trail.time);
    }

    protected override void OnCollisionEnter(Collision collision)
    {
        base.OnCollisionEnter(collision);
        if (collision.gameObject.CompareTag("Wall"))
        {
            currentBounce++;
            if (currentBounce > MaxBounces)
            {
                DestoryBullet();
            }
        }
    }
}
