using System.Collections;
using UnityEngine;

public class Bullet : BulletBase
{
    void Awake()
    {
        init();
    }
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void OnEnable()
    {
        move();
        StartCoroutine(DisableBullet());
    }
    IEnumerator DisableBullet()
    {
        yield return new WaitForSeconds(LifeTime);
        DestoryBullet();
    }
    protected override void DestoryBullet()
    {
        gameObject.SetActive(false);
    }
}
