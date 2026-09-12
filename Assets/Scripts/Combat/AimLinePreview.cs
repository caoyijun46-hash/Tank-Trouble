using UnityEngine;

public class AimLinePreview : MonoBehaviour
{
    [SerializeField] private Transform firePoint;
    [SerializeField] private LineRenderer line;
    [SerializeField] private BallisticConfig config; // 与子弹共用同一份配置，预览和实际一致
    [SerializeField] private LayerMask obstacleMask = ~0; // Inspector 里把 Bullet 层勾掉，预览线不再被子弹挡
    [SerializeField] private Color normalColor = Color.green;
    [SerializeField] private Color hitColor = Color.red;

    void Start()
    {
        if (line == null)
        {
            line = GetComponent<LineRenderer>();
        }
    }


    void Update()
    {
        DrawPreview();
    }

    // 由 TankBase 按 Power.Laser 启停：开启 = 恢复逐帧绘制；
    // 关闭只停 Update 不会抹掉 LineRenderer 已画的网格，必须清空线条，
    // 否则 Laser 打完后预览线残留在原地
    public void Show(bool on)
    {
        enabled = on;
        if (!on && line != null)
        {
            line.positionCount = 0;
        }
    }

    void DrawPreview()
    {
        Vector3 dir = firePoint.forward;
        PathResult result = BallisticPath.Simulate(
            firePoint.position, dir, config.maxBounces, config.MaxDistance, obstacleMask,
            reuse: null, radius: config.bulletRadius);
        // 线宽与碰撞半径解耦：radius=0（如激光点光束）时给最小可见宽度——
        // 否则线宽为 0，预览线看起来"消失"（Ballistic_Laser.bulletRadius=0 的坑）
        float width = Mathf.Max(config.bulletRadius * 2f, 0.1f);
        line.startWidth = width;
        line.endWidth = width;
        line.positionCount = result.points.Count;
        for (int i = 0; i < result.points.Count; i++)
        {
            line.SetPosition(i, result.points[i]);
        }
        Color c = result.end == PathEnd.HitTank ? hitColor : normalColor;
        line.startColor = c;
        line.endColor = c;
    }
}
