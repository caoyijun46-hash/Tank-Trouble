using UnityEngine;

// 对局节奏与玩法数值（GameManager 读取）：道具周期、结算演出、生成人数。
// 资产引用（prefab/相机）不进 SO，留在 GameManager 场景组件上
[CreateAssetMenu(fileName = "RoundConfig", menuName = "TankGame/Round Config")]
public class RoundConfig : ScriptableObject
{
    [Header("参与者生成")]
    [Tooltip("1vAI 的 AI 数量（双人固定 1v1）")]
    public int aiCount = 1;

    [Header("道具周期生成")]
    public float itemInterval = 10f;
    public int maxItems = 8;
    [Tooltip("随机 Power 池：只列已实现的（Laser/Missile）")]
    public Power[] itemPool = { Power.Laser, Power.Missile };

    [Header("结算演出（慢放 + 特写）")]
    [Tooltip("结算慢放比例（0.12 ≈ 8 倍慢镜）")]
    [Range(0.02f, 0.5f)] public float slowMotion = 0.12f;
    [Tooltip("结算展示时长（真实秒）")]
    [Range(0.5f, 10f)] public float restartDelay = 3f;
}
