using System;
using System.Collections.Generic;
using UnityEngine;

// ============================================================
// 决策层：迷宫地图的抽象描述（纯数据 + 生成算法）
//
// 模型约定（与需求方确认）：
//   - 地图 = cols × rows 个"名义格"，每格 10×10（世界单位），紧密排列
//   - 每格内部净空 9×9：四面被厚 1 的墙占据（每侧 0.5）→ "10×10 实际 9×9"
//   - 墙 = 1 厚 × 10 长的墙段，位于相邻格之间的边界格线上（共享墙）
//   - 外框 = 地图最外圈格线（x=0 / x=cols*10 / z=0 / z=rows*10）上的墙段
//   - 坦克活动区 = 每格内部 9×9 区域；两格间无墙段 = 通道（整面开放，无门洞概念）
//
// 数据结构：
//   vWall[i, r]  x=i*10 处（i ∈ [0..cols]）沿 Z 向的第 r 段墙，true=墙存在
//   hWall[c, j]  z=j*10 处（j ∈ [0..rows]）沿 X 向的第 c 段墙，true=墙存在
//   每段长 10（= 格边），厚 1（执行层按此建碰撞体）
//
// 生成算法（DFS 保证连通）：
//   初始全墙 → 从格 (0,0) 深度优先遍历，每访问一个新格，
//   打通它与当前格之间的那面墙 → 生成树保证全部格可达且无环。
//   外框段（i=0/cols、j=0/rows）永不打通。
//
// 可选补开洞（Generate 传 loopMin/loopMax 比例区间，>0 时启用）：
//   树的迷宫是"完美迷宫"（任意两格间唯一路径）。补开洞 = 在树上加边：
//   连通图每加一条边恰产生一个新环 → 每开一面墙，迷宫多一个独立环
//   （多一条可选通路）。只动仍站立的共享内墙，外框永不参与；
//   分散约束：被开的洞与其共角点的几何相邻墙段互斥，洞不会连成片。
// ============================================================
public class MazeData
{
    public const float CellSize = 10f;    // 名义格边长（含两侧墙厚）
    public const float WallThick = 1f;    // 墙段厚度

    public readonly int cols;             // X 向格数
    public readonly int rows;             // Z 向格数

    // 墙段存在性（true = 有墙）。索引规则见类注释。
    public readonly bool[,] vWall;        // [cols+1, rows]
    public readonly bool[,] hWall;        // [cols, rows+1]

    // 生成时的随机种子（可复现）
    public readonly int seed;

    MazeData(int cols, int rows, int seed)
    {
        this.cols = cols;
        this.rows = rows;
        this.seed = seed;
        vWall = new bool[cols + 1, rows];
        hWall = new bool[cols, rows + 1];
        for (int i = 0; i <= cols; i++)
            for (int r = 0; r < rows; r++)
                vWall[i, r] = true;
        for (int c = 0; c < cols; c++)
            for (int j = 0; j <= rows; j++)
                hWall[c, j] = true;
    }

    // ================= 世界坐标换算 =================
    // 名义格 (c,r) 的中心（XZ，y=0）
    public Vector2 CellCenter(int c, int r)
        => new Vector2(c * CellSize + CellSize * 0.5f, r * CellSize + CellSize * 0.5f);

    // 垂直墙段 vWall[i,r] 中心（世界 XZ）
    public Vector2 VWallCenter(int i, int r)
        => new Vector2(i * CellSize, r * CellSize + CellSize * 0.5f);

    // 水平墙段 hWall[c,j] 中心（世界 XZ）
    public Vector2 HWallCenter(int c, int j)
        => new Vector2(c * CellSize + CellSize * 0.5f, j * CellSize);

    // 整张地图的外框范围（含外框墙半厚）
    public float Width  => cols * CellSize;
    public float Depth  => rows * CellSize;

    // ================= 生成 =================
    // cols×rows 迷宫，指定种子；DFS 从 (0,0) 保证全连通。
    // loopMin/loopMax：补开洞比例区间（对剩余共享墙的比例），默认 0 = 完美迷宫（无环）。
    // 比例也在同一 rng 上抽取 → 同 seed 的整张迷宫（含环）可完整复现
    public static MazeData Generate(int cols, int rows, int seed, float loopMin = 0f, float loopMax = 0f)
    {
        var maze = new MazeData(cols, rows, seed);
        var rng = new System.Random(seed);

        bool[] visited = new bool[cols * rows];
        int V(int c, int r) => r * cols + c;

        // 方向：右/下/左/上（先右后下保证删墙坐标简单）
        (int dc, int dr)[] dirs = { (1, 0), (0, 1), (-1, 0), (0, -1) };

        void Shuffle<T>(T[] arr)
        {
            for (int i = arr.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (arr[i], arr[j]) = (arr[j], arr[i]);
            }
        }

        // 栈式 DFS
        var stack = new Stack<(int c, int r)>();
        stack.Push((0, 0));
        visited[V(0, 0)] = true;

        while (stack.Count > 0)
        {
            var (c, r) = stack.Pop();
            Shuffle(dirs);
            foreach (var (dc, dr) in dirs)
            {
                int nc = c + dc, nr = r + dr;
                if (nc < 0 || nc >= cols || nr < 0 || nr >= rows) continue;
                if (visited[V(nc, nr)]) continue;

                // 打通两格之间的共享墙段
                if (dc == 1)       maze.vWall[c + 1, r] = false;  // 右边界 vWall[c+1, r]
                else if (dc == -1) maze.vWall[c, r] = false;      // 左边界 vWall[c, r]
                else if (dr == 1)  maze.hWall[c, r + 1] = false;  // 下边界 hWall[c, r+1]
                else               maze.hWall[c, r] = false;      // 上边界 hWall[c, r]

                visited[V(nc, nr)] = true;
                stack.Push((nc, nr));
            }
        }

        // ---- 第二阶段：补开洞（把树变成有环迷宫）----
        if (loopMax > 0f)
        {
            float lo = Mathf.Clamp01(Mathf.Min(loopMin, loopMax));
            float hi = Mathf.Clamp01(Mathf.Max(loopMin, loopMax));
            float ratio = lo + (hi - lo) * (float)rng.NextDouble();
            maze.CarveLoops(rng, ratio);
        }

        return maze;
    }

    // ================= 补开洞（树 → 有环） =================
    // 候选 = 仍站立的共享墙段。每段用两端格点标识（整数格线坐标）：
    //   水平段 hWall[c,j] ⇔ (c,j)-(c+1,j)；垂直段 vWall[i,r] ⇔ (i,r)-(i,r+1)
    // 分散约束：候选若与"已开洞"共角点（几何相邻）则跳过 → 洞不会连成片，
    // 只允许单洞或链状走廊；约束可能使实际开洞数 < 目标（候选耗尽即止）。
    // 外框墙不夹在两格之间 → 不在候选内，地图外圈永不开口。
    void CarveLoops(System.Random rng, float ratio)
    {
        // 候选收集：内列线 i∈[1,cols-1]、内行线 j∈[1,rows-1] 上的站立段
        var cand = new List<(int x1, int z1, int x2, int z2)>();
        for (int c = 0; c < cols; c++)
            for (int j = 1; j < rows; j++)
                if (hWall[c, j]) cand.Add((c, j, c + 1, j));
        for (int i = 1; i < cols; i++)
            for (int r = 0; r < rows; r++)
                if (vWall[i, r]) cand.Add((i, r, i, r + 1));

        // Fisher–Yates：洗牌后的顺序决定哪些候选被优先开（与 DFS 共用 rng → 同 seed 可复现）
        for (int i = cand.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (cand[i], cand[j]) = (cand[j], cand[i]);
        }

        // 两条墙段是否共用一个格点（水平相邻或转角相交都算）
        bool SharesCorner((int x1, int z1, int x2, int z2) a, (int x1, int z1, int x2, int z2) b)
            => (a.x1 == b.x1 && a.z1 == b.z1) || (a.x1 == b.x2 && a.z1 == b.z2)
            || (a.x2 == b.x1 && a.z2 == b.z1) || (a.x2 == b.x2 && a.z2 == b.z2);

        void Open((int x1, int z1, int x2, int z2) s)
        {
            if (s.z1 == s.z2) hWall[s.x1, s.z1] = false; // 水平段
            else              vWall[s.x1, s.z1] = false; // 垂直段（x1==x2，z 用起点的 r）
        }

        int target = (int)(cand.Count * ratio); // 目标开洞数（向下取整）
        var opened = new List<(int x1, int z1, int x2, int z2)>();
        foreach (var seg in cand)
        {
            if (opened.Count >= target) break;
            bool conflict = false;
            foreach (var done in opened)
            {
                if (SharesCorner(seg, done)) { conflict = true; break; }
            }
            if (conflict) continue;
            Open(seg);
            opened.Add(seg);
        }
        ExtraOpenings = opened.Count;
    }

    // ================= 验收辅助 =================
    // 本局实际补开的洞数（分散约束下可能小于目标；0 = 完美迷宫无环）
    public int ExtraOpenings { get; private set; }

    // 全部墙段数
    public int TotalWallSegments
    {
        get
        {
            int n = 0;
            foreach (bool b in vWall) if (b) n++;
            foreach (bool b in hWall) if (b) n++;
            return n;
        }
    }

    // 格 (c,r) 与邻格是否连通（无墙）
    public bool IsOpenBetween(int c, int r, int nc, int nr)
    {
        if (nc == c + 1) return !vWall[c + 1, r];
        if (nc == c - 1) return !vWall[c, r];
        if (nr == r + 1) return !hWall[c, r + 1];
        if (nr == r - 1) return !hWall[c, r];
        return false;
    }

    // ============ 控制台可视化（决策层验收用） ============
    // 字符含义：
    //   '#' = 该格左/上边界有墙段   '.' = 格心(净空)   ' ' = 无墙(已打通，通道)
    // 行结构（自 Z 大向小打印）：
    //   墙行 : 每个格 2 字符 = 顶墙[hWall[c][j]] + 分隔；外框行必全 '#'
    //   格行 : 每格 = 左墙[vWall[c][r]] + '.' + 右墙[vWall[c+1][r]]
    // 空格在邻格间补位 → 墙列在同一字符列，观感整齐。
    public string ToAscii()
    {
        var sb = new System.Text.StringBuilder();

        // 顶部外框墙行：j=rows，hWall 全 true
        sb.Append(WallRow(rows));

        for (int r = rows - 1; r >= 0; r--)
        {
            // 格心行
            sb.Append(vWall[0, r] ? '#' : ' ');   // 最左列 = vWall[0]（左外框）
            for (int c = 0; c < cols; c++)
            {
                sb.Append('0');
                sb.Append(vWall[c + 1, r] ? '#' : ' ');  // 右边界段
            }
            sb.Append('\n');

            if (r > 0)
                sb.Append(WallRow(r));
        }

        // 底部外框
        sb.Append(WallRow(0));
        return sb.ToString();
    }

    // 一行"顶墙/底墙"段：hWall[c][j]
    string WallRow(int j)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('#');  // 与格心行最左外框对齐
        for (int c = 0; c < cols; c++)
        {
            sb.Append(hWall[c, j] ? '#' : ' ');
            sb.Append(' ');  // 与格心行的格间空隙对齐
        }
        return sb.ToString() + "\n";
    }
}
