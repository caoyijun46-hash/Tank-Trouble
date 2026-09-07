using UnityEngine;
using UnityEngine.InputSystem;

// 阶段 2 联调输入源（临时脚本，真坦克接入后删除）：用键盘模拟"远端玩家"的操作意图。
// WASD → Move 状态（20Hz 定频发最新值，松开即发 0=停车）；空格 → Fire 事件（按下发一次）。
// 管道选择在 NetManager.SendCommand 内部，产生方只填 CommandData——协议对输入源透明
public class ClientInputSimulator : MonoBehaviour
{
    [Tooltip("Move 状态发送间隔（秒）：0.05 = 20Hz")]
    [SerializeField, Range(0.02f, 0.2f)] private float stateInterval = 0.05f;

    private float sendTimer;
    private uint moveSeq; // Move 流序号：主机以它判断新旧（不可靠流可能乱序）
    private uint fireSeq; // Fire 流序号（可靠流有序，仅调试用）

    void Update()
    {
        Keyboard kb = Keyboard.current;
        if (kb == null || NetManager.Instance == null)
        {
            return; // 无键盘（移动端）或无主机连接：不产输入也不炸
        }

        // Fire：一次性事件，按下瞬间发一次（可靠管道负责送达）
        if (kb.spaceKey.wasPressedThisFrame)
        {
            NetManager.Instance.SendCommand(new CommandData
            {
                kind = CommandKind.Fire,
                seq = fireSeq++,
            });
        }

        // Move：连续状态按节拍发最新值。键盘静止也照发 (0,0)——保持状态流节奏，
        // 主机"按住不放"的语义才能被明确打断（收到 0 才停车，不用主机猜）
        sendTimer += Time.unscaledDeltaTime;
        if (sendTimer >= stateInterval)
        {
            sendTimer = 0f;
            NetManager.Instance.SendCommand(new CommandData
            {
                kind = CommandKind.Move,
                seq = moveSeq++,
                moveX = ReadAxis(kb),
                moveY = ReadThrottle(kb),
            });
        }
    }

    // 转向轴：A/D，-1 左 / +1 右（与 TankBase.Move 的 moveInput.x 同号约定）
    static float ReadAxis(Keyboard kb)
    {
        float x = 0f;
        if (kb.aKey.isPressed)
        {
            x -= 1f;
        }
        if (kb.dKey.isPressed)
        {
            x += 1f;
        }
        return x;
    }

    // 油门：W/S，+1 前进 / -1 倒车（与 TankBase.Move 的 moveInput.y 同号约定）
    static float ReadThrottle(Keyboard kb)
    {
        float y = 0f;
        if (kb.sKey.isPressed)
        {
            y -= 1f;
        }
        if (kb.wKey.isPressed)
        {
            y += 1f;
        }
        return y;
    }
}
