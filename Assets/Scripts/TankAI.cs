using System.Collections.Generic;
using UnityEngine;

// 坦克 AI：决策层（节流）在"躲避 > 射击 > 追击"间切换，执行层（每帧）按模式驱动 moveInput。
// 与玩家 Tank 对称：玩家注入 InputAction，AI 注入决策结果（moveInput + Fire）
public class TankAI : TankBase
{
    private enum AIAction { Shoot, Chase, DodgeMove, DodgeRotate }

    [SerializeField] private float decideInterval = 0.1f;   // 决策节流间隔（秒）
    [SerializeField] private float angleStep = 1f;          // 角度枚举步长（度）：越小越准，越大越"笨"
    [SerializeField] private float aimThreshold = 1f;       // 对准判定阈值（度）
    [SerializeField] private float fireCooldown = 0.5f;     // 开火冷却（秒）
    [SerializeField] private float fireRange = 20f;         // 射击射程：弹道长度低于它才站桩射击，否则追击
    [SerializeField] private float chaseArriveDistance = 4f; // 追击到达判定距离（接近目标就停，等切换射击）
    [SerializeField] private int unitRadius = 2;            // 坦克体积半径（格数）
    [SerializeField] private int maxRecoverRadius = 10;     // 找回网格的最大搜索半径
    [SerializeField] private float threatHighThreshold = 1.5f;  // 命中点离中心低于它 → 穿心（高威胁，移动躲）
    [SerializeField] private float dodgeRotateStep = 5f;        // 旋转躲避角度采样步长：量化精度（15° 会导致过转/不转）
    [SerializeField] private float threatTimeLimit = 1.2f;      // 威胁到达时间上限：更远的子弹不值得立即反应（避免过早躲避 + 虚警刷屏）
    [SerializeField] private BallisticConfig config;        // 与子弹同源：模拟参数和实际一致
    [SerializeField] private LayerMask obstacleMask = ~0;   // Inspector 里排除 Bullet 层

    private Transform target;
    private AIAction action = AIAction.Shoot;
    private float? fireAngle;      // 射击模式的瞄准角；null = 没有可命中角度
    private List<Vector2Int> path; // 追击模式的寻路路径
    private int pathIndex;
    private float decideTimer;
    private float cooldownTimer;

    private bool threatIsHigh;        // 当前威胁等级（决策结果）
    private Vector3 threatDir;        // 威胁子弹轨迹方向（水平）
    private Vector3 threatHitPoint;   // 威胁子弹命中点
    private float threatArriveTime;   // 威胁子弹到达时间（时间预算用）
    private float threatSafeAngle;    // 旋转躲避的安全角（决策层算一次，执行层只消费）

    // 卡住检测：有移动输入但位置几乎不动（顶墙）→ 倒车脱困
    private Vector3 lastPos;
    private float stuckTimer;
    private bool isStuck;
    private float backingTimer; // 倒车状态计时：倒车期间不计 stuck，结束后自动恢复

    // 角度枚举复用缓存，避免每秒数千次 PathResult 分配
    private readonly PathResult reuse = new PathResult();
    private readonly PathResult reuseThreat = new PathResult();
    private Collider selfCollider;

    // 坦克碰撞体本地半尺寸（XZ）：prefab Scale (3,2,4) → x 半宽 1.5，z 半长 2。
    // 必须与碰撞箱对齐：写反会让旋转躲避判定"安全"的角度实际被击中
    private static readonly Vector2 TankHalfExtents = new Vector2(1.5f, 2f);

    void Start()
    {
        Init();
        selfCollider = GetComponent<Collider>(); // SphereCast 枚举起点在自身碰撞体内，必须忽略
        lastPos = transform.position;
    }

    // 决策层：节流执行，输出"意图"（模式 + 参数）
    void Update()
    {
        decideTimer -= Time.deltaTime;
        if (decideTimer <= 0f)
        {
            Decide();
            decideTimer = decideInterval;
        }
    }

    // 执行层：每帧把意图变成输入
    void FixedUpdate()
    {
        Execute();
        UpdateStuckState();
        Move(); // TankBase 物理移动（moveInput 由 Execute 设置）
    }

    // 卡住状态机：Normal → Stuck（0.3s 无有效前进）→ Backing（倒车 0.5s）→ Normal。
    // 倒车是独立状态：期间不计 stuck（否则倒车让前进 progress 恒为负，stuck 永不解除 → 一直退）
    void UpdateStuckState()
    {
        if (backingTimer > 0f)
        {
            backingTimer -= Time.fixedDeltaTime;
            moveInput.x = 0f;
            moveInput.y = -0.5f; // 倒车（半速后退）
            return;
        }

        CheckStuck();

        if (isStuck)
        {
            backingTimer = 0.5f; // 顶墙：进入倒车
            stuckTimer = 0f;     // 清零：倒车结束后从零重新检测——
            // 否则倒车期间 stuckTimer 保持 0.3+，结束瞬间立即重新 stuck → 无限倒车
            moveInput.x = 0f;
            moveInput.y = -0.5f;
        }
    }

    // 卡住检测：有前进输入但"有效前进"不足 → 顶墙。
    // 有效前进 = 沿坦克当前朝向的净位移（点积投影）——顶墙时 box 角滑动/旋转的
    // 横向位移不算有效前进（位置在动但没朝目标走），位置不动式检测会被它骗过
    void CheckStuck()
    {
        if (moveInput.y > 0.1f)
        {
            Vector3 delta = transform.position - lastPos;
            float progress = Vector3.Dot(delta, transform.forward);
            if (progress < 0.02f)
            {
                stuckTimer += Time.fixedDeltaTime;
            }
            else
            {
                stuckTimer = 0f;
            }
        }
        else
        {
            stuckTimer = 0f;
        }
        lastPos = transform.position;
        isStuck = stuckTimer > 0.3f;
    }

    void Decide()
    {
        FindTarget();

        // 保命优先：任何威胁存在都打断攻击/追击。
        // 子弹不分敌我（有自伤），所有子弹都是潜在威胁
        if (DetectThreat())
        {
            print("Threat!");
            if (threatIsHigh)
            {
                action = AIAction.DodgeMove;
            }
            else
            {
                action = AIAction.DodgeRotate;
                // 安全角在决策层算一次：它依赖当前朝向，执行层每帧重算会让目标角
                // 随自身转动漂移（追着自己的影子转 → 抖动/不停）
                threatSafeAngle = FindSafeAngle();
            }
            fireAngle = null;
            path = null;
            return;
        }

        if (target == null)
        {
            action = AIAction.Shoot;
            fireAngle = null;
            path = null;
            return;
        }

        float pathLength;
        float? angle = SolveFireAngle(target, out pathLength);

        // 打得到且不远 → 站桩射击；否则追击（追近再打）。
        // fireRange 是"追还是打"的切换点：没有它 AI 会永远站在原地打远弹
        if (angle.HasValue && pathLength < fireRange)
        {
            action = AIAction.Shoot;
            // 滞回：新角度与当前角度差小于 2×angleStep 时不更新。
            // 墙角反弹时命中角是一段连续范围，弹道最短角随位置微变跳变，
            // 直接更新会让转向追着跳变角过冲震荡（抖动不射击）
            if (!fireAngle.HasValue ||
                Mathf.Abs(Mathf.DeltaAngle(fireAngle.Value, angle.Value)) > angleStep * 2f)
            {
                fireAngle = angle;
            }
        }
        else
        {
            action = AIAction.Chase;
            fireAngle = null;
            PlanChasePath();
        }
    }

    void FindTarget()
    {
        // 简化：所有 tag=Tank 的物体里找最近的（排除自己）。
        // 场景里只有一个玩家时它即是目标；多 AI 互打也成立。阵营区分留待后续
        GameObject[] tanks = GameObject.FindGameObjectsWithTag("Tank");
        target = null;
        float bestDist = float.MaxValue;
        foreach (GameObject t in tanks)
        {
            if (t == gameObject)
            {
                continue;
            }
            float d = Vector3.Distance(t.transform.position, transform.position);
            if (d < bestDist)
            {
                bestDist = d;
                target = t.transform;
            }
        }
    }

    // 威胁检测：预测所有子弹（不分敌我）的弹道，是否有子弹命中自己。
    // 取"最先到达"的威胁；命中点离中心近 = 穿心（高威胁，旋转躲不开），远 = 擦边（低威胁，旋转可躲）
    bool DetectThreat()
    {
        BulletBase[] bullets = FindObjectsByType<BulletBase>();
        float bestTime = float.MaxValue;
        bool found = false;

        foreach (BulletBase b in bullets)
        {
            if (!b.gameObject.activeInHierarchy)
            {
                continue;
            }

            // 用 velocity 方向而非 forward：旋转锁定的子弹反弹后 velocity 与 forward 解耦，
            // 预测物理运动必须用 velocity
            PathResult r = BallisticPath.Simulate(
                b.transform.position, b.VelocityDirection,
                config.maxBounces, config.MaxDistance, obstacleMask,
                reuseThreat, config.bulletRadius + 0.2f);

            if (r.end == PathEnd.HitTank && r.hitCollider != null && r.hitCollider.transform == transform)
            {
                float arriveTime = r.length / config.speed;
                // 时效过滤：到达时间超过上限的子弹不值得立即反应——
                // 否则远处的子弹触发躲避，且每颗新子弹刷新威胁快照，躲避方向持续漂移（坦克卡在转向里不动）
                if (arriveTime > threatTimeLimit)
                {
                    continue;
                }
                if (arriveTime < bestTime)
                {
                    bestTime = arriveTime;
                    // 与 Simulate 同源用 velocity 方向（旋转锁定的子弹反弹后 forward 不等于运动方向）
                    threatDir = b.VelocityDirection;
                    threatDir.y = 0f;
                    threatHitPoint = r.points[r.points.Count - 1];
                    threatArriveTime = arriveTime;
                    // 轨迹线到中心的距离（2D 叉积 = XZ 平面点到直线距离），而非命中点到中心距离：
                    // 侧面正对中心的射击，命中点离中心远（半宽处）但直线穿过中心——后者会漏判穿心。
                    // 用 2D 叉积（仅 y 分量）：3D 叉积会带入命中点与中心的 y 差（子弹飞行高度 vs 坦克中心）
                    Vector3 toCenter = threatHitPoint - transform.position;
                    float lineDist = Mathf.Abs(threatDir.x * toCenter.z - threatDir.z * toCenter.x);
                    threatIsHigh = lineDist < threatHighThreshold;
                    found = true;
                }
            }
        }
        return found;
    }

    // 角度枚举求解：找到能命中目标的发射角度，返回"弹道最短"的那个。
    // 选弹道最短而非转向最小：命中时间快，目标来不及反应；
    // 转向最小会把 AI 锁死在绕远的反弹角上
    float? SolveFireAngle(Transform target, out float bestLength)
    {
        bestLength = float.MaxValue;
        // 起点用坦克根物体而非 firePoint（子物体）：根物体不随旋转漂移，
        // 枚举角度（世界基准）与坦克 yaw 同基准，消除旋转误差。
        // 注意：从根物体（碰撞体内部）出发的射线不会命中自己，自伤方向的模拟是近似
        Vector3 origin = transform.position;
        float best = -1f;

        for (float a = 0f; a < 360f; a += angleStep)
        {
            Vector3 dir = Quaternion.Euler(0f, a, 0f) * Vector3.forward;
            PathResult r = BallisticPath.Simulate(
                origin, dir, config.maxBounces, config.MaxDistance, obstacleMask,
                reuse, config.bulletRadius, selfCollider);

            if (r.end == PathEnd.HitTank && r.hitCollider != null && r.hitCollider.transform == target)
            {
                if (r.length < bestLength)
                {
                    bestLength = r.length;
                    best = a;
                }
            }
        }
        return best >= 0f ? (float?)best : null;
    }

    // 追击路径规划：寻路到目标格，起点不可走时找回网格（导弹踩过的坑直接复用）
    void PlanChasePath()
    {
        GridMap grid = GridMap.Instance;
        if (grid == null || target == null)
        {
            path = null;
            return;
        }

        Vector2Int start = grid.WorldToCell(transform.position);
        Vector2Int goal = grid.WorldToCell(target.position);

        if (!grid.IsWalkable(start, unitRadius))
        {
            Vector2Int? near = grid.ClosestWalkable(start, maxRecoverRadius, unitRadius);
            if (near == null)
            {
                path = null;
                return;
            }
            start = near.Value;
        }
        if (!grid.IsWalkable(goal, unitRadius))
        {
            Vector2Int? near = grid.ClosestWalkable(goal, maxRecoverRadius, unitRadius);
            if (near == null)
            {
                path = null;
                return;
            }
            goal = near.Value;
        }

        path = Pathfinding.FindPath(grid, start, goal, unitRadius);
        pathIndex = 1; // 跳过起点格
    }

    void Execute()
    {
        cooldownTimer -= Time.fixedDeltaTime;
        print(action);
        switch (action)
        {
            case AIAction.Shoot:
                ExecuteShoot();
                break;
            case AIAction.Chase:
                ExecuteChase();
                break;
            case AIAction.DodgeMove:
                ExecuteDodgeMove();
                break;
            case AIAction.DodgeRotate:
                ExecuteDodgeRotate();
                break;
        }
    }

    void ExecuteShoot()
    {
        moveInput.y = 0f; // 站桩射击

        if (target == null || fireAngle == null)
        {
            moveInput.x = 0f;
            return;
        }

        moveInput.x = SteerToward(fireAngle.Value);
        if (Mathf.Abs(Mathf.DeltaAngle(transform.eulerAngles.y, fireAngle.Value)) <= aimThreshold
            && cooldownTimer <= 0f)
        {
            //Fire();
            cooldownTimer = fireCooldown;
        }
    }

    // PD 转向：比例项朝目标（收敛），微分项（角速度）预测过冲提前减速（刹停）。
    // 微分项两个约束缺一不可：
    // 1. 只在与目标同向转动时生效（angleDiff × vel > 0 = 正在接近）——反向时不阻尼
    // 2. 幅度限制为比例项的一半——否则快到位时（角度差小、角速度大）阻尼压过比例项产生反向驱动，原地抖动
    float SteerToward(float targetAngle, float approachAngle = 24f)
    {
        float angleDiff = Mathf.DeltaAngle(transform.eulerAngles.y, targetAngle);
        if (Mathf.Abs(angleDiff) < aimThreshold)
        {
            return 0f; // 死区：到位即停
        }

        float steer = angleDiff / approachAngle; // 比例项

        float angularVelDeg = rb.angularVelocity.y * Mathf.Rad2Deg;
        if (angleDiff * angularVelDeg > 0f)
        {
            float velNorm = Mathf.Clamp(Mathf.Abs(angularVelDeg) / rotateSpeed, 0f, 1f);
            steer -= 0.5f * steer * velNorm; // 阻尼最多削减比例项一半，永不反转
        }

        return Mathf.Clamp(steer, -1f, 1f);
    }

    void ExecuteChase()
    {
        if (target == null || path == null || path.Count == 0)
        {
            moveInput.x = 0f;
            moveInput.y = 0f;
            return;
        }

        // 到达判定：靠近目标就停，等下次决策切换射击模式
        if (Vector3.Distance(transform.position, target.position) < chaseArriveDistance)
        {
            moveInput.x = 0f;
            moveInput.y = 0f;
            return;
        }

        // 投影推进路径点（与导弹同款判定）：高速连续运动下用"越过"而非"距离"
        while (pathIndex < path.Count)
        {
            Vector3 wp = GridMap.Instance.CellToWorld(path[pathIndex]);
            Vector3 prev = pathIndex > 0
                ? GridMap.Instance.CellToWorld(path[pathIndex - 1])
                : transform.position;

            Vector3 segDir = wp - prev;
            float segLen = segDir.magnitude;
            if (segLen < 0.001f)
            {
                pathIndex++;
                continue;
            }

            float proj = Vector3.Dot(transform.position - prev, segDir / segLen);
            if (proj >= segLen - chaseArriveDistance)
            {
                pathIndex++;
            }
            else
            {
                break;
            }
        }

        // 路径走完：停下等重算。目标格可能在墙后，直走必然顶墙
        if (pathIndex >= path.Count)
        {
            moveInput.x = 0f;
            moveInput.y = 0f;
            return;
        }

        // 前瞻转向：朝下一个路径点转，提前切弯，轨迹圆弧化。
        // 路径曲率（1 格直角）远大于坦克转弯半径（约 9.6 单位），到拐角才转必然冲出路径
        int lookAhead = Mathf.Min(pathIndex + 1, path.Count - 1);
        Vector3 moveTarget = GridMap.Instance.CellToWorld(path[lookAhead]);

        Vector3 toTarget = moveTarget - transform.position;
        toTarget.y = 0f;
        float targetAngle = Mathf.Atan2(toTarget.x, toTarget.z) * Mathf.Rad2Deg;
        float angleDiff = Mathf.DeltaAngle(transform.eulerAngles.y, targetAngle);

        // 比例转向：角度大时全速，接近目标时减速，避免在路径点附近抖动
        moveInput.x = Mathf.Clamp(angleDiff / 45f, -1f, 1f);
        // 转角减速：角度差越大前进越慢——转弯与速度协调，否则转弯半径与路径曲率不匹配
        float turnFactor = Mathf.Clamp01(1f - Mathf.Abs(angleDiff) / 90f);
        moveInput.y = turnFactor;
    }

    // 移动躲避（高威胁：穿心命中，旋转躲不开）：
    // 朝子弹轨迹的垂直方向移动，方向用网格查询选侧（体积感知），转弯时减速
    // 移动躲避：向"脱离炮弹路径路程最小且可达"的方向移动；脱离危险区即停。
    // 危险区 = 炮弹路径线 ± 半宽（坦克半对角 + 炮弹半径）——坦克中心移出即安全
    void ExecuteDodgeMove()
    {
        GridMap grid = GridMap.Instance;
        float halfDiag = Mathf.Sqrt(TankHalfExtents.x * TankHalfExtents.x
                                   + TankHalfExtents.y * TankHalfExtents.y);
        float dangerHalf = halfDiag + config.bulletRadius + 0.2f;

        // 当前坦克中心到炮弹路径线的距离（2D 点到直线）
        Vector3 toLine = transform.position - threatHitPoint;
        float lineDistNow = Mathf.Abs(threatDir.x * toLine.z - threatDir.z * toLine.x);

        // 已脱离危险区：停（下个决策周期恢复）
        if (lineDistNow >= dangerHalf)
        {
            moveInput.x = 0f;
            moveInput.y = 0f;
            return;
        }

        // 远离侧 = 路径线的横向分量方向（从路径线指向坦克，垂直于路径线）
        Vector3 toCenter = transform.position - threatHitPoint;
        Vector3 h = toCenter - threatDir * Vector3.Dot(threatDir, toCenter);
        Vector3 perp = h.sqrMagnitude > 0.0001f
            ? h.normalized
            : Quaternion.Euler(0f, 90f, 0f) * threatDir.normalized; // 恰好在线上的退化情况

        // 候选路程（沿垂直方向移动的量）：
        // 远离侧：直接移出危险区
        float needFar = Mathf.Max(0f, dangerHalf - lineDistNow);
        // 穿过侧：穿过路径线到另一侧（更远——默认被排除，仅当远离侧堵死时用）
        float needCross = lineDistNow + dangerHalf;

        Vector3 dodgeDir = perp;
        if (grid != null)
        {
            float reachFar = ReachAlong(grid, perp);
            float reachCross = ReachAlong(grid, -perp);
            if (reachFar < needFar && reachCross >= needCross)
            {
                dodgeDir = -perp; // 远离侧走不出危险区，改穿到另一侧（路程更大但可达）
            }
            else if (reachFar < needFar && reachCross < needCross)
            {
                // 两侧都走不出危险区：选可达路程长的一侧（尽量远离，stuck 检测兜底脱困）
                if (reachCross > reachFar)
                {
                    dodgeDir = -perp;
                }
            }
        }

        float targetAngle = Mathf.Atan2(dodgeDir.x, dodgeDir.z) * Mathf.Rad2Deg;
        float angleDiff = Mathf.DeltaAngle(transform.eulerAngles.y, targetAngle);
        moveInput.x = Mathf.Clamp(angleDiff / 45f, -1f, 1f);
        float turnFactor = Mathf.Clamp01(1f - Mathf.Abs(angleDiff) / 90f);
        moveInput.y = turnFactor;
    }

    // 沿 dir 探测可走距离（步进 0.5 格，上限 8 格；体积判定）
    float ReachAlong(GridMap grid, Vector3 dir)
    {
        if (grid == null)
        {
            return 8f;
        }
        for (float d = 0.5f; d <= 8f; d += 0.5f)
        {
            if (!grid.IsWalkable(grid.WorldToCell(transform.position + dir * d), unitRadius))
            {
                return d - 0.5f;
            }
        }
        return 8f;
    }

    // 旋转躲避（低威胁：擦边命中）：转决策层算好的安全角（快照，执行层不重算）。
    // 旋转不动位置，代价最低——但只对擦边有效（穿心无论怎么转都躲不开）
    void ExecuteDodgeRotate()
    {
        if (threatSafeAngle < 0f)
        {
            // 找不到安全角（理论不该发生）：退化为移动躲
            ExecuteDodgeMove();
            return;
        }

        // 时间预算：旋转躲的前提是"子弹到达前转好姿态"。
        // 转向时间 > 到达时间 → 来不及，改移动躲
        float turnTime = Mathf.Abs(Mathf.DeltaAngle(transform.eulerAngles.y, threatSafeAngle)) / rotateSpeed;
        if (turnTime > threatArriveTime)
        {
            ExecuteDodgeMove();
            return;
        }

        moveInput.x = SteerToward(threatSafeAngle);
        moveInput.y = 0f; // 旋转躲避不动位置
    }

    // 轨迹线绕坦克中心旋转 -θ，用 slab 测试判断是否仍穿过 box。
    // 从当前朝向开始扫：第一个安全角 = 转动最小的；当前朝向已安全则返回当前朝向（不转）
    float FindSafeAngle()
    {
        Vector3 center = transform.position;
        Vector3 p0 = threatHitPoint - threatDir * 4f; // 轨迹延长，覆盖 box 区域
        Vector3 p1 = threatHitPoint + threatDir * 4f;
        float startYaw = transform.eulerAngles.y;

        // 测试 box 按子弹半径膨胀：线避开 box 不代表子弹球体避开——
        // 子弹擦着 box 边缘一个半径内飞过，实际会命中（"转准了还是擦到"的根源）
        float inflate = config.bulletRadius + 0.1f;
        Vector2 extents = new Vector2(TankHalfExtents.x + inflate, TankHalfExtents.y + inflate);

        for (float offset = 0f; offset < 360f; offset += dodgeRotateStep)
        {
            float theta = startYaw + offset;
            Vector2 a = Rotate2D(p0 - center, -theta);
            Vector2 b = Rotate2D(p1 - center, -theta);
            if (!SegmentIntersectsAABB(a, b, extents))
            {
                return ((theta % 360f) + 360f) % 360f; // 归一化到 [0,360)
            }
        }
        return -1f;
    }

    // 2D 向量绕原点旋转（XZ 平面）
    static Vector2 Rotate2D(Vector3 v, float deg)
    {
        float rad = deg * Mathf.Deg2Rad;
        float c = Mathf.Cos(rad);
        float s = Mathf.Sin(rad);
        return new Vector2(v.x * c + v.z * s, -v.x * s + v.z * c);
    }

    // 2D AABB（中心在原点，半轴 half）与线段相交测试（slab method）：
    // 对每条轴，把线段参数 t 的可行区间裁剪到 [0,1]，任一轴裁剪失败即不相交
    static bool SegmentIntersectsAABB(Vector2 p0, Vector2 p1, Vector2 half)
    {
        Vector2 d = p1 - p0;
        float tMin = 0f;
        float tMax = 1f;

        if (Mathf.Abs(d.x) < 1e-6f)
        {
            if (Mathf.Abs(p0.x) > half.x)
            {
                return false;
            }
        }
        else
        {
            float t1 = (-half.x - p0.x) / d.x;
            float t2 = (half.x - p0.x) / d.x;
            tMin = Mathf.Max(tMin, Mathf.Min(t1, t2));
            tMax = Mathf.Min(tMax, Mathf.Max(t1, t2));
            if (tMin > tMax)
            {
                return false;
            }
        }

        if (Mathf.Abs(d.y) < 1e-6f)
        {
            if (Mathf.Abs(p0.y) > half.y)
            {
                return false;
            }
        }
        else
        {
            float t1 = (-half.y - p0.y) / d.y;
            float t2 = (half.y - p0.y) / d.y;
            tMin = Mathf.Max(tMin, Mathf.Min(t1, t2));
            tMax = Mathf.Min(tMax, Mathf.Max(t1, t2));
            if (tMin > tMax)
            {
                return false;
            }
        }
        return true;
    }
}
