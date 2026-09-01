using UnityEngine;

public class GameMnager : MonoBehaviour
{
    private int bulletLayer;
    private int aimLineLayer;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        bulletLayer = LayerMask.NameToLayer("Bullet");
        aimLineLayer = LayerMask.NameToLayer("Aim Line");
        Physics.IgnoreLayerCollision(bulletLayer, bulletLayer, true);
        Physics.IgnoreLayerCollision(bulletLayer, aimLineLayer, true);
    }
    // Update is called once per frame
    void Update()
    {
        
    }
}
