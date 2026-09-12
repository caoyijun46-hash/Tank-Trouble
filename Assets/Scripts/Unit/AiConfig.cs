using UnityEngine;

// TankAI 行为算法参数（决策节流/射击/撤退/躲避/卡墙脱困全部集中）。
// 想调 AI 难度：复制一份资产改数值，Enemy.prefab 拖另一份即可，代码零改动
[CreateAssetMenu(fileName = "AiConfig", menuName = "TankGame/AI Config")]
public class AiConfig : ScriptableObject
{
    [Header("决策节流")]
    public float decideInterval = 0.1f;   // 决策层刷新间隔（秒）
    public int unitRadius = 2;            // 坦克体积半径（格数），网格查询口径
    public int maxRecoverRadius = 10;     // 找回网格的最大搜索半径

    [Header("射击瞄准")]
    public float angleStep = 1f;          // 角度枚举步长（度）：越小越准，越大越"笨"
    public float aimThreshold = 1f;       // 对准判定阈值（度）
    public float fireCooldown = 0.5f;     // 开火冷却（秒）
    public float fireRange = 20f;         // 站桩射击的射程上限，超出改追击
    public float chaseArriveDistance = 4f; // 追击到达判定距离

    [Header("撤退（打带跑）")]
    public int retreatAfterShots = 2;   // 连续开火达几发后转入撤退
    public float retreatDuration = 5f;  // 撤退持续（秒）：到点或弹池恢复即回战斗

    [Header("威胁躲避")]
    public float threatHighThreshold = 1.5f;  // 命中线离中心低于它 → 穿心（高威胁，移动躲）
    public float dodgeRotateStep = 5f;        // 旋转躲避安全角采样步长（度）
    public float threatTimeLimit = 1.2f;      // 威胁到达时间上限：更远的子弹不值得立即反应
    public LayerMask obstacleMask = ~0;       // 弹道模拟的障碍层（Inspector 排除 Bullet 层）

    [Header("卡墙脱困")]
    public float backMinTime = 0.35f;  // 倒车最短时长（秒）：退出前先退开一段
    public float backMaxTime = 1.5f;   // 倒车上限（秒）：防极端死角无限倒车
    public float backClearDist = 2.5f; // 前方净空超过它即视为让开
}
