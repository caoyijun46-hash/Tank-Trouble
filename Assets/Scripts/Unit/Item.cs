using UnityEngine;

public class Item : MonoBehaviour
{
    [SerializeField] private Power itemType;
    [SerializeField] private float rotationSpeed = 50f;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    // Update is called once per frame
    void Update()
    {
        transform.Rotate(Vector3.up * rotationSpeed * Time.deltaTime);
    }
    void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Tank"))
        {
            // 走 SetPower 而非直接写 status 字段：Laser 预览线开关跟随武器状态
            other.gameObject.GetComponent<TankBase>().SetPower(itemType);
            Destroy(gameObject);
        }
    }
    public void SetPower(Power newPower)
    {
        itemType = newPower;
    }
}
