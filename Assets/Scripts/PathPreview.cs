using System.Collections.Generic;
using UnityEngine;

// 调试工具：可视化 A* 寻路结果。
// 挂场景空物体上，指定起点/终点 Transform，Play 模式下在 Scene 视图查看绿色路径
public class PathPreview : MonoBehaviour
{
    [SerializeField] private Transform startPoint;
    [SerializeField] private Transform goalPoint;
    [SerializeField] private int unitRadius = 0; // 单位体积半径：0=导弹（贴墙走），2=坦克（离墙远）
    [SerializeField] private bool updateEveryFrame = true;

    private List<Vector2Int> path;

    void Update()
    {
        if (updateEveryFrame && startPoint != null && goalPoint != null)
        {
            Find();
        }
    }

    // 编辑态也可手动触发（菜单/右键调用），但需要先 Play 让 GridMap 构建
    [ContextMenu("Find Path")]
    void Find()
    {
        GridMap grid = GridMap.Instance;
        if (grid == null)
        {
            path = null;
            return;
        }

        path = Pathfinding.FindPath(
            grid,
            grid.WorldToCell(startPoint.position),
            grid.WorldToCell(goalPoint.position),
            unitRadius);
    }

    void OnDrawGizmos()
    {
        if (path == null || GridMap.Instance == null)
        {
            return;
        }

        Gizmos.color = Color.green;
        for (int i = 0; i < path.Count; i++)
        {
            Vector3 p = GridMap.Instance.CellToWorld(path[i]);
            Gizmos.DrawCube(p, Vector3.one * 0.8f);
            if (i > 0)
            {
                Gizmos.DrawLine(GridMap.Instance.CellToWorld(path[i - 1]), p);
            }
        }
    }
}
