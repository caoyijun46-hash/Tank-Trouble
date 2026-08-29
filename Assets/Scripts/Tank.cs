using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
public class Tank : MonoBehaviour
{
    [SerializeField]private InputAction moveAction;
    private Vector2 moveInput = new Vector2();

    [SerializeField]private InputAction fireAction;
    private Rigidbody rb;

    [SerializeField] private GameObject bulletPrefab;
    [SerializeField] private Transform firePoint;

    private List<GameObject> pooledBullets = new List<GameObject>();
    [SerializeField] public int maxBullets = 5; 
    private GameObject bullet;

    [SerializeField] private float moveSpeed = 20f;
    [SerializeField] private float rotateSpeed = 120f;
    void Awake()
    {
        rb = gameObject.GetComponent<Rigidbody>();
    }
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        moveAction.Enable();
        fireAction.Enable();

        InitPool();

    }
    // Update is called once per frame
    void Update()
    {
        moveInput = moveAction.ReadValue<Vector2>();
        if (fireAction.triggered)
        {
            Fire();
        }
    }
    void FixedUpdate()
    {
        Move();
    }
    void Move()
    {
        rb.linearVelocity = transform.forward * moveInput.y * moveSpeed + Vector3.up * rb.linearVelocity.y;
        rb.angularVelocity = new Vector3(0, moveInput.x * rotateSpeed * Mathf.Deg2Rad, 0);
    }
    void Fire()
    {
        if(GetPooledBullet() != null)
        {
            bullet = GetPooledBullet();
            bullet.SetActive(true);
            bullet.transform.position = firePoint.transform.position;
            bullet.transform.rotation = firePoint.transform.rotation;
        }
    }
    void InitPool()
    {
        for(int i = 1; i <= maxBullets; i++)
        {
            GameObject bullet = Instantiate(bulletPrefab);
            bullet.SetActive(false);
            pooledBullets.Add(bullet);
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
}
