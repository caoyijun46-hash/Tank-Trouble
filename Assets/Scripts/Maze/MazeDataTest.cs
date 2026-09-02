using UnityEngine;

// 决策层验收辅助：在 Unity 里生成迷宫并打印到 Console。
// 用法：挂到 Test 场景任意空物体 → Play（或编辑器里右键组件 → Print）→ 看 Console
public class MazeDataTest : MonoBehaviour
{
    [SerializeField] private int cols = 10;
    [SerializeField] private int rows = 4;
    [SerializeField] private int seed = 42;

    [ContextMenu("Print (决策层可视化)")]
    public void Print()
    {
        var maze = MazeData.Generate(cols, rows, seed);
        Debug.Log($"===== MazeData {cols}x{rows} seed={seed} =====");
        Debug.Log($"总墙段数: {maze.TotalWallSegments}  (期望 {(cols + 1) * rows + cols * (rows + 1) - (cols * rows - 1)})");
        Debug.Log(maze.ToAscii());
    }

    void Awake()
    {
        Print();
    }
}
