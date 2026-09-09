using UnityEngine;
using UnityEngine.InputSystem;

// 本机玩家坦克：默认吃本地键盘（InputAction）。
// 联机 1v1 里它兼任两种角色：
//   1. host 本机玩家车 —— 照常读键盘（IsRemote=false）
//   2. 远端 client 玩家的化身（Tank 1 prefab 实例）——IsRemote 由 GameManager
//      联机分支置位，输入来自上行命令缓存（ApplyCommand），不读键盘。
// 曾用 RemoteTank 承担"命令驱动"职责，本类加入 remote 模式后它已删除——
// 两种输入模式收进一个类，避免双输入真相源并存。
public class Tank : TankBase, ISyncHost
{
    [SerializeField] private InputAction moveAction;
    [SerializeField] private InputAction fireAction;

    /// <summary>联机：本车是远端 client 玩家的化身，不吃本地键盘</summary>
    public bool IsRemote { get; set; }

    private Vector2 netMove;     // 命令缓存（"按住不放"语义：命令间持续生效，收到 0 才停）
    private bool netFirePending; // Fire 事件积压（下一 Update 消费；冷却在 TankBase.Fire 把关）

    void Start()
    {
        Init();
        moveAction.Enable();
        fireAction.Enable();
    }

    void Update()
    {
        if (IsRemote)
        {
            // 化身：输入唯一来源是命令缓存，键盘动作全部忽略
            moveInput = netMove;
            if (netFirePending)
            {
                netFirePending = false;
                Fire();
            }
            return;
        }
        moveInput = moveAction.ReadValue<Vector2>();
        if (fireAction.WasPressedThisFrame())
        {
            Fire();
        }
    }

    void FixedUpdate()
    {
        Move(); // 本机与化身同走物理驱动（rb velocity），Move 读 moveInput 不区分来源
    }

    // ---- ISyncHost（命令面；姿态采集由 NetSyncEntity/注册表读 transform，不走接口）----

    public void ApplyCommand(in CommandData cmd)
    {
        if (cmd.kind == CommandKind.Move)
        {
            netMove = new Vector2(cmd.moveX, cmd.moveY);
        }
        else if (cmd.kind == CommandKind.Fire)
        {
            netFirePending = true;
        }
    }
}
