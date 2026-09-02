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
    // cols×rows 迷宫，指定种子；DFS 从 (0,0) 保证全连通
    public static MazeData Generate(int cols, int rows, int seed)
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
        return maze;
    }

    // ================= 验收辅助 =================
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
