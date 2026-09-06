using System.Collections.Generic;
using UnityEngine;

// 插值缓冲：客户端平滑的核心。原理：渲染必须"先拿到窗口两端快照"才敢动，
// 所以它永远渲染过去（固有延迟 ≈ 缓冲深度 × 快照间隔）——这是平滑的代价，
// 不是缺陷。缓冲深度 = 延迟预算旋钮：越深越抗抖动，画面越滞后。
//
// 推进用本地真实时间（unscaledDeltaTime）：两端时钟速率一致（只差起点偏移），
// 所以"窗口时间跨度内消耗多少"可以用本地流逝推，不需要绝对时钟同步。
// 丢包时的行为：窗口走完且队列空 → 冻结在最新端点；新快照到达自动滑窗续走
public class InterpBuffer
{
    readonly Queue<SnapshotData> queue = new Queue<SnapshotData>();
    public int Depth { get; set; } = 3;

    SnapshotData? s0;  // 窗口起点
    SnapshotData? s1;  // 窗口终点（"未来端点"，必须先于渲染拿到）
    float progress;    // 窗口内已消耗比例 0..1

    public void Push(SnapshotData snap)
    {
        queue.Enqueue(snap);
        while (queue.Count > Depth)
        {
            queue.Dequeue(); // 只留最近 Depth 个
        }
    }

    // 每帧取当前应渲染姿态；返回 false = 缓冲不足，渲染层冻结在最后位置
    public bool GetPose(out SnapshotData pose)
    {
        // 窗口未开且已攒够两帧 → 开窗（队列是历史序，前两个就是窗口）
        if (s1 == null && queue.Count >= 2)
        {
            s0 = queue.Dequeue();
            s1 = queue.Dequeue();
            progress = 0f;
        }
        if (s1 == null)
        {
            pose = default;
            return false;
        }

        // 按窗口的时间跨度消耗进度；走完就滑到队列里下一个快照
        float span = s1.Value.hostTime - s0.Value.hostTime;
        if (span > 1e-4f)
        {
            progress += Time.unscaledDeltaTime / span;
        }
        while (progress >= 1f && queue.Count > 0)
        {
            progress -= 1f;
            s0 = s1;
            s1 = queue.Dequeue();
        }
        if (progress >= 1f)
        {
            // 缓冲耗尽：冻结在最新端点（连发丢包/断流时画面停但不崩）
            pose = s1.Value;
            return true;
        }

        // 插值：位置 Lerp；yaw 是周期量必须 LerpAngle（否则 350°→10° 会反绕 340°）
        float t = Mathf.Clamp01(progress);
        pose.seq = s1.Value.seq;
        pose.hostTime = s1.Value.hostTime;
        pose.x = Mathf.Lerp(s0.Value.x, s1.Value.x, t);
        pose.z = Mathf.Lerp(s0.Value.z, s1.Value.z, t);
        pose.yaw = Mathf.LerpAngle(s0.Value.yaw, s1.Value.yaw, t);
        return true;
    }
}
