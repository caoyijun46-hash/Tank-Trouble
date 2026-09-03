using UnityEngine;

// ============================================================
// 固定全图俯视相机（正交，纯俯视）：实现 2D 化观感——
// 视线完全竖直向下（固定 90°，无俯仰角可调），世界 X/Z 与
// 屏幕 X/Y 严格 1:1，无透视无变形。屏幕右 = 世界 +X、上 = +Z。
//
// 地图宽高比 ≈ 2.5:1 > 屏幕 1.78:1 → 按宽适配保证全图入框，
// 上下超出屏幕的部分暂时留空（不铺背景，留待画面风格定了再补）。
// 每局迷宫尺寸随机（MapSpawner.SetBounds 改 GridMap 范围）→
// LateUpdate 读 GridMap.Min/MaxBounds，范围变化时自动重取景。
//
// 纯俯视的副产品：墙只显示顶面（1 厚轮廓条），房间里不存在
// 任何墙体遮挡——这正是 2D 迷宫地图的观感来源。
// ============================================================
[RequireComponent(typeof(Camera))]
public class FixedMapCamera : MonoBehaviour
{
    [Tooltip("视框余量（比例）：0 = 地图边缘刚好贴屏幕边缘")]
    [SerializeField, Range(0f, 0.5f)] private float padding = 0.03f;

    private Camera cam;
    private Vector2 lastMin;
    private Vector2 lastMax;
    private bool framed;

    void Awake()
    {
        cam = GetComponent<Camera>();
        cam.orthographic = true;
    }

    void LateUpdate()
    {
        if (GridMap.Instance == null) return;
        var min = GridMap.Instance.MinBounds;
        var max = GridMap.Instance.MaxBounds;
        if (framed && min == lastMin && max == lastMax) return;
        lastMin = min;
        lastMax = max;
        framed = true;
        Frame(min, max);
    }

    // 每局地图重建后范围变化会自动触发；运行时手动改了参数可调它强制重取景
    public void Reframe() => framed = false;

    void Frame(Vector2 min, Vector2 max)
    {
        float w = max.x - min.x;
        float h = max.y - min.y;
        Vector2 c = (min + max) * 0.5f;

        // 正交 size = 可视半高。取"按宽"与"按高"两种适配的较大者 →
        // 任意宽高比的地图都能整张入框（当前 100×40 下是宽度受限）
        float byWidth = w * 0.5f / cam.aspect;
        cam.orthographicSize = Mathf.Max(byWidth, h * 0.5f) * (1f + padding);

        // 完全俯视：屏幕右 = +X、上 = +Z。正交下相机高度不影响成像，
        // 只要全部物体（墙顶最高 y=6）落在近/远裁剪面内即可。
        // 固定高 7：near=0.3 → 近裁剪面在 y=6.7，正好压住墙顶 6，不留无谓高度
        transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        transform.position = new Vector3(c.x, 45f, c.y);

        // far 对齐 Test 场景相机（near0.3/far50）的深度预算：
        // 正交下墙顶(y=6)到地面(y=0)的深度差恒为 6，far=1000 时这段只占深度
        // 缓冲 0.6%，描边后处理的 Sobel 梯度小到过不了阈值 → 效果退化。
        cam.farClipPlane = 50f;
        cam.nearClipPlane = 0.3f;
    }
}
