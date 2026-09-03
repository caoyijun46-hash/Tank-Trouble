using UnityEngine;

// 地图网格化：把迷宫几何变成寻路可用的格子数据
// 构建方式：逐格用物理查询检测 tag=Wall 的碰撞体。
// 网格只存原始数据（墙=不可走），单位体积的膨胀由寻路时的 unitRadius 参数动态判定
public class GridMap : MonoBehaviour
{
    [SerializeField] private Vector2 minBounds;   // 地图左下角（XZ 平面）
    [SerializeField] private Vector2 maxBounds;   // 地图右上角
    [SerializeField] private float cellSize = 1f;
    [SerializeField] private bool showGizmos = true;

    private bool[] walkable; // true = 可走；一维数组，索引 = y * width + x
    private int width;
    private int height;

    public static GridMap Instance { get; private set; }
    public int Width => width;
    public int Height => height;

    // 当前网格覆盖的世界范围（XZ），供外部按实际地图取景/定位
    public Vector2 MinBounds => minBounds;
    public Vector2 MaxBounds => maxBounds;

    void Awake()
    {
        Instance = this;
        Build();
    }

    // 运行时地图变化（如迷宫生成）后调用：重建可走网格
    public void Rebuild()
    {
        Build();
    }

    // 迷宫每局尺寸随机：先按实际范围更新扫描矩形，再重建网格。
    // min 为地图左下角（迷宫坐标系以原点为起点）
    public void SetBounds(Vector2 min, Vector2 max)
    {
        minBounds = min;
        maxBounds = max;
        Build();
    }

    void Build()
    {
        width = Mathf.CeilToInt((maxBounds.x - minBounds.x) / cellSize);
        height = Mathf.CeilToInt((maxBounds.y - minBounds.y) / cellSize);
        walkable = new bool[width * height];

        Vector3 halfExtents = new Vector3(cellSize * 0.5f, 50f, cellSize * 0.5f);
        Collider[] hits = new Collider[16];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                Vector3 center = CellCenterWorld(x, y);
                bool blocked = false;
                int count = Physics.OverlapBoxNonAlloc(center, halfExtents, hits);
                for (int i = 0; i < count; i++)
                {
                    if (hits[i].CompareTag("Wall"))
                    {
                        blocked = true;
                        break;
                    }
                }
                walkable[y * width + x] = !blocked;
            }
        }
    }

    public Vector2Int WorldToCell(Vector3 world)
    {
        int x = Mathf.FloorToInt((world.x - minBounds.x) / cellSize);
        int y = Mathf.FloorToInt((world.z - minBounds.y) / cellSize);
        return new Vector2Int(x, y);
    }

    public Vector3 CellToWorld(Vector2Int cell)
    {
        return new Vector3(
            minBounds.x + (cell.x + 0.5f) * cellSize,
            0f,
            minBounds.y + (cell.y + 0.5f) * cellSize);
    }

    public bool IsWalkable(int x, int y)
    {
        if (x < 0 || x >= width || y < 0 || y >= height)
        {
            return false; // 边界外不可走
        }
        return walkable[y * width + x];
    }

    public bool IsWalkable(Vector2Int cell)
    {
        return IsWalkable(cell.x, cell.y);
    }

    // 单位半径判定：以 cell 为中心、radius 内的格子全部可走才返回 true。
    // 这是"单位体积"的动态膨胀——radius=0 退化为单格判定
    public bool IsWalkable(Vector2Int cell, int radius)
    {
        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (!IsWalkable(cell.x + dx, cell.y + dy))
                {
                    return false;
                }
            }
        }
        return true;
    }

    // 从 cell 开始按距离逐层向外扩展（正方形环），找第一个对 unitRadius 可走的格子。
    // 层序扩展保证找到的是最近的；超过 maxRadius 返回 null
    public Vector2Int? ClosestWalkable(Vector2Int cell, int maxRadius, int unitRadius = 0)
    {
        for (int r = 0; r <= maxRadius; r++)
        {
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    // 只检查当前层的环（max(|dx|,|dy|) == r），内层已查过
                    if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)) != r)
                    {
                        continue;
                    }
                    Vector2Int c = new Vector2Int(cell.x + dx, cell.y + dy);
                    if (IsWalkable(c, unitRadius))
                    {
                        return c;
                    }
                }
            }
        }
        return null;
    }

    Vector3 CellCenterWorld(int x, int y)
    {
        return new Vector3(
            minBounds.x + (x + 0.5f) * cellSize,
            0f,
            minBounds.y + (y + 0.5f) * cellSize);
    }

    void OnDrawGizmos()
    {
        if (!showGizmos || walkable == null)
        {
            return;
        }
        Gizmos.color = new Color(1f, 0f, 0f, 0.5f);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (!walkable[y * width + x])
                {
                    Gizmos.DrawCube(CellCenterWorld(x, y), Vector3.one * cellSize * 0.9f);
                }
            }
        }
    }
}
