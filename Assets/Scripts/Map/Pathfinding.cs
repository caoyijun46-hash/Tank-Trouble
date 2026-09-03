using System.Collections.Generic;
using UnityEngine;

// A* 寻路原语：纯静态函数，输入网格和起终点，输出路径格子序列
// 8 邻域移动 + 对角线距离启发式（可采纳），斜对角移动禁止穿越墙角
public static class Pathfinding
{
    private static readonly Vector2Int[] Directions =
    {
        new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1),
        new Vector2Int(1, 1), new Vector2Int(1, -1), new Vector2Int(-1, 1), new Vector2Int(-1, -1)
    };

    private static readonly float[] Costs =
    {
        1f, 1f, 1f, 1f, Mathf.Sqrt(2f), Mathf.Sqrt(2f), Mathf.Sqrt(2f), Mathf.Sqrt(2f)
    };

    // 返回从 start 到 goal 的格子序列（含两端）；无路径返回 null。
    // unitRadius：单位体积半径（格数），导弹传 0，坦克传 2 等
    public static List<Vector2Int> FindPath(GridMap grid, Vector2Int start, Vector2Int goal, int unitRadius = 0)
    {
        if (!grid.IsWalkable(start, unitRadius) || !grid.IsWalkable(goal, unitRadius))
        {
            return null;
        }
        if (start == goal)
        {
            return new List<Vector2Int> { start };
        }

        int width = grid.Width;
        int height = grid.Height;
        int size = width * height;

        float[] gScore = new float[size];
        int[] cameFrom = new int[size];
        for (int i = 0; i < size; i++)
        {
            gScore[i] = float.PositiveInfinity;
            cameFrom[i] = -1;
        }

        // open 列表：朴素实现（每次线性扫描找 f 最小）。
        // 规模小（<1 万格）足够快；要优化就换二叉堆
        List<int> open = new List<int>();
        bool[] inOpen = new bool[size];
        bool[] closed = new bool[size];

        int startIdx = Idx(start, width);
        int goalIdx = Idx(goal, width);
        gScore[startIdx] = 0f;
        open.Add(startIdx);
        inOpen[startIdx] = true;

        while (open.Count > 0)
        {
            int currentIdx = open[0];
            float bestF = gScore[currentIdx] + Heuristic(IdxToCell(currentIdx, width), goal);
            for (int i = 1; i < open.Count; i++)
            {
                int idx = open[i];
                float f = gScore[idx] + Heuristic(IdxToCell(idx, width), goal);
                if (f < bestF)
                {
                    bestF = f;
                    currentIdx = idx;
                }
            }

            if (currentIdx == goalIdx)
            {
                break;
            }

            open.Remove(currentIdx);
            inOpen[currentIdx] = false;
            closed[currentIdx] = true;

            Vector2Int current = IdxToCell(currentIdx, width);
            for (int d = 0; d < Directions.Length; d++)
            {
                Vector2Int next = current + Directions[d];
                if (next.x < 0 || next.x >= width || next.y < 0 || next.y >= height)
                {
                    continue;
                }

                // 对角移动禁止斜穿墙角：两个正交邻居必须都可走
                if (d >= 4)
                {
                    Vector2Int ortho1 = new Vector2Int(current.x + Directions[d].x, current.y);
                    Vector2Int ortho2 = new Vector2Int(current.x, current.y + Directions[d].y);
                    if (!grid.IsWalkable(ortho1, unitRadius) || !grid.IsWalkable(ortho2, unitRadius))
                    {
                        continue;
                    }
                }

                if (!grid.IsWalkable(next, unitRadius))
                {
                    continue;
                }

                int nextIdx = Idx(next, width);
                if (closed[nextIdx])
                {
                    continue;
                }

                float tentativeG = gScore[currentIdx] + Costs[d];
                if (tentativeG < gScore[nextIdx])
                {
                    gScore[nextIdx] = tentativeG;
                    cameFrom[nextIdx] = currentIdx;
                    if (!inOpen[nextIdx])
                    {
                        open.Add(nextIdx);
                        inOpen[nextIdx] = true;
                    }
                }
            }
        }

        if (cameFrom[goalIdx] == -1)
        {
            return null; // 终点不可达
        }

        List<Vector2Int> path = new List<Vector2Int>();
        int cur = goalIdx;
        while (cur != -1)
        {
            path.Add(IdxToCell(cur, width));
            cur = cameFrom[cur];
        }
        path.Reverse();
        return path;
    }

    static int Idx(Vector2Int cell, int width) => cell.y * width + cell.x;

    static Vector2Int IdxToCell(int idx, int width) => new Vector2Int(idx % width, idx / width);

    // 对角线距离（octile）：8 邻域下可采纳的最优启发式
    static float Heuristic(Vector2Int a, Vector2Int b)
    {
        int dx = Mathf.Abs(a.x - b.x);
        int dy = Mathf.Abs(a.y - b.y);
        return Mathf.Max(dx, dy) + (Mathf.Sqrt(2f) - 1f) * Mathf.Min(dx, dy);
    }
}
