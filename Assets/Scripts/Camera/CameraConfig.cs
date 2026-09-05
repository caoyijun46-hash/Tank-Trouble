using UnityEngine;

// 固定俯视相机参数（FixedMapCamera 读取）：全图取景余量 + 结算特写机位
[CreateAssetMenu(fileName = "CameraConfig", menuName = "TankGame/Camera Config")]
public class CameraConfig : ScriptableObject
{
    [Header("全图取景")]
    [Tooltip("视框余量（比例）：0 = 地图边缘刚好贴屏幕边缘")]
    [Range(0f, 0.5f)] public float padding = 0.03f;

    [Header("结算特写")]
    [Tooltip("特写半高（orthographicSize）：坦克长 4、房间格 10，7 左右框住击杀现场")]
    [Range(2f, 30f)] public float focusSize = 7f;
    [Tooltip("机位平滑速率（次/秒，按真实时间）")]
    [Range(0.5f, 20f)] public float followRate = 6f;
}
