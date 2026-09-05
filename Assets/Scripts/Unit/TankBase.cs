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
    protected GameObject bullet;
    protected bool isCoolDown = true;
    // 手感数值单一来源（Assets/Config/UnitConfig.asset，三个坦克 prefab 共用一份）。
    // Init 时缓存进下方字段（Move/Fire 热路径不查资产）；不设组件侧第二套字段——
    // 调手感只改资产一处
    [SerializeField] protected UnitConfig unitConfig;
    protected float moveSpeed;
    protected float rotateSpeed;
    protected float coolDown;
    protected int maxBullets;
    // 移动扬尘：FX_DirtSplatter（looping 常驻型），Start 时实例化为子物体，
    // 由 Move 每帧按实际速度接管 Play/Stop——prefab 的 playOnAwake=1 必须先压住
    [SerializeField] protected GameObject dirtPrefab;
    [SerializeField] protected Vector3 dirtOffset; // 相对坦克根的位置（0 = prefab 发射器已贴地）
    private ParticleSystem dirt;
    // 死亡爆炸（一次性粒子）：Explosion_Red/Orange/Black 任选，拖到各坦克
    // prefab 上（阵营分色自己定）。见 Die()
    [SerializeField] protected GameObject explosionPrefab;

    // 任意坦克被击杀时广播位置（静态：不依赖场景对象引用）。
    // GameManager 订阅它记"最后阵亡点"作平局特写机位；换局清场不触发
    public static event System.Action<Vector3> AnyDied;


    protected void Move()
    {
        rb.linearVelocity = transform.forward * moveInput.y * moveSpeed + Vector3.up * rb.linearVelocity.y;
        rb.angularVelocity = new Vector3(0, moveInput.x * rotateSpeed * Mathf.Deg2Rad, 0);
        UpdateDirt();
    }

    // 扬尘开关以"物理结算后的实际水平速度"为准（Move 里刚赋的值还没被物理
    // 步进修正）——顶墙时每帧重设的 20 会被墙消掉，下一帧读到的近 0 → 不喷；
    // 倒车（moveInput.y<0）同样产生速度 → 也喷，语义正确
    void UpdateDirt()
    {
        if (dirt == null)
        {
            return;
        }
        Vector3 v = rb.linearVelocity;
        v.y = 0f;
        bool rolling = v.sqrMagnitude > 0.25f; // 阈值 0.5 m/s
        if (rolling && dirt.isStopped)
        {
            dirt.Play();
        }
        else if (!rolling && dirt.isPlaying)
        {
            dirt.Stop();
        }
    }

    protected void Fire()
    {
        if(isCoolDown)
        {
            isCoolDown = false;
            AudioManager.PlayFire(); // 冷却放行 = 真正开火（玩家/AI 同源）
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
                status = Power.Normal;
            }
            else if(status == Power.Missile)
            {
                GameObject missile = Instantiate(missilePrefab, firePoint.position + firePoint.forward * 0.5f, firePoint.rotation);
                missile.GetComponent<Missile>().owner = gameObject;
                status = Power.Normal;
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
        ApplyUnitConfig();
        InitPool();
        InitDirt();
    }

    // 手感参数只在 Init 读一次进缓存字段（Move/Fire 每帧热路径不查资产）
    void ApplyUnitConfig()
    {
        if (unitConfig == null)
        {
            Debug.LogError($"{name}: 缺 UnitConfig 引用（Tank/Tank 1/Enemy prefab 组件上拖 Assets/Config/UnitConfig.asset）", this);
            moveSpeed = 20f;   // 兜底常量仅防缺配崩溃，不是第二配置源
            rotateSpeed = 240f;
            coolDown = 0.5f;
            maxBullets = 5;
            return;
        }
        moveSpeed = unitConfig.moveSpeed;
        rotateSpeed = unitConfig.rotateSpeed;
        coolDown = unitConfig.coolDown;
        maxBullets = unitConfig.maxBullets;
    }

    // dirt 实例化为坦克子物体（随坦克移动/销毁自动跟随）；立即 Stop 压住
    // playOnAwake 的首播，之后由 UpdateDirt 按速度接管
    void InitDirt()
    {
        if (dirtPrefab == null)
        {
            return;
        }
        dirt = Instantiate(dirtPrefab, transform).GetComponent<ParticleSystem>();
        dirt.transform.localPosition = dirtOffset;
        dirt.Stop();
    }

    // 被击杀的唯一入口：死亡爆炸 + 销毁。子弹命中走这里；GameManager 换局
    // 清场是裸 Destroy（不经过 Die）→ 死因天然分流，不会出现换局烟花
    public void Die()
    {
        AudioManager.PlayCrash(); // 死亡爆炸音（与粒子同点触发）
        SpawnExplosion(transform.position);
        AnyDied?.Invoke(transform.position); // GameManager 记机位（平局特写）
        Destroy(gameObject);
    }

    // 一次性爆炸（duration 0.1s、stopAction=None 播完不销毁）→ 按
    // main.duration + 粒子寿命估全灭时刻，延时销毁兜底
    void SpawnExplosion(Vector3 point)
    {
        if (explosionPrefab == null)
        {
            return;
        }
        GameObject boom = Instantiate(explosionPrefab, point, Quaternion.identity);
        ParticleSystem ps = boom.GetComponent<ParticleSystem>();
        float total = ps.main.duration + ps.main.startLifetime.constantMax;
        Destroy(boom, Mathf.Max(total, 1f) + 0.5f);
    }
}
