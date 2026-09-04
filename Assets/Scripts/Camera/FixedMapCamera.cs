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
//
// 结算特写（Focus / WatchFullMap）：正交相机的机位只由
// 位置 + orthographicSize 决定，没有景深概念。特写时每帧把机位
// 平滑推向目标；平滑速率按 unscaledDeltaTime 走——结算在慢放
// （timeScale≈0.1）下进行，用缩放时间镜头动作会跟着慢放拖沓。
// ============================================================
[RequireComponent(typeof(Camera))]
public class FixedMapCamera : MonoBehaviour
{
    [Tooltip("视框余量（比例）：0 = 地图边缘刚好贴屏幕边缘")]
    [SerializeField, Range(0f, 0.5f)] private float padding = 0.03f;

    [Header("结算特写")]
    [Tooltip("特写半高（orthographicSize）：坦克长 4、房间格 10，7 左右框住击杀现场")]
    [SerializeField, Range(2f, 30f)] private float focusSize = 7f;
    [Tooltip("机位平滑速率（次/秒，按真实时间）")]
    [SerializeField, Range(0.5f, 20f)] private float followRate = 6f;

    private Camera cam;
    private Vector2 lastMin;
    private Vector2 lastMax;
    private bool framed;

    // 特写机位：focusTarget != null → 跟随对象（胜者慢放中可能还在移动）；
    // 否则 hasFocusPoint → 固定点（平局盯最后阵亡处）
    private Transform focusTarget;
    private Vector3 focusPoint;
    private bool hasFocusPoint;

    void Awake()
    {
        cam = GetComponent<Camera>();
        cam.orthographic = true;
    }

    void LateUpdate()
    {
        if (focusTarget != null || hasFocusPoint)
        {
            ApplyFocus();
            return;
        }
        if (GridMap.Instance == null) return;
        var min = GridMap.Instance.MinBounds;
        var max = GridMap.Instance.MaxBounds;
        if (framed && min == lastMin && max == lastMax) return;
        lastMin = min;
        lastMax = max;
        framed = true;
        Frame(min, max);
    }

    // 特写：盯住一个对象（胜者），或一个固定点（平局最后阵亡处）
    public void Focus(Transform target)
    {
        focusTarget = target;
        hasFocusPoint = false;
    }

    public void Focus(Vector3 point)
    {
        focusTarget = null;
        hasFocusPoint = true;
        focusPoint = point;
    }

    // 回到全图：清特写机位并强制下一帧按当前 bounds 重取景
    public void WatchFullMap()
    {
        focusTarget = null;
        hasFocusPoint = false;
        framed = false;
    }

    // 每帧把位置/半高平滑推向机位；指数衰减趋近，到点自然停
    void ApplyFocus()
    {
        Vector3 p = focusTarget != null ? focusTarget.position : focusPoint;
        p.y = 45f; // 相机高度恒定（与 Frame 一致；正交成像与高度无关）
        float k = 1f - Mathf.Exp(-followRate * Time.unscaledDeltaTime);
        transform.position = Vector3.Lerp(transform.position, p, k);
        cam.orthographicSize = Mathf.Lerp(cam.orthographicSize, focusSize, k);
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
        // 只要全部物体落在近/远裁剪面内即可。高度 45 + near0.3/far50：
        // 近裁剪面在 y=44.7（上方无物体），远裁剪面在 y=-5（地面 0 之下），
        // 墙顶（最高 6）到地面全在深度范围内
        transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        transform.position = new Vector3(c.x, 45f, c.y);

        // far 对齐 Test 场景相机（near0.3/far50）的深度预算：
        // 正交下墙顶(y=6)到地面(y=0)的深度差恒为 6，far=1000 时这段只占深度
        // 缓冲 0.6%，描边后处理的 Sobel 梯度小到过不了阈值 → 效果退化。
        cam.farClipPlane = 50f;
        cam.nearClipPlane = 0.3f;
    }
}
