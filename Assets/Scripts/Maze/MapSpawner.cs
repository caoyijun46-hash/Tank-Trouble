using UnityEngine;

// ============================================================
// 执行层：把 MazeData（决策层产物）的墙段布尔数据，
// 实例化为场景中的真实墙体（Wall.prefab），并挂到统一容器下。
//
// 挂载与配置（Test 场景）：
//   1. 建一个空物体 "Map Generator"，挂本组件
//   2. Inspector 拖入 wallPrefab（Prefabs/Wall.prefab，默认 scale 10×6×1）
//   3. Awake 时自动生成迷宫 → 之后的 GridMap.Rebuild() 会扫到这些墙
//
// 旋转约定（与 Wall.prefab 对齐）：
//   Wall.prefab 默认沿 X 轴长 10（scale x=10, z=1）→ 水平墙段直接放
//   垂直墙段（沿 Z 长 10）：R_y(θ)(1,0,0)=(cosθ,0,-sinθ)，θ=-90° → +Z
//   （scale 在 prefab 上，不另设；height 6 → y 中心 3）
// ============================================================
// 执行顺序晚于默认(0)：GridMap.Awake 先注册 Instance，
// 本组件再生成墙并 SetBounds——保证每局网格范围跟迷宫一致
[DefaultExecutionOrder(10)]
public class MapSpawner : MonoBehaviour
{
    [Header("迷宫参数（每局在范围内随机）")]
    [Tooltip("X 向格数范围（默认 8~10 → 地图 80~100 宽）")]
    [SerializeField] private int colsMin = 8;
    [SerializeField] private int colsMax = 10;
    [Tooltip("Z 向格数范围（默认 3~4 → 地图 30~40 深）")]
    [SerializeField] private int rowsMin = 3;
    [SerializeField] private int rowsMax = 4;
    [Tooltip("0 = 每局随机种子（不同迷宫）")]
    [SerializeField] private int seed = 0;

    [Header("墙体资源")]
    [SerializeField] private GameObject wallPrefab;  // Prefabs/Wall.prefab

    [Header("地面资源")]
    [SerializeField] private Material floorMat;      // Materials/Ground.mat

    [Header("运行时引用（自动填充）")]
    [SerializeField] private Transform wallRoot;     // 所有墙的父容器

    // 本局使用的种子与数据（供其他系统查询，如出生点定位）
    public int UsedSeed { get; private set; }
    public MazeData Data { get; private set; }

    // 运行时生成的地板（每局重建）
    GameObject floorGo;

    void Awake()
    {
        if (wallPrefab == null)
        {
            Debug.LogError("MapSpawner: wallPrefab 未赋值", this);
            return;
        }
        // 每局在可调范围内随机行列数；Random.Range(int) 上限不含 → +1
        int mazeCols = Random.Range(colsMin, colsMax + 1);
        int mazeRows = Random.Range(rowsMin, rowsMax + 1);
        Generate(mazeCols, mazeRows, seed == 0 ? Random.Range(1, int.MaxValue) : seed);
    }

    /// <summary>决策层生成 + 执行层实例化。可重复调用（旧墙先清掉）</summary>
    public void Generate(int mazeCols, int mazeRows, int mazeSeed)
    {
        // ---- 决策层 ----
        Data = MazeData.Generate(mazeCols, mazeRows, mazeSeed);
        UsedSeed = mazeSeed;

        // ---- 执行层：清理上一张图的旧墙/旧地板 ----
        if (wallRoot == null)
            CreateRoot();
        for (int i = wallRoot.childCount - 1; i >= 0; i--)
            Destroy(wallRoot.GetChild(i).gameObject);
        if (floorGo != null)
        {
            Destroy(floorGo);
            floorGo = null;
        }

        // ---- 执行层：按墙段实例化 ----
        var m = Data;

        // 垂直墙段 vWall[i,r]：x = i*10，沿 Z 覆盖 [r*10, (r+1)*10]
        for (int i = 0; i <= m.cols; i++)
        {
            for (int r = 0; r < m.rows; r++)
            {
                if (!m.vWall[i, r]) continue;
                Vector2 pos = m.VWallCenter(i, r);
                InstantiateWall(new Vector3(pos.x, 0f, pos.y), -90f); // 竖墙：绕Y -90°（见类注释推导）
            }
        }

        // 水平墙段 hWall[c,j]：z = j*10，沿 X 覆盖 [c*10, (c+1)*10]
        for (int c = 0; c < m.cols; c++)
        {
            for (int j = 0; j <= m.rows; j++)
            {
                if (!m.hWall[c, j]) continue;
                Vector2 pos = m.HWallCenter(c, j);
                InstantiateWall(new Vector3(pos.x, 0f, pos.y), 0f);  // 横墙：不旋转
            }
        }

        // ---- 执行层：地板铺满整张地图 ----
        CreateFloor(m);

        // GridMap 跟随迷宫实际范围重建（迷宫固定以原点为左下角）
        if (GridMap.Instance != null)
            GridMap.Instance.SetBounds(Vector2.zero, new Vector2(m.Width, m.Depth));
    }

    // 地面：一张 Plane 平铺整张地图（含墙厚），随迷宫 cols/rows 自动缩放。
    // 保留 MeshCollider 与主场景地面一致；tag 非 Wall，不影响 GridMap 扫描
    void CreateFloor(MazeData m)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Plane);
        go.name = "Floor";
        go.transform.SetParent(transform, false);
        // Plane 内置网格原始尺寸 10×10 → 缩放倍数 = 格数
        go.transform.localScale = new Vector3(m.cols, 1f, m.rows);
        go.transform.localPosition = new Vector3(
            m.cols * MazeData.CellSize * 0.5f, 0f, m.rows * MazeData.CellSize * 0.5f);

        var rend = go.GetComponent<MeshRenderer>();
        if (floorMat != null)
            rend.sharedMaterial = floorMat;

        floorGo = go;
    }

    void CreateRoot()
    {
        var go = new GameObject("Walls");
        go.transform.SetParent(transform, false);
        wallRoot = go.transform;
    }

    // prefab 本体：scale (10,6,1)，墙高 6 → y 中心 3
    void InstantiateWall(Vector3 xzPos, float rotY)
    {
        var wall = Instantiate(wallPrefab, wallRoot);
        float centerY = wallPrefab.transform.localScale.y * 0.5f;
        wall.transform.localPosition = new Vector3(xzPos.x, centerY, xzPos.z);
        wall.transform.localRotation = Quaternion.Euler(0f, rotY, 0f);
        wall.name = "Wall";
    }
}
