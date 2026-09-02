using System.Collections;
using System.Collections.Generic;
using UnityEngine;
public enum Power
    {
        Normal,
        Laser,
        LandMine,
        Missile,
        Bomb
    }
public abstract class TankBase : MonoBehaviour
{
    
    [SerializeField] public Power status = Power.Normal;
    protected Vector2 moveInput = new Vector2();
    protected Rigidbody rb;
    [SerializeField] protected GameObject bulletPrefab;
    [SerializeField] protected GameObject laserPrefab;
    [SerializeField] protected GameObject missilePrefab;
    [SerializeField] protected Transform firePoint;
    protected List<GameObject> pooledBullets = new List<GameObject>();
    [SerializeField] protected int maxBullets = 5; 
    protected GameObject bullet;
    [SerializeField] protected float coolDown = 0.5f;
    protected bool isCoolDown = true;
    [SerializeField] protected float moveSpeed = 20f;
    [SerializeField] protected float rotateSpeed = 240f;


    protected void Move()
    {
        rb.linearVelocity = transform.forward * moveInput.y * moveSpeed + Vector3.up * rb.linearVelocity.y;
        rb.angularVelocity = new Vector3(0, moveInput.x * rotateSpeed * Mathf.Deg2Rad, 0);
    }

    protected void Fire()
    {
        if(isCoolDown)
        {
            isCoolDown = false;
            StartCoroutine(CoolDown());
            if(status == Power.Normal)
            {
                bullet = GetPooledBullet();
                if(bullet != null)
                {
                    bullet.transform.position = firePoint.transform.position;
                    bullet.transform.rotation = firePoint.transform.rotation;
                    bullet.SetActive(true);
                }
            }
            else if(status == Power.Laser)
            {
                Instantiate(laserPrefab, firePoint.position, firePoint.rotation);
                //status = Power.Normal;
            }
            else if(status == Power.Missile)
            {
                Instantiate(missilePrefab, firePoint.position + firePoint.forward * 0.5f, firePoint.rotation);
                //status = Power.Normal;
            }
        }
        
    }
    IEnumerator CoolDown()
    {
        yield return new WaitForSeconds(coolDown);
        isCoolDown = true;
    }
    protected void InitPool()
    {
        for(int i = 1; i <= maxBullets; i++)
        {
            GameObject obj = Instantiate(bulletPrefab);
            obj.SetActive(false);
            pooledBullets.Add(obj);
        }
    }

    GameObject GetPooledBullet()
    {
        for(int i = 0; i < maxBullets; i++)
        {
            if(!pooledBullets[i].activeInHierarchy)
            {
                return pooledBullets[i];
            }
        }
        return null;
    }
    protected void Init()
    {
        rb = gameObject.GetComponent<Rigidbody>();
        InitPool();
    }
}
