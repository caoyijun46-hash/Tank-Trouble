using UnityEngine;

// 迷宫生成参数（MapSpawner 读取）：格数范围/种子/补开洞比例。
// 墙/地板的资产引用（wallPrefab/floorMat）不进 SO，留在 MapSpawner 上
[CreateAssetMenu(fileName = "MazeConfig", menuName = "TankGame/Maze Config")]
public class MazeConfig : ScriptableObject
{
    [Header("迷宫尺寸（每局在范围内随机，大格固定 10×10）")]
    [Tooltip("X 向格数范围（默认 8~10 → 地图 80~100 宽）")]
    public int colsMin = 8;
    public int colsMax = 10;
    [Tooltip("Z 向格数范围（默认 3~4 → 地图 30~40 深）")]
    public int rowsMin = 3;
    public int rowsMax = 4;

    [Header("种子与补开洞（DFS 生成树后额外打通，制造环/多通路）")]
    [Tooltip("0 = 每局随机种子（不同迷宫）")]
    public int seed = 0;
    [Tooltip("开洞比例下限：对本局「仍站立的共享内墙」的比例")]
    [Range(0f, 1f)] public float loopMin = 0.1f;
    [Tooltip("开洞比例上限：每局在区间内随机取一个比例（设 0 = 无环完美迷宫）")]
    [Range(0f, 1f)] public float loopMax = 0.25f;
}
