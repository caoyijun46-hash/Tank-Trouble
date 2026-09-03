using System.Collections.Generic;
using UnityEngine;

// 弹道结局：三类，语义各不相同
public enum PathEnd
{
    HitTank,    // 命中坦克：子弹会消灭它，路径在此终结
    HitWall,    // 撞墙反弹，但反弹次数已用尽（模拟在此截断）
    Exhausted   // 路程耗尽，什么也没碰到
}

// 模拟结果：预览和 AI 消费的同一份数据
public class PathResult
{
    public List<Vector3> points = new List<Vector3>(); // 折线点列，points[0] 是起点
    public PathEnd end;
    public Collider hitCollider;  // 命中对象（HitTank 时有效）
    public float length;          // 模拟覆盖的总长度 = 飞行时间 × 子弹速度
}

// 弹道原语：纯计算，无状态，确定性
public static class BallisticPath
{
    public static PathResult Simulate(Vector3 origin, Vector3 dir,
                                      int maxBounces, float maxDistance,
                                      int layerMask = Physics.DefaultRaycastLayers,
                                      PathResult reuse = null,
                                      float radius = 0f,
                                      Collider ignoreCollider = null)
    {
        // reuse：调用方高频枚举（如 AI 角度求解）时传入缓存对象，避免每帧分配
        if (reuse == null)
        {
            reuse = new PathResult();
        }
        else
        {
            reuse.points.Clear();
        }
        PathResult result = reuse;
        result.points.Add(origin);

        Vector3 currentDir = dir;
        Vector3 currentPos = origin;
        float remaining = maxDistance;

        // 段数 = 反弹次数 + 1，第一段是发射段
        for (int i = 0; i <= maxBounces; i++)
        {
            if (remaining <= 0f)
            {
                result.end = PathEnd.Exhausted;
                break;
            }

            // 模拟子弹体积：radius > 0 用 SphereCast（球体扫描），radius = 0 用 Raycast（激光等极小球体）。
            // 注意两者行为差异：SphereCast 起点在碰撞体内部时会立即命中（球体重叠）
            RaycastHit hit;
            bool hitDetected;
            if (radius > 0f)
            {
                hitDetected = Physics.SphereCast(currentPos, radius, currentDir, out hit, remaining, layerMask);
            }
            else
            {
                hitDetected = Physics.Raycast(currentPos, currentDir, out hit, remaining, layerMask);
            }

            if (hitDetected)
            {
                if (hit.collider == ignoreCollider)
                {
                    // 穿透发射者自身（AI 枚举起点在坦克碰撞体内部，必须忽略否则全方向自命中）。
                    // 与物理自伤不冲突：这是枚举模拟的几何近似，物理自伤仍然存在
                    remaining -= hit.distance;
                    currentPos = hit.point + currentDir * 0.001f;
                    continue;
                }

                // 球心修正：SphereCast 的 hit.point 是球面接触点，球心在表面外一个半径
                Vector3 sphereCenter = hit.point + hit.normal * radius;
                result.points.Add(sphereCenter);
                remaining -= hit.distance;

                if (hit.collider.CompareTag("Tank"))
                {
                    result.end = PathEnd.HitTank;
                    result.hitCollider = hit.collider;
                    break;
                }

                result.end = PathEnd.HitWall; // 先记下"这段撞墙"，若反弹次数耗尽以此收尾
                currentDir = Vector3.Reflect(currentDir, hit.normal);
                currentPos = sphereCenter;
            }
            else
            {
                result.points.Add(currentPos + currentDir * remaining);
                result.end = PathEnd.Exhausted;
                break;
            }
        }

        result.length = maxDistance - remaining;
        return result;
    }
}
