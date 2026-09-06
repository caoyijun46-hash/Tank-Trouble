using System.Collections.Generic;
using UnityEngine;

// 坦克 AI：决策层（节流）在"躲避 > 射击 > 追击"间切换，执行层（每帧）按模式驱动 moveInput。
// 与玩家 Tank 对称：玩家注入 InputAction，AI 注入决策结果（moveInput + Fire）
public class TankAI : TankBase
{
    private enum AIAction { Shoot, Chase, GetItem, DodgeMove, DodgeRotate }

    // 行为算法参数单一来源（Assets/Config/AiConfig.asset）。
    // 想调 AI 难度：复制资产改数值，Enemy.prefab 换拖一份，代码零改动
    [SerializeField] private AiConfig aiConfig;
    [SerializeField] private BallisticConfig config;        // 与子弹同源：模拟参数和实际一致

    private Transform target;      // 战斗目标（最近的敌方坦克，射击瞄准用）
    private Transform chaseTarget; // 移动跟随目标（Chase=target；GetItem=道具）
    private Item pickupItem;       // 当前要去捡的道具（空手决策产出）
    private AIAction action = AIAction.Shoot;
    private float? fireAngle;      // 射击模式的瞄准角；null = 没有可命中角度
    private List<Vector2Int> path; // 追击模式的寻路路径（A* 后已压缩成关键点）
    private int pathIndex;
    private Vector2Int? plannedGoalCell; // path 对应的目标格；目标换格才重寻
    private float decideTimer;
    private float cooldownTimer;

    private bool threatIsHigh;        // 当前威胁等级（决策结果）
    private Vector3 threatDir;        // 威胁子弹轨迹方向（水平）
    private Vector3 threatHitPoint;   // 威胁子弹命中点
    private float threatArriveTime;   // 威胁子弹到达时间（时间预算用）
    private float threatSafeAngle;    // 旋转躲避的安全角（决策层算一次，执行层只消费）

    // 卡墙检测：有前进输入但"有效前进"不足 → 顶墙
    private Vector3 lastPos;
    private float stuckTimer;
    private bool isStuck;
    // 脱困（Backing）状态：倒车 + 朝 escapeYaw 转向，直到前方重新让出净空。
    // backingLeft > 0 = 脱困中；escapeYaw 进入脱困时算一次（执行层只消费）
    private float backingLeft;
    private float escapeYaw = -1f;

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
        if (aiConfig == null)
        {
            Debug.LogError($"{name}: 缺 AiConfig 引用（Enemy prefab 组件上拖 Assets/Config/AiConfig.asset）", this);
            enabled = false;
            return;
        }
    }

    // 决策层：节流执行，输出"意图"（模式 + 参数）
    void Update()
    {
        decideTimer -= Time.deltaTime;
        if (decideTimer <= 0f)
        {
            Decide();
            decideTimer = aiConfig.decideInterval;
        }
    }

    // 执行层：每帧把意图变成输入
    void FixedUpdate()
    {
        Execute();
        UpdateStuckState();
        Move(); // TankBase 物理移动（moveInput 由 Execute 设置）
    }

    // 卡墙状态机：Normal → Stuck（0.3s 无有效前进）→ Backing → Normal。
    // 旧版只在 0.5s 内直线倒车：倒完车头仍顶着原墙，随即再次顶上去，
    // 形成"撞墙 → 倒车 → 再撞墙"死循环。新版两点改动：
    //   1. 倒车同时朝脱困角 escapeYaw 旋转（进入时扫掠算一次）——让车头
    //      离开墙面，而不是倒完继续怼原墙；
    //   2. 退出条件 = 前方净空恢复（aiConfig.backClearDist）且已倒够 aiConfig.backMinTime；
    //      倒不干净就继续倒，aiConfig.backMaxTime 兜底防极端卡死。
    // Backing 期间不计 stuck（否则倒车让前进 progress 恒为负，永远退不出去）
    void UpdateStuckState()
    {
        if (backingLeft > 0f)
        {
            backingLeft -= Time.fixedDeltaTime;
            EscapeDrive();
            return;
        }

        CheckStuck();
        if (isStuck)
        {
            escapeYaw = ComputeEscapeYaw(); // 决策一次，执行层只消费
            backingLeft = aiConfig.backMaxTime;
            stuckTimer = 0f; // 清零：倒车结束后从零重新检测
            EscapeDrive();   // 进入状态的当帧立即接管 moveInput
        }
    }

    // 脱困驾驶（Backing 每帧）：倒车 + PD 转向脱困角；前方让开即结束
    void EscapeDrive()
    {
        moveInput.x = escapeYaw >= 0f ? SteerToward(escapeYaw) : 0f;
        moveInput.y = -0.5f; // 半速倒车

        // 前方净空 >= aiConfig.backClearDist = 不再顶墙；且已倒够最短时长 → 恢复自由决策
        bool frontClear = FrontReach() >= aiConfig.backClearDist;
        if ((frontClear && backingLeft <= aiConfig.backMaxTime - aiConfig.backMinTime) || backingLeft <= 0f)
        {
            backingLeft = 0f;
            escapeYaw = -1f;
        }
    }

    // 前方净空探测（沿车头方向，GridMap 体积口径与寻路一致）
    float FrontReach()
    {
        GridMap grid = GridMap.Instance;
        if (grid == null)
        {
            return aiConfig.backClearDist; // 无网格场景不判"顶墙"
        }
        return ReachAlong(grid, transform.forward);
    }

    // 脱困角：从当前朝向向两侧交替扫掠（20° 步进，上限 160°），
    // 取第一个前方净空达标的航向；全堵死则兜底原路返回（+180°，来路必然可走）
    float ComputeEscapeYaw()
    {
        GridMap grid = GridMap.Instance;
        float current = transform.eulerAngles.y;
        if (grid == null)
        {
            return current + 180f;
        }
        for (float offset = 20f; offset <= 160f; offset += 20f)
        {
            for (int sign = 1; sign >= -1; sign -= 2)
            {
                float yaw = current + sign * offset;
                Vector3 dir = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                if (ReachAlong(grid, dir) >= aiConfig.backClearDist)
                {
                    return yaw;
                }
            }
        }
        return current + 180f;
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

        // —— 拾取道具（优先级高于战斗）：空手时场上只要还有道具就去拿，
        // 放弃正在进行的射击/追击（决策每 0.1s 重评估，吃完立即回战斗）。
        // 有武器时不捡：status 单槽，捡新的会顶掉现有的 Laser/Missile
        if (TrySwitchToPickup())
        {
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
        // aiConfig.fireRange 是"追还是打"的切换点：没有它 AI 会永远站在原地打远弹
        if (angle.HasValue && pathLength < aiConfig.fireRange)
        {
            action = AIAction.Shoot;
            // 滞回：新角度与当前角度差小于 2×aiConfig.angleStep 时不更新。
            // 墙角反弹时命中角是一段连续范围，弹道最短角随位置微变跳变，
            // 直接更新会让转向追着跳变角过冲震荡（抖动不射击）
            if (!fireAngle.HasValue ||
                Mathf.Abs(Mathf.DeltaAngle(fireAngle.Value, angle.Value)) > aiConfig.angleStep * 2f)
            {
                fireAngle = angle;
            }
        }
        else
        {
            action = AIAction.Chase;
            fireAngle = null;
            chaseTarget = target;
            EnsureChasePath();
        }
    }

    // 空手且场上有道具 → 切拾取模式并（按需）规划路径；否则不动动作返回 false。
    // GetItem 的路径维护与 Chase 共用 EnsureChasePath：执行层只认 chaseTarget
    bool TrySwitchToPickup()
    {
        if (CurrentPower != Power.Normal || !FindPickup())
        {
            pickupItem = null;
            return false;
        }
        action = AIAction.GetItem;
        fireAngle = null;
        chaseTarget = pickupItem.transform;
        EnsureChasePath();
        return true;
    }

    // 移动目标路径维护（Chase/GetItem 共用）：目标没换格就不重寻——
    // 每决策周期重寻会把 pathIndex 重置，执行层刚推进的进度全丢，
    // 坦克在转弯点反复原地踏步
    void EnsureChasePath()
    {
        GridMap grid = GridMap.Instance;
        Vector2Int goal = grid != null ? grid.WorldToCell(chaseTarget.position) : default;
        if (path == null || path.Count == 0 || grid == null || goal != plannedGoalCell)
        {
            PlanChasePath();
        }
    }

    // 场上有道具时挑最近的（空手才调用；Item 被吃掉 Destroy 后 Unity
    // 空引用判定自动失效，本方法每决策周期重跑）
    bool FindPickup()
    {
        Item best = null;
        float bestSqr = float.MaxValue;
        foreach (Item item in FindObjectsByType<Item>())
        {
            float d = (item.transform.position - transform.position).sqrMagnitude;
            if (d < bestSqr)
            {
                bestSqr = d;
                best = item;
            }
        }
        pickupItem = best;
        return best != null;
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
                config.maxBounces, config.MaxDistance, aiConfig.obstacleMask,
                reuseThreat, config.bulletRadius + 0.2f);

            if (r.end == PathEnd.HitTank && r.hitCollider != null && r.hitCollider.transform == transform)
            {
                float arriveTime = r.length / config.speed;
                // 时效过滤：到达时间超过上限的子弹不值得立即反应——
                // 否则远处的子弹触发躲避，且每颗新子弹刷新威胁快照，躲避方向持续漂移（坦克卡在转向里不动）
                if (arriveTime > aiConfig.threatTimeLimit)
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
                    threatIsHigh = lineDist < aiConfig.threatHighThreshold;
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

        for (float a = 0f; a < 360f; a += aiConfig.angleStep)
        {
            Vector3 dir = Quaternion.Euler(0f, a, 0f) * Vector3.forward;
            PathResult r = BallisticPath.Simulate(
                origin, dir, config.maxBounces, config.MaxDistance, aiConfig.obstacleMask,
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

    // 移动路径规划（Chase/GetItem 共用）：寻路到 chaseTarget 所在格，
    // 起点/终点不可走时找回网格（导弹踩过的坑直接复用）
    void PlanChasePath()
    {
        GridMap grid = GridMap.Instance;
        if (grid == null || chaseTarget == null)
        {
            path = null;
            return;
        }

        Vector2Int start = grid.WorldToCell(transform.position);
        Vector2Int goal = grid.WorldToCell(chaseTarget.position);

        if (!grid.IsWalkable(start, aiConfig.unitRadius))
        {
            Vector2Int? near = grid.ClosestWalkable(start, aiConfig.maxRecoverRadius, aiConfig.unitRadius);
            if (near == null)
            {
                path = null;
                return;
            }
            start = near.Value;
        }
        if (!grid.IsWalkable(goal, aiConfig.unitRadius))
        {
            Vector2Int? near = grid.ClosestWalkable(goal, aiConfig.maxRecoverRadius, aiConfig.unitRadius);
            if (near == null)
            {
                path = null;
                return;
            }
            goal = near.Value;
        }

        // A* 输出是逐格密集序列（cellSize=1，相邻格距 1 单位 ≪ 坦克尺寸），
        // 先压缩成"直线可达"的关键点，坦克才能按动力学跟随
        List<Vector2Int> dense = Pathfinding.FindPath(grid, start, goal, aiConfig.unitRadius);
        path = dense == null ? null : SimplifyPath(dense);
        pathIndex = path == null || path.Count == 0 ? 0 : 1; // 跳过起点格
        plannedGoalCell = path == null ? null : goal;
    }

    // 路径压缩（视线剪枝）：从锚点贪心拉直——锚点到候选格整条直线都能
    // 容纳坦克，就跨过中间所有密集格点；直道被压成两点，只在需要拐弯处
    // 保留转折点。逐格路径直接跟随的后果：转向目标每 1 单位跳变一次，
    // 车长 4 的坦克追不上，表现为乱转向 + 顶墙
    List<Vector2Int> SimplifyPath(List<Vector2Int> dense)
    {
        if (dense == null || dense.Count <= 2)
        {
            return dense;
        }
        var key = new List<Vector2Int> { dense[0] };
        int anchor = 0;
        for (int i = 1; i < dense.Count; i++)
        {
            if (!LineClear(dense[anchor], dense[i]))
            {
                key.Add(dense[i - 1]); // 锚点能到 i-1、到不了 i → 转折点落在 i-1
                anchor = i - 1;
            }
        }
        if (key[key.Count - 1] != dense[dense.Count - 1])
        {
            key.Add(dense[dense.Count - 1]);
        }
        return key;
    }

    // 两点连线全程可直行（坦克体积口径）：沿线段每 0.75 格采一个点，
    // 用 IsWalkable(cell, aiConfig.unitRadius) 保证车体能沿这条直线开过去不蹭墙
    bool LineClear(Vector2Int a, Vector2Int b)
    {
        GridMap grid = GridMap.Instance;
        if (grid == null)
        {
            return false;
        }
        float dx = b.x - a.x;
        float dy = b.y - a.y;
        float len = Mathf.Sqrt(dx * dx + dy * dy);
        int steps = Mathf.Max(1, Mathf.CeilToInt(len / 0.75f));
        for (int s = 0; s <= steps; s++)
        {
            float t = s / (float)steps;
            var cell = new Vector2Int(
                a.x + Mathf.RoundToInt(dx * t),
                a.y + Mathf.RoundToInt(dy * t));
            if (!grid.IsWalkable(cell, aiConfig.unitRadius))
            {
                return false;
            }
        }
        return true;
    }

    void Execute()
    {
        cooldownTimer -= Time.fixedDeltaTime;
        switch (action)
        {
            case AIAction.Shoot:
                ExecuteShoot();
                break;
            case AIAction.Chase:
            case AIAction.GetItem:
                ExecuteChase(); // 战斗追击与拾取共用的移动执行（停判定内部按 action 区分）
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
        if (Mathf.Abs(Mathf.DeltaAngle(transform.eulerAngles.y, fireAngle.Value)) <= aiConfig.aimThreshold
            && cooldownTimer <= 0f)
        {
            //Fire();
            cooldownTimer = aiConfig.fireCooldown;
        }
    }

    // PD 转向：比例项朝目标（收敛），微分项（角速度）预测过冲提前减速（刹停）。
    // 微分项两个约束缺一不可：
    // 1. 只在与目标同向转动时生效（angleDiff × vel > 0 = 正在接近）——反向时不阻尼
    // 2. 幅度限制为比例项的一半——否则快到位时（角度差小、角速度大）阻尼压过比例项产生反向驱动，原地抖动
    float SteerToward(float targetAngle, float approachAngle = 24f)
    {
        float angleDiff = Mathf.DeltaAngle(transform.eulerAngles.y, targetAngle);
        if (Mathf.Abs(angleDiff) < aiConfig.aimThreshold)
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

    // 移动执行（Chase/GetItem 共用）：跟随"压缩后"的关键点序列。
    // 与旧版差异：
    //   1. 推进判定 = 距当前关键点 < aiConfig.chaseArriveDistance 就换下一个。旧版对
    //      cellSize=1 的逐格路径做"投影越过"判定时 segLen(1) < arrive(4)，
    //      条件恒成立 → pathIndex 每帧瞬间冲到底，追击模式实际从未移动；
    //   2. 转向目标 = 当前关键点本身。旧版朝"下下个点"前瞻，目标点斜穿墙角，
    //      直线冲过去必顶墙（这就是注释卡墙检测后看到乱撞的来源）
    void ExecuteChase()
    {
        if (chaseTarget == null || path == null || path.Count == 0)
        {
            moveInput.x = 0f;
            moveInput.y = 0f;
            return;
        }

        // 到达判定：Chase 靠近战斗目标就停，等下次决策切射击；
        // GetItem 不停——道具在格中心，必须走完 path（压到 Trigger 才算拾取），
        // 提前 4 米停会停在车头够不着道具碰撞体的位置
        if (action != AIAction.GetItem
            && Vector3.Distance(transform.position, chaseTarget.position) < aiConfig.chaseArriveDistance)
        {
            moveInput.x = 0f;
            moveInput.y = 0f;
            return;
        }

        // 距离推进：到关键点 < aiConfig.chaseArriveDistance 就切下一个（关键点间距大，
        // 这个半径不会像逐格路径那样误跳）
        while (pathIndex < path.Count)
        {
            Vector3 wp = GridMap.Instance.CellToWorld(path[pathIndex]);
            float dx = transform.position.x - wp.x;
            float dz = transform.position.z - wp.z;
            if (dx * dx + dz * dz < aiConfig.chaseArriveDistance * aiConfig.chaseArriveDistance)
            {
                pathIndex++;
            }
            else
            {
                break;
            }
        }

        // 路径走完：清掉等下个决策周期重寻（目标若还在射程外会立即重规划）
        if (pathIndex >= path.Count)
        {
            moveInput.x = 0f;
            moveInput.y = 0f;
            path = null;
            return;
        }

        // 朝当前关键点转（不带"下下点"前瞻——那是斜穿墙角的根源）
        Vector3 moveTarget = GridMap.Instance.CellToWorld(path[pathIndex]);
        Vector3 toTarget = moveTarget - transform.position;
        toTarget.y = 0f;
        float targetAngle = Mathf.Atan2(toTarget.x, toTarget.z) * Mathf.Rad2Deg;
        float angleDiff = Mathf.DeltaAngle(transform.eulerAngles.y, targetAngle);

        // 比例转向 + 转角减速：角度大时慢行，转弯与速度协调
        moveInput.x = Mathf.Clamp(angleDiff / 45f, -1f, 1f);
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
            if (!grid.IsWalkable(grid.WorldToCell(transform.position + dir * d), aiConfig.unitRadius))
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

        for (float offset = 0f; offset < 360f; offset += aiConfig.dodgeRotateStep)
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
