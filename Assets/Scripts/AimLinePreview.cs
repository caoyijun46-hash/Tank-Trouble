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

    void DrawPreview()
    {
        Vector3 dir = firePoint.forward;
        PathResult result = BallisticPath.Simulate(
            firePoint.position, dir, config.maxBounces, config.MaxDistance, obstacleMask,
            reuse: null, radius: config.bulletRadius);
        line.startWidth = config.bulletRadius * 2f;
        line.endWidth = config.bulletRadius * 2f;
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
