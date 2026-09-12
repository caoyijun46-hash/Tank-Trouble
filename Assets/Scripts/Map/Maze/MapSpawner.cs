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
    [Header("迷宫参数（MazeConfig 资产，Assets/Config/）")]
    [Tooltip("格数范围/种子/补开洞比例都在 MazeConfig.asset 上，这里不设第二套字段")]
    [SerializeField] private MazeConfig mazeConfig;

    [Header("墙体资源")]
    [SerializeField] private GameObject wallPrefab;  // Prefabs/Wall.prefab

    [Header("地面资源")]
    [SerializeField] private Material floorMat;      // Materials/Ground.mat

    [Header("运行时引用（自动填充）")]
    [SerializeField] private Transform wallRoot;     // 所有墙的父容器

    // 本局使用的种子与数据（供其他系统查询，如出生点定位）
    public int UsedSeed { get; private set; }
    public MazeData Data { get; private set; }

    // 一张迷宫生成完成时广播其决策层参数（静态观察者，仿 TankBase.AnyDied）：
    //   Net 域订阅它做迷宫同构广播；单机场景无人订阅，零影响。
    //   参数顺序与 Generate 签名一致（cols, rows, seed, loopMin, loopMax）
    public static event System.Action<int, int, int, float, float> MapGenerated;

    // 运行时生成的地板（每局重建）
    GameObject floorGo;

    void Awake()
    {
        if (wallPrefab == null)
        {
            return;
        }
        RegenerateRandom(); // 首次生成走同一入口（无旧墙 → 网格同帧重建）
    }

    /// <summary>按 MazeConfig 资产参数开新一局：行列数/种子每局随机。可重复调用（每局主循环用）</summary>
    public void RegenerateRandom()
    {
        if (mazeConfig == null)
        {
            return;
        }
        // 每局在可调范围内随机行列数；Random.Range(int) 上限不含 → +1
        int mazeCols = Random.Range(mazeConfig.colsMin, mazeConfig.colsMax + 1);
        int mazeRows = Random.Range(mazeConfig.rowsMin, mazeConfig.rowsMax + 1);
        int mazeSeed = mazeConfig.seed == 0 ? Random.Range(1, int.MaxValue) : mazeConfig.seed;
        Generate(mazeCols, mazeRows, mazeSeed, mazeConfig.loopMin, mazeConfig.loopMax);
    }

    /// <summary>决策层生成 + 执行层实例化。可重复调用（旧墙先清掉）</summary>
    public void Generate(int mazeCols, int mazeRows, int mazeSeed, float loopMin = 0f, float loopMax = 0f)
    {
        // ---- 决策层 ----
        Data = MazeData.Generate(mazeCols, mazeRows, mazeSeed, loopMin, loopMax);
        UsedSeed = mazeSeed;

        // ---- 执行层：清理上一张图的旧墙/旧地板 ----
        // 记下是否销毁了旧墙：Destroy 在帧末才生效，若当帧就重建网格，
        // 扫描会混入尚未移除的旧墙碰撞体（旧墙吞掉可走格）
        bool hadOldWalls = wallRoot != null && wallRoot.childCount > 0;
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

        if (hadOldWalls)
        {
            // 重开路径：等一帧让 Destroy 移除旧墙碰撞体后再重建网格
            StartCoroutine(RebuildGridNextFrame(m.Width, m.Depth));
        }
        else if (GridMap.Instance != null)
        {
            // 首局路径：无旧墙，可同帧重建。
            // 物理同步：墙是同一帧 Instantiate 出来的新静态碰撞体，不强制同步的话，
            // 紧随其后的 GridMap.Build 里的 OverlapBox 扫不到它们 → 网格以为全图
            // 可走 → 导弹/AI 寻路穿墙直飞（手摆墙时代无此问题：场景序列化碰撞体
            // 在场景加载时已注册，Awake 扫描天然可见）
            Physics.SyncTransforms();
            GridMap.Instance.SetBounds(Vector2.zero, new Vector2(m.Width, m.Depth));
        }

        // 生成完成通知（决策层参数透传，订阅方如 Net 域拿它做迷宫同构广播）。
        // 放末尾：调用方此刻可安全读取 Data/UsedSeed，且旧墙重建路径也已走完
        MapGenerated?.Invoke(m.cols, m.rows, mazeSeed, loopMin, loopMax);
    }

    // 重开路径专用：Destroy 帧末才真正移除碰撞体，当帧扫描会把旧墙当新墙。
    // 等一帧再 SetBounds → 网格扫到的只有新墙（SyncTransforms 保证新墙可见）
    System.Collections.IEnumerator RebuildGridNextFrame(float width, float depth)
    {
        yield return null;
        Physics.SyncTransforms();
        if (GridMap.Instance != null)
        {
            GridMap.Instance.SetBounds(Vector2.zero, new Vector2(width, depth));
        }
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
