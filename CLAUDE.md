# 坦克动荡 · 项目约定

Unity 坦克对战原型：随机迷宫地图 + 玩家/AI 坦克对轰（子弹/激光/导弹弹道）。中文沟通，代码与注释用英文标识符 + 中文注释说明 WHY。

## 脚本目录（功能域分类轴，单数英文命名）

```
Assets/Scripts/
├── Map/        GridMap(网格/寻路数据)  Pathfinding(A*)  Maze/(迷宫生成)
│   └── Maze/   MazeData(决策层)  MapSpawner(执行层)  MazeDataTest(验收)
├── Combat/     弹道与弹药域: BallisticConfig/Path + BulletBase + Bullet/Laser/Missile + AimLinePreview
├── Unit/       单位域: TankBase + Tank/TankAI + Item
├── Core/       游戏流程: GameManager + ScoreHud/PauseController(UI 总控)
├── Camera/     相机: FixedMapCamera
├── Audio/      音频: AudioManager(全局单例, 见下方音频纪律)
└── Particle/   粒子: DestroyAfterDelay 等通用组件
```

- 新增脚本先对号入座；只有"它属于哪个域"一条分类轴，基类跟子类同目录，不设 Base 杂物堆。
- 命名：文件主类名 = 文件名（PascalCase）；场景可挂的组件必须是 MonoBehaviour。
- 移动脚本必须 `.cs` + `.cs.meta` 成对移动（引用按 GUID，路径变了不断连）。
- 资源目录约定：ScriptableObject 资产 → `Assets/Config/`；预制体 → `Assets/Prefabs/`；材质/物理材质 → `Assets/Materials/`；音频 → `Assets/Sound/`。

## 配置纪律（数值参数统一进 Config 资产）

- 玩法数值按域收进 `Assets/Config/` 的 ScriptableObject（Unit/Ai/Round/Maze/Camera/Ballistic 各一份），组件只留一个 Config 引用——不设组件侧第二套字段（双真相源打架）；组件内兜底常量仅供缺配不崩，不算配置源。
- 资产引用（prefab/材质/clip/相机）不进 Config，留在场景/prefab 组件上。
- 想差异化（如 AI 难度）：复制一份资产改数值，组件换拖即可，代码零改动。
- 调参入口 = 双击 Config 资产；加新参数先想好归哪个域、加进对应 Config 类，再给组件使用。

## 音频纪律（AudioManager：prefab 全局单例 + 场景切换驱动 BGM）

- AudioManager 做成 prefab（Assets/Prefabs/，拖齐 click/fire/crash/bgm + bgmSceneName），Menu 与 Main 场景各放一个该 prefab 的实例：先加载者经 DontDestroyOnLoad 成为全局，后加载者直接自毁——两实例引用同源（同一 prefab），无交接逻辑；任一场景单独 Play（编辑器直进 Main 调试）都有声音。
- BGM 由 `SceneManager.activeSceneChanged` 驱动：切到场景名 == bgmSceneName 就循环播，否则停。暂停菜单（timeScale=0）不暂停 BGM（AudioSource 不受 timeScale 影响）。
- 两条音量通道独立（暂停面板 BGM/SFX 两个 Slider），PlayerPrefs 持久化（audio.bgmVolume / audio.sfxVolume，0-1）。
- 静态播放 API 一律判空（AudioManager.Instance == null 时静默跳过），无 AudioManager 的场景（如测试）不炸。
- UI 点击音：Menu(UGUI) 由 AudioManager 启动时遍历 Button 动态挂；Main(UI Toolkit) 由 PauseController.OnUIReload 注册 root 捕获阶段 ClickEvent（只对 Button 响，Slider 拖动不响）+ 绑定音量 Slider。PauseController 是所有 UI Toolkit 面板重载回调的唯一收口，AudioManager 不再注册 PanelRenderer。

## 迷宫分层纪律（决策/执行分离）

- 决策层（`MazeData`）纯数据 + 纯函数，不碰场景、不依赖时间；`Generate(cols, rows, seed, loopMin, loopMax)` 同 seed 必得同一张图（含补开洞）。
- 执行层（`MapSpawner`）只负责把决策层数据实例化成 GameObject。
- 决策层改动用 `MazeDataTest` 的 Console 打印（ToAscii）验收，不必进 Play。
- 迷宫/网格时序：`GridMap.Awake`(0) 先注册 Instance → `MapSpawner`(ExecutionOrder 10) 建墙并 `SetBounds` → GridMap 每局扫描范围跟随迷宫实际尺寸。

## 场景纪律

- 助手不直接修改 `.unity` 场景文件；场景接线（挂组件/拖引用/摆物体）一律由用户在 Unity 编辑器完成，助手只给操作步骤。
- 运行时按需生成的对象（墙/地板）由脚本在 Awake 创建，不进场景序列化。

## 验证流程

- 改完必须验证：决策层 → MazeDataTest Print；玩法 → 回 Unity Play 跑一局看 Console/行为。
- 迷宫可调参数（格数区间/seed/补开洞比例区间）都在 `MapSpawner` Inspector，改了直接生效。
- 相机是固定全图正交（FixedMapCamera），每局自动按 GridMap 实际范围取景，参数在 Main Camera 的组件上。
