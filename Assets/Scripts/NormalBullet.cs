using System.Collections;
using UnityEngine;

public class NormalBullet : MonoBehaviour
{
    [SerializeField] private float speed = 20f;
    [SerializeField] private float lifeTime = 10f;
    private Rigidbody rb;
    void Awake()
    {
        rb = GetComponent<Rigidbody>();
    }
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        gameObject.SetActive(false);
    }
    void OnEnable()
    {
        rb.linearVelocity = transform.forward * speed;
        StartCoroutine(DisableBullet());
    }
    void OnDisable()
    {
        rb.linearVelocity = Vector3.zero;
    }
    IEnumerator DisableBullet()
    {
        yield return new WaitForSeconds(lifeTime);
        gameObject.SetActive(false);
    }
    void OnCollisionEnter(Collision collision)
    {
        if(collision.transform.CompareTag("Tank"))
        {
            Destroy(collision.gameObject);
            gameObject.SetActive(false);
        }
    }
}
