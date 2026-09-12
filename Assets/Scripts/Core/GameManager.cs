using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// ============================================================
// 游戏主循环（阵营制轮次控制器）：
//   第 N 局开始：MapSpawner 重新随机迷宫 + 按 roaster 生成各阵营坦克
//   对局中：    每 roundConfig.itemInterval 秒在随机大格中心刷一个随机 Power 的 Item
//   结束判定：  通用规则——统计仍存活的阵营数；≤1 时结束（1 = 该阵营胜，
//               0 = 同归于尽平局）。当前参与者：玩家 = 阵营 0，AI = 阵营 1；
//               未来双人（同队或互打）/三人混战只需改生成时的 team 号。
//   结算演出：  判定结束 → timeScale 降到 roundConfig.slowMotion（默认 0.12）慢放 + 相机
//               特写击杀现场——子弹缓飞、爆炸烟在慢镜里展开。刻意不用
//               timeScale=0：粒子系统吃 timeScale，归零会把爆炸冻在半空。
//   自动重开：  展示结束 → 清场 → 新随机迷宫 → 生成新参与者 → 相机回全图 →
//               恢复 timeScale=1。
//
// timeScale 纪律（慢放/暂停菜单共用）：
//   - 轮次计时一律用 Time.unscaledDeltaTime / yield return null（按帧推进）
//   - 物理查询/Destroy/InputSystem 在任何 timeScale 下照常工作，重建流程无需特殊处理
//   - 恢复 timeScale=1 放在生成完成之后
//
// 清场纪律（Destroy 帧末生效）：
//   - 旧参与者/子弹/道具 Destroy 后必须等一帧再生成，否则旧碰撞体与新生坦克重叠
//   - 池化子弹/飞行导弹都算 BulletBase，一并清除，防止上一局流弹命中新生坦克
//   - 迷宫由 MapSpawner.RegenerateRandom() 重建：内部检测到旧墙时自动把
//     GridMap 扫描推迟到销毁生效之后（见 MapSpawner.RebuildGridNextFrame）
// ============================================================
public class GameManager : MonoBehaviour
{
    private enum RoundState { Playing, RoundEnded, Restarting }

    // 阵营号只是"分组键"，不假设里面是人还是 AI：
    //   槽位 0 放玩家 1 号 prefab；槽位 1 放对手——塞 Enemy.prefab = 1vAI，
    //   塞第二套人控 Tank prefab = 本地双人（双人时两个 prefab 的输入绑定
    //   必须不同，否则一套键位同时驱动两台坦克）
    public const int TeamOne = 0;
    public const int TeamTwo = 1;

    // 比分是跨局的（无限局只记分，不做"先到 N 胜"的比赛收尾），
    // 阵营号作键：将来第三人混战新增阵营不用改存储结构
    private readonly Dictionary<int, int> scores = new();
    public static GameManager Instance { get; private set; }
    public event System.Action ScoresUpdated;

    [Header("参与者生成（阵营槽位）")]
    [SerializeField] private GameObject tankPrefab;      // Tank.prefab（玩家 1 号）
    [SerializeField] private GameObject aiPrefab;        // 1vAI 模式的对手（Enemy.prefab）
    [SerializeField] private GameObject player2Prefab;   // 双人模式的对手（第二套人控 Tank）

    [Header("玩法数值（RoundConfig 资产，Assets/Config/）")]
    [SerializeField] private RoundConfig roundConfig;    // 道具周期/结算演出/AI 数量

    [Header("道具生成")]
    [SerializeField] private GameObject itemPrefab;      // Item.prefab

    [Header("迷宫数据源（留空自动查找场景里的 Map Spawner）")]
    [SerializeField] private MapSpawner mapSpawner;

    [Header("结算特写相机（留空自动查找场景相机）")]
    [SerializeField] private FixedMapCamera roundCamera;

    private MazeData maze;
    private readonly HashSet<Vector2Int> tankCells = new(); // 本局坦克已占大格（防重合）
    private readonly List<GameObject> itemGos = new();      // 存活道具（与 itemCells 对齐）
    private readonly List<Vector2Int> itemCells = new();
    private float itemTimer;

    // 参与者登记表：任何判活/清场都只认这张表，不做"玩家=A"的特殊假设
    private readonly List<(GameObject go, int team)> roster = new();

    private RoundState state = RoundState.Playing;
    private float endTimer;    // 结算展示剩余时间（unscaled 真实秒）
    private int roundNumber;
    private int winnerTeam;    // 结算阵营号；-1 = 平局
    private Vector3? lastDeathPos; // 最近一次阵亡点（TankBase.AnyDied 上报；平局特写机位）

    void Awake()
    {
        Instance = this;
        TankBase.AnyDied += OnTankDied;
    }

    void OnDestroy()
    {
        TankBase.AnyDied -= OnTankDied;
        if (Instance == this)
        {
            Instance = null;
        }
    }

    void OnTankDied(Vector3 point) => lastDeathPos = point;

    void Start()
    {
        int bulletLayer = LayerMask.NameToLayer("Bullet");
        Physics.IgnoreLayerCollision(bulletLayer, bulletLayer, true);

        if (mapSpawner == null)
        {
            mapSpawner = FindAnyObjectByType<MapSpawner>();
        }
        if (mapSpawner == null || mapSpawner.Data == null)
        {
            enabled = false;
            return;
        }
        if (roundConfig == null)
        {
            enabled = false;
            return;
        }
        if (roundCamera == null)
        {
            roundCamera = FindAnyObjectByType<FixedMapCamera>();
        }
        Time.timeScale = 1f;
        maze = mapSpawner.Data;
        roundNumber = 1;
        SpawnRound();
        state = RoundState.Playing;
        // 联机：开局广播当前比分（0:0），client 对账用。判空：单机静默
        NetManager.Instance?.HostSendRoundStart(0, 0);
    }

    void Update()
    {
        switch (state)
        {
            case RoundState.Playing:
                SpawnItemLoop();
                CheckRoundEnd();
                break;

            case RoundState.RoundEnded:
                endTimer -= Time.unscaledDeltaTime; // timeScale=0 也要走
                if (endTimer <= 0f)
                {
                    state = RoundState.Restarting;
                    StartCoroutine(StartNewRound());
                }
                break;
        }
    }

    // ---------- 胜负判定（阵营制，通用规则） ----------

    // 统计仍存活的阵营数；≤1 即分出结果（1 = 胜者，0 = 平局）
    void CheckRoundEnd()
    {
        int aliveTeams = 0;
        int lastAliveTeam = -1;
        foreach (int team in DistinctTeams())
        {
            if (AnyAlive(team))
            {
                aliveTeams++;
                lastAliveTeam = team;
            }
        }
        if (aliveTeams > 1)
        {
            return;
        }

        if (aliveTeams == 1)
        {
            winnerTeam = lastAliveTeam;
            AwardRound(winnerTeam);
        }
        else
        {
            winnerTeam = -1;
        }

        // 慢放演出：timeScale 降到 roundConfig.slowMotion——子弹缓飞、AI 慢动、爆炸烟在
        // 慢镜里展开（timeScale=0 会把粒子冻在半空，粒子系统吃 timeScale）
        state = RoundState.RoundEnded;
        Time.timeScale = roundConfig.slowMotion;
        endTimer = roundConfig.restartDelay;
        FocusRoundEnd(aliveTeams); // 相机特写（机位由"谁赢了/死在哪"决定）

        // 联机：胜负裁决广播（比分已加完，带全量 + 结算特写机位=最后阵亡点）。
        // 判空：单机无 NetManager 静默。hasCam 与 host 相机机位同源（FocusRoundEnd
        // 已执行，lastDeathPos 已定）：client 端按同一参数复刻特写
        bool hasCam = lastDeathPos.HasValue;
        NetManager.Instance?.HostSendRoundEnd(winnerTeam, GetScore(TeamOne), GetScore(TeamTwo),
            hasCam, hasCam ? lastDeathPos.Value.x : 0f, hasCam ? lastDeathPos.Value.z : 0f);
    }

    // 结算机位：有胜者 → 盯住存活者（它慢放中可能还在移动）；平局 → 盯
    // 最后阵亡点（TankBase.AnyDied 上报）；都拿不到 → 全图
    void FocusRoundEnd(int aliveTeams)
    {
        if (roundCamera == null)
        {
            return;
        }
        if (lastDeathPos.HasValue)
        {
            roundCamera.Focus(lastDeathPos.Value);
        }
        else
        {
            roundCamera.WatchFullMap();
        }
    }

    // 结算展示中（RoundEnded，慢放几秒）：暂停入口应禁用——展示只有几秒
    // 且到点自动重开，此时开暂停会和重开协程的 timeScale 恢复打架（见 PauseController）
    public bool RoundFrozen => state == RoundState.RoundEnded;

    // 本局涉及的所有阵营号（去重，供判活遍历）
    HashSet<int> DistinctTeams()
    {
        var teams = new HashSet<int>();
        foreach (var p in roster)
        {
            teams.Add(p.team);
        }
        return teams;
    }

    bool AnyAlive(int team)
    {
        foreach (var p in roster)
        {
            if (p.team == team && p.go != null)
            {
                return true;
            }
        }
        return false;
    }

    // 胜方 +1 分并广播（UI 记分板订阅）；平局不加分
    void AwardRound(int team)
    {
        scores.TryGetValue(team, out int s);
        scores[team] = s + 1;
        ScoresUpdated?.Invoke();
    }

    public int GetScore(int team) => scores.TryGetValue(team, out int s) ? s : 0;

    // ---------- 自动重开 ----------

    // 暂停面板"重新开始"：整场重置——比分清零、回到第 1 局、立即重开。
    // 正处结算定格（RoundEnded）也直接打断进入重开；timeScale 由
    // StartNewRound 完成时统一恢复（可能在暂停 timeScale=0 下被调用）
    public void ResetMatch()
    {
        scores.Clear();
        ScoresUpdated?.Invoke();
        roundNumber = 0; // StartNewRound 内部会 ++ 回到 1
        if (state == RoundState.Playing || state == RoundState.RoundEnded)
        {
            state = RoundState.Restarting;
            StartCoroutine(StartNewRound());
        }
    }

    IEnumerator StartNewRound()
    {
        // ---- 清场（暂停态下 Destroy 照常帧末生效）----
        foreach (var p in roster)
        {
            if (p.go != null)
            {
                Destroy(p.go);
            }
        }
        roster.Clear();
        ClearItems();
        // 流弹/池子弹/导弹/激光一并清空，防止跨局残留命中新生坦克
        BulletBase[] bullets = FindObjectsByType<BulletBase>(
            FindObjectsInactive.Include);
        foreach (BulletBase b in bullets)
        {
            Destroy(b.gameObject);
        }
        tankCells.Clear();
        maze = null;

        // ---- 新迷宫（内部会等旧墙销毁后重建 GridMap）----
        mapSpawner.RegenerateRandom();
        yield return null; // 等帧末 Destroy 全部生效

        maze = mapSpawner.Data;
        roundNumber++;
        roundCamera?.WatchFullMap(); // 机位回全图：新图 bounds 已生效，强制重取景
        SpawnRound();
        Time.timeScale = 1f; // 生成完成才恢复世界运行
        state = RoundState.Playing;
        // 联机：新局广播当前比分（跨局累计对账）。判空：单机静默
        NetManager.Instance?.HostSendRoundStart(GetScore(TeamOne), GetScore(TeamTwo));
    }

    // ---------- 参与者生成 ----------

    // 阵营二的人选由主菜单（GameConfig.Mode）决定：
    //   1vAI  → aiPrefab × roundConfig.aiCount；双人 → player2Prefab × 1（固定 1v1）
    //   联机  → tankPrefab(Tank, host 本机玩家) + player2Prefab(Tank 1, client 化身)
    void SpawnRound()
    {
        if (GameConfig.Mode == GameMode.Online)
        {
            // 联机 1v1：场上只有两辆人控车，无 AI。化身车由 NetManager 注册，
            // 吃 client 上行命令；host 车照常吃本机键盘
            SpawnParticipant(tankPrefab, TeamOne);
            GameObject remote = SpawnParticipant(player2Prefab, TeamTwo);
            if (remote != null && remote.TryGetComponent<Tank>(out var tank))
            {
                tank.IsRemote = true;
                // 判空：单机（无 NetManager/非联机）静默——化身概念只在联机存在
                NetManager.Instance?.RegisterRemoteAvatar(tank);
            }
            return;
        }

        bool doubleMode = GameConfig.Mode == GameMode.Double;
        GameObject opponentPrefab = doubleMode ? player2Prefab : aiPrefab;
        int opponentCount = doubleMode ? 1 : roundConfig.aiCount;
        if (doubleMode && player2Prefab == null)
        {
            opponentPrefab = aiPrefab;
            opponentCount = roundConfig.aiCount;
        }

        SpawnParticipant(tankPrefab, TeamOne);
        for (int i = 0; i < opponentCount; i++)
        {
            SpawnParticipant(opponentPrefab, TeamTwo);
        }
    }

    // 单台坦克落到随机空房间中心，yaw 随机，并登记进 roster。返回生成对象
    // （联机分支要拿它标记化身；其余调用方忽略返回值）
    GameObject SpawnParticipant(GameObject prefab, int team)
    {
        if (prefab == null)
        {
            return null;
        }
        Vector2Int? cell = PickFreeMazeCell(tankCells);
        if (cell == null)
        {
            return null;
        }
        tankCells.Add(cell.Value);
        GameObject go = Instantiate(prefab, MazeCellCenter(cell.Value),
            Quaternion.Euler(0f, Random.Range(0f, 360f), 0f)); // 随机朝向
        go.name = prefab.name;
        roster.Add((go, team));
        return go;
    }

    // ---------- 道具 ----------

    void SpawnItemLoop()
    {
        PruneItems();
        if (itemGos.Count >= roundConfig.maxItems)
        {
            itemTimer = 0f;
            return;
        }
        itemTimer += Time.deltaTime;
        if (itemTimer < roundConfig.itemInterval)
        {
            return;
        }
        itemTimer = 0f;
        SpawnItem();
    }

    // 道具被吃掉（Destroy → 引用为 null）→ 同时释放它占的大格
    void PruneItems()
    {
        for (int i = itemGos.Count - 1; i >= 0; i--)
        {
            if (itemGos[i] == null)
            {
                itemGos.RemoveAt(i);
                itemCells.RemoveAt(i);
            }
        }
    }

    void ClearItems()
    {
        foreach (GameObject go in itemGos)
        {
            if (go != null)
            {
                Destroy(go);
            }
        }
        itemGos.Clear();
        itemCells.Clear();
        itemTimer = 0f;
    }

    void SpawnItem()
    {
        if (maze == null || itemPrefab == null || roundConfig.itemPool.Length == 0)
        {
            return;
        }
        Vector2Int? cell = PickFreeMazeCell(itemCells); // 只防道具叠放，不避坦克
        if (cell == null)
        {
            return; // 满图了，本周期跳过，下周期再试
        }
        Power power = roundConfig.itemPool[Random.Range(0, roundConfig.itemPool.Length)];
        GameObject go = Instantiate(itemPrefab, MazeCellCenter(cell.Value), Quaternion.identity);
        go.name = $"Item({power})";
        go.GetComponent<Item>().SetPower(power);
        itemGos.Add(go);
        itemCells.Add(cell.Value);
    }

    // ---------- 共用 ----------

    // 随机抽一个未被占用的迷宫大格（房间总数随尺寸变化，随机 200 次必命中）
    Vector2Int? PickFreeMazeCell(ICollection<Vector2Int> excluded)
    {
        for (int i = 0; i < 200; i++)
        {
            var cell = new Vector2Int(Random.Range(0, maze.cols), Random.Range(0, maze.rows));
            if (!excluded.Contains(cell))
            {
                return cell;
            }
        }
        return null;
    }

    // 大格 (c,r) 中心的世界坐标（y=0，房间地面）
    Vector3 MazeCellCenter(Vector2Int cell)
    {
        Vector2 v = maze.CellCenter(cell.x, cell.y);
        return new Vector3(v.x, 0f, v.y);
    }
}
