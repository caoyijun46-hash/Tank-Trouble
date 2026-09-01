using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
public class Tank : TankBase
{
    [SerializeField] private InputAction moveAction;
    

    [SerializeField] private InputAction fireAction;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        Init();
        moveAction.Enable();
        fireAction.Enable();
    }
    // Update is called once per frame
    void Update()
    {
        moveInput = moveAction.ReadValue<Vector2>();
        if (fireAction.WasPressedThisFrame())
        {
            Fire();
        }
    }
    void FixedUpdate()
    {
        Move();
    }
    
    
    
    
}
