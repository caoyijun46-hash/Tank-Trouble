using UnityEngine;

// 出生音组件（挂 client 端的子弹壳：Dead Bullet / Dead Laser / Dead Missile）：
// 壳被 Spawn 事件实例化 = host 世界里"某辆坦克开火了"的事实 → 此刻播开火音。
//
// 为什么挂在子弹壳而不是输入端：一次开火在 client 只能响一声。自己开火若在
// 输入端播即时音，壳出生的那一帧会再响一次（约半 RTT 后的回声），所以统一
// 由这条链路发声——对方（host 玩家/AI）开火也走同一链路，天然有声。
// 代价：自己开火音延迟 ≈ 半 RTT（同机几十 ms 无感；远程网络下是真实往返的一部分）
//
// 只挂 client 壳：host 端玩法 prefab（Bullet/Laser/Missile）已有 TankBase.Fire
// 的 PlayFire，挂上会双响。静态 API 判空——没拖 AudioManager 的场景静默
public class SpawnSound : MonoBehaviour
{
    void OnEnable()
    {
        AudioManager.PlayFire(); // Instantiate 当帧即响（不用等 Start）
    }
}
