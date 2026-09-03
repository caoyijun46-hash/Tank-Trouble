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
            other.gameObject.GetComponent<TankBase>().status = itemType;
            Destroy(gameObject);
        }
    }
    public void SetPower(Power newPower)
    {
        itemType = newPower;
    }
}
