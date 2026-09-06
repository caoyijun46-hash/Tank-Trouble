using UnityEngine;

// 追踪弹：寻路绕墙追踪目标。
// 机制：周期重算路径（目标在动）→ 沿路径点转向 → 每帧重设速度方向
public class Missile : BulletBase
{
    [SerializeField] private float turnSpeed = 360f;      // 转向速率（度/秒）
    [SerializeField] private float repathInterval = 0.3f; // 路径重算间隔（秒）
    [SerializeField] private float arriveDistance = 0.6f; // 到达路径点的判定距离
    [SerializeField] private int unitRadius = 0;          // 体积半径（格数）：导弹小，0 即可
    [SerializeField] private int maxRecoverRadius = 10;   // 找回网格的最大搜索半径（格数）

    [SerializeField]private Transform target;
    private System.Collections.Generic.List<Vector2Int> path;
    private int pathIndex;
    private float repathTimer;
    public GameObject owner;
    void Awake()
    {
        init();
        Destroy(gameObject, LifeTime);
    }

    void OnEnable()
    {
        Repath();
    }
    void Start()
    {
        GameObject[] t = GameObject.FindGameObjectsWithTag("Tank");
        foreach (GameObject tank in t)
        {
            if (tank != owner) // 不追踪自己
            {
                target = tank.transform;
                break;
            }
        }       
    }
    void FixedUpdate()
    {
        if (target == null)
        {
            move(); // 目标丢失：直飞，等寿命结束销毁
            return;
        }

        repathTimer -= Time.fixedDeltaTime;
        if (repathTimer <= 0f)
        {
            Repath();
            repathTimer = repathInterval;
        }

        FollowPath();
    }

    public void SetTarget(Transform t)
    {
        target = t;
    }

    void Repath()
    {
        GridMap grid = GridMap.Instance;
        if (grid == null || target == null)
        {
            return;
        }

        Vector2Int start = grid.WorldToCell(transform.position);
        Vector2Int goal = grid.WorldToCell(target.position);

        // 兜底：导弹可能因转向限制甩出可走区域，先找回最近的网格位置再寻路
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
        pathIndex = 1; // 跳过起点格：导弹已在起点格内，从第二格开始跟
    }

    void FollowPath()
    {
        if (path == null || path.Count == 0)
        {
            move();
            return;
        }

        // 推进路径点：用"是否越过路径点"（投影）判定，替代径向距离。
        // 径向距离在高速连续运动下失效：导弹擦过/冲过头时永远到不了"< 阈值"，会掉头打转
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
            if (proj >= segLen - arriveDistance)
            {
                pathIndex++;
            }
            else
            {
                break;
            }
        }

        if (pathIndex >= path.Count)
        {
            move(); // 路径走完，直飞；下一帧 Repath 会重新规划
            return;
        }

        Vector3 waypoint = GridMap.Instance.CellToWorld(path[pathIndex]);
        Vector3 dir = waypoint - transform.position;
        dir.y = 0f; // 只绕 Y 轴转向（俯视游戏，平面移动）

        if (dir.sqrMagnitude > 0.0001f)
        {
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation,
                Quaternion.LookRotation(dir),
                turnSpeed * Time.fixedDeltaTime);
            move(); // 转向后必须重设 velocity，刚体不会自动跟随 forward
        }
    }

    protected override void DestoryBullet()
    {
        Destroy(gameObject);
    }
}
