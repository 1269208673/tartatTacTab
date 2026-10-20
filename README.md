# 塔塔战术平板 · Tarkov Tactical Map
本软件由DeepSeekHarness协助开发QAQ
小队内部使用的**战术地图白板**。玩家手动上传地图图片，在图上放置语义图标、文本、箭头、直线、圆圈、方框，
所有标记通过 TCP 在房主与队友之间可靠同步；房主是唯一权威，冲突串行处理，不需要 CRDT。

> 需求与设计依据：仓库根目录的 [AGENTS.md](AGENTS.md)。
<img width="200" height="300" alt="IMG_2101" src="https://github.com/user-attachments/assets/fb7a36d6-50c9-47dc-bbac-0a7db6059c90" />
自愿赞助收款码↑↑↑


## 快速开始

### 1. 构建与测试

```powershell
dotnet build TarkovTacticalMap.sln          # 编译全部四个项目
dotnet test  TarkovTacticalMap.sln          # 运行 100 个单元 / 集成 / UI 冒烟测试
```

### 2. 直接运行（开发）

```powershell
dotnet run --project src/TarkovTacticalMap.App
```

命令行快捷入口：

```powershell
dotnet run --project src/TarkovTacticalMap.App -- --offline   # 跳过首页，直接进入本地模式（不联网）
```

### 3. 自包含发布（队友无需安装运行时）

```powershell
& .\scripts\publish.ps1                    # 生成 publish/TarkovTacticalMap.exe（单文件、自包含、约 67 MB）
& .\scripts\publish.ps1 -Output dist       # 指定输出目录
& .\scripts\publish.ps1 -Version 2.2.0     # 指定版本号（写入 exe 文件版本 / 关于对话框）
& .\scripts\publish.ps1 -FrameworkDependent # 框架依赖（体积小，需装 .NET 8 桌面运行时）
```

等价的 dotnet 命令：

```powershell
dotnet publish src/TarkovTacticalMap.App/TarkovTacticalMap.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish
```

### 3.1 每个版本发布为 GitHub Release

打一个 `v*` 的 tag 即可，`.github/workflows/release.yml` 会自动：

1. Release 编译 + 无头测试
2. 生成 win-x64 自包含单文件 exe（版本号取自 tag）
3. 压缩成 `TarkovTacticalMap-vX.Y.Z-win-x64.zip`
4. 创建 GitHub Release，上传 exe 与 zip，并附带自动生成的变更说明

```powershell
# 方式一：本地打 tag 并推送（推荐，CI 完成发布）
git tag v2.2.0
git push origin v2.2.0

# 方式二：用脚本（可选 -Push 推送、-Gh 直接用 gh CLI 创建 Release）
& .\scripts\release.ps1 -Version 2.2.0            # 本地构建 zip + 打 tag
& .\scripts\release.ps1 -Version 2.2.0 -Push      # 打 tag 并推送，触发 CI 发布
& .\scripts\release.ps1 -Version 2.2.0 -LocalOnly # 只在本地构建，不打 tag

# 方式三：在 GitHub Actions 页面手动 Run workflow，填版本号
```

> 仓库需要先配置 `origin`（`git remote add origin <url>`）并有推送权限；Release 由 `GITHUB_TOKEN` 创建，无需额外密钥。

### 4. 防火墙 / 组网注意

- 房主监听 `0.0.0.0:端口`（默认 `27015`），**不是** `127.0.0.1`，队友才能从虚拟网卡连进来。
- 首次监听端口时 Windows 可能弹出防火墙询问，请允许 **TCP 入站**；也可手动为程序放行入站规则。
- 房主首页会列出本机所有可用 IPv4 地址（虚拟网卡会被优先排序），队友填其中之一即可。
- 口令错误的连接会被立即拒绝；客户端协议版本不一致会被提示「版本不匹配」。

---

## 使用说明

### 首页

| 区域 | 说明 |
| --- | --- |
| 创建房间（房主） | 玩家名、监听端口、房间口令、地图图片（可选「使用空白网格」）、网格密度 |
| 加入房间（客户端） | 玩家名、房主 IP、端口、房间口令 |
| 仅本机模式 | 不联网，只在本机做标记（单人使用 / 自测） |

### 主界面

```
┌──────────────────────────────────────────────────────┐
│ 菜单栏：文件 | 编辑 | 房间 | 视图 | 帮助                │
├──────┬──────────────────────────────────┬────────────┤
│ 左侧 │                                  │ 右侧属性   │
│ 工具栏│        SkiaSharp 画布             │ 面板       │
├──────┴──────────────────────────────────┴────────────┤
│ 状态栏：连接状态 | 网格 D5:A3:E1:A1 | 在线玩家 | 坐标   │
└──────────────────────────────────────────────────────┘
```

左侧工具栏顺序：`选择` | `击杀 踩点 文件 钥匙 目标` | `箭头 圆圈 方框 直线 文本 画笔` | `颜色` `大小` `粗细`。
五种语义图标直接显示在按钮上，用 `Color` 着色，同一图标可任意改色。
`大小` 是几何尺寸（图标宽度 / 字号 / 箭头长度 / 半径），`粗细` 是描边线宽，两者独立。

### 交互

| 操作 | 行为 |
| --- | --- |
| 鼠标滚轮 | 以鼠标位置为中心缩放，0.2x ~ 10x |
| **右键拖拽** / 空格+左键拖拽 | 平移画布 |
| **中键点击** | 快速删除光标下最上层的标记（需权限；只删一个，不进入选中/拖拽状态） |
| 工具栏选图标 + 左键点击 | 放置该 IconId 的 Point 标记 |
| 箭头 / 圆圈 / 方框 / 直线 | 按下拖拽释放创建（方向、半径、宽高由释放点决定） |
| 画笔（自由笔画） | 按下拖动随意写画，松开提交；可按屏幕距离自动抽稀折线点 |
| 文本工具 + 左键点击 | 弹出输入框，确定后放置文本标记 |
| 左键点击标记 | 选中，右侧属性面板可改颜色 / 大小 / 粗细 / 旋转 / 文本 / 删除 |
| 拖动选中标记 | 移动；圆圈拖圆心，方框/直线/笔画整体移动 |
| 拖动控制点 | 箭头改长度与方向；圆圈改半径；方框改宽高；直线改终点 |
| 双击文本标记 | 编辑文本内容 |
| `Ctrl+Z` / `Ctrl+Y` | 撤销 / 重做本机编辑（新增、修改、删除；只记录自己的操作） |
| `Delete` | 删除选中标记（需权限） |
| `Esc` | 取消选中 / 放弃当前拖拽 |
| `Ctrl+0` | 重置视图 |

控制点命中检测在**屏幕坐标系**完成，控制点半径固定 7px，不随缩放变化。

**箭头不再是整体缩放**：杆粗与箭头头部只由 `粗细` 决定，拉长只增加长度，适合做远距离指示箭头。
**圆圈 / 直线 / 方框 / 笔画**的描边粗细同样由 `粗细` 单独控制（圆圈半径仍由 `大小` 控制）。

### 菜单

- **文件**：新建房间（房主）、加入房间（客户端）、上传地图图片、清空缓存、退出
- **编辑**：撤销（Ctrl+Z）、重做（Ctrl+Y）
- **房间**：玩家列表（含本机地址）、断开连接
- **视图**：显示/隐藏网格、网格密度设置、重置视图
- **帮助**：关于（版本、协议版本、缓存目录）

---

## 架构

房主权威（Listen Server）：

```
队友A ──TCP──┐
队友B ──TCP──┼──> 房主（TcpListener，监听 0.0.0.0:端口）
队友C ──TCP──┘
```

所有标记变更：客户端发给房主 → 房主校验权限与合法性 → 房主广播给所有客户端（含发起者）→ 各端以房主广播为准更新本地状态。
房主串行处理请求，天然无并发冲突。

```
TarkovTacticalMap/
├── TarkovTacticalMap.sln
├── AGENTS.md                     # 需求文档
├── README.md
├── scripts/publish.ps1           # 自包含发布脚本
├── src/
│   ├── TarkovTacticalMap.Core/   # 数据模型、协议、序列化、几何换算、权威状态表（无 UI / 网络依赖）
│   │   ├── Models/               # MapInfo, Marker, MarkerType, PlayerInfo
│   │   ├── Protocol/             # Envelope, MessageType, 各消息体, 错误码, 协议常量
│   │   ├── Serialization/        # MessagePack 配置
│   │   ├── Geometry/             # ViewportState（归一化↔屏幕）、GridReference、NestedGrid、MarkerGeometry
│   │   ├── State/                # MarkerStore（权威表 / 幂等 / 权限）、MarkerValidator
│   │   └── Utils/                # SHA-256、颜色、口令哈希
│   ├── TarkovTacticalMap.Network/ # FrameCodec、HostServer、GameClient、ImageTransfer（无 UI 依赖）
│   └── TarkovTacticalMap.App/     # WPF 应用
│       ├── Assets/Icons/          # 内置图标（kill/scout/document/key/target.svg，嵌入资源）
│       ├── Rendering/             # MapRenderer（SkiaSharp 绘制）、IconLibrary、图标转 WPF 位图
│       ├── Services/              # TacticalSession（会话门面）、ImageCache、AppSettings、网络地址枚举
│       ├── ViewModels/            # MainViewModel（MVVM + CommunityToolkit.Mvvm）
│       └── Views/                 # MainWindow、StartupWindow、TextInputDialog 等
└── tests/TarkovTacticalMap.Tests/ # 协议 / 几何 / 状态 / 分块 / 网络回环 / 渲染快照 / UI 冒烟
```

分层约束：`Core` 不引用 UI 与网络库；`Network` 只引用 `Core`；`App` 引用 `Core` + `Network`。
网络层通过事件（`MarkerAdded` / `MarkerUpdated` / `MarkerDeleted` / `PlayerJoined` / `PlayerLeft` / `Disconnected`）通知上层。

### 一次标记变更的完整路径

```
画布鼠标事件 → MainViewModel.CreatePrototype → TacticalSession.AddMarkerAsync
  ├─ 客户端：GameClient.SendAddAsync → 房主校验 → 广播 MARKER_ADDED → 客户端事件 → Dispatcher → ViewModel → 重绘
  ├─ 房主  ：HostServer.AddMarkerAsync → 本地权威表 + 广播（含自己）→ 事件 → ViewModel → 重绘
  └─ 本机  ：直接改本地权威表 → 事件 → ViewModel → 重绘
```

---

## 协议摘要

### 帧格式

```
[4 字节大端 int32：payload 长度 N][N 字节 payload]
payload = MessagePack 序列化的 Envelope { Type: MessageType, Data: byte[]（消息体的 MessagePack 字节） }
```

单帧上限 **8MB**，超过视为协议错误并断开。地图图片必须走分块传输。

### 消息表

| 方向 | 类型 | 消息体 |
| --- | --- | --- |
| C→S | `HELLO` | `{ ProtocolVersion, RoomPasswordHash, PlayerName }` |
| S→C | `WELCOME` | `{ PlayerId, MapInfo, Markers[], Players[] }` |
| S→C | `ERROR` | `{ Code, Message, RelatedType? }` |
| C→S | `MAP_REQUEST` | `{ ImageHash }` |
| S→C | `MAP_CHUNK` | `{ ImageHash, Index, Total, Data }`（每块 64KB） |
| C→S | `MARKER_ADD` | `Marker` |
| C→S | `MARKER_UPDATE` | `{ Id, Marker }` |
| C→S | `MARKER_DELETE` | `{ Id }` |
| S→C | `MARKER_ADDED` | `Marker` |
| S→C | `MARKER_UPDATED` | `Marker` |
| S→C | `MARKER_DELETED` | `{ Id }` |
| S→C | `PLAYER_JOINED` | `PlayerInfo` |
| S→C | `PLAYER_LEFT` | `{ PlayerId }` |
| 双向 | `PING` / `PONG` | `{}`，每 5 秒 |

错误码：`AuthFailed=1`、`ProtocolVersionMismatch=2`、`PermissionDenied=3`、`InvalidRequest=4`、`ProtocolError=5`、`MapUnavailable=6`、`ServerClosed=7`、`HandshakeTimeout=8`。

### 可靠性机制

- **幂等**：重复 `MARKER_ADD`（同 Id）忽略；删除不存在的 Id 忽略；更新不存在的 Id 回 `ERROR` 并忽略。
- **权限**：客户端只能 `UPDATE` / `DELETE` 自己 `OwnerId` 的标记；房主可操作全部（本地状态表按连接身份强制写入 `OwnerId`，客户端无法伪造）。
- **心跳**：每 5 秒 `PING`，30 秒无响应视为断线。
- **重连**：客户端指数退避（1s、2s、4s… 上限 30s）；重连成功后重走 `HELLO → WELCOME`，用全量快照重建本地状态。
- **认证**：`HELLO.RoomPasswordHash` 与房主本地口令的 SHA-256 比对，失败回 `ERROR` 并断开（虚拟局域网内的轻量认证，不上 TLS）。
- **版本**：`HELLO.ProtocolVersion` 与房主期望值不一致时回 `ERROR(ProtocolVersionMismatch)`。
- **地图分发**：房主选择图片时算 SHA-256 → 客户端先查 `%LocalAppData%/TarkovTacticalMap/cache/{imageHash}.img`，命中直接加载，未命中再分块请求；收齐后校验 SHA-256，通过才写缓存。

### 数据模型（要点）

- 所有坐标归一化到 `0~1`（相对图片宽高），`Size` / `Thickness` 相对图宽，渲染时才乘当前显示宽度。
- `Size` = 几何尺寸：图标宽度 / 字号 / 箭头长度 / 圆半径；`Thickness` = 描边粗细（箭头、圆、直线、方框、笔画）。
  `Thickness = 0` 时按旧的 Size 推导规则回退，保证旧数据渲染一致。
- 箭头长度只受 `Size` 影响，杆粗与箭头头部只受 `Thickness` 影响 —— 拉长不会整体放大。
- 圆圈是正圆（`Size` 为半径、`Thickness` 为描边）；方框由 `(X,Y)` 与 `(EndX,EndY)` 决定；箭头由 `(X,Y)` + `Rotation` + `Size` 决定，方向按屏幕角度（与窗口宽高比无关）。
- **自由笔画**（`MarkerType.Stroke`）：`Points` 为归一化折线点数组（最多 2000 点），`Thickness` 为线宽。
- **网格（只有一级）**：每边最多 10 格 —— 列 `A~J`、行 `0~9`。按图片宽高比自动推导行列数，非正方形图片会减少较短那边（例如 3:1 的宽图是 9×3），使格子保持接近正方形。网格只是视觉参考，**不参与存储**；标记大小 / 粗细用属性面板调整，不提供大小手柄。
- `IconId` 固定五种：`kill`（击杀）、`scout`（踩点）、`document`（文件）、`key`（钥匙）、`target`（目标）；未知值回退为默认圆点。
- 未知 `MarkerType` 安全回退为默认圆点，不崩溃、不断连。
- MessagePack：`[MessagePackObject]` + `[Key(n)]`，`ContractlessStandardResolver` 兜底；新增字段追加 `[Key(n)]`，不复用旧编号。当前 `ProtocolVersion = 2`（v2 新增 `Marker.Thickness`、`Marker.Points` 与 `MarkerType.Stroke`，v1 客户端会被房主提示版本不匹配）。

---

## 测试

```powershell
dotnet test TarkovTacticalMap.sln                 # 全部
dotnet test TarkovTacticalMap.sln --filter "FullyQualifiedName~NetworkIntegrationTests"
```

当前 **121 个测试**全部通过，覆盖：

| 测试类 | 覆盖内容 |
| --- | --- |
| `ProtocolFrameTests` | 帧格式（大端长度、4 字节头）、超长帧拒绝、截断帧报错、Envelope / Marker（含 Thickness / Points）/ Welcome / MapChunk 序列化往返 |
| `GeometryTests` | 适应窗口比例、居中矩形、归一化↔屏幕可逆、滚轮缩放锚点不动、缩放限幅、窗口尺寸变化后位置正确、网格标签、箭头角度/长度反算、**箭头杆粗与长度解耦**、笔画命中与包围盒、**大小控制点反算**、控制点与命中检测 |
| `GridLayoutTests` | 一级网格保持正方形格子（≤6% 误差）与每边 ≤10 格上限、宽图减行 / 高图减列、方图满密度、引用形如 `C3`（单级、不含冒号）、引用与格子矩形自洽、密度调整与限幅 |
| `MarkerStoreTests` | 新增/删除幂等、权限（客户端 vs 房主）、OwnerId 与 CreatedAt 不可篡改、CreatedAt 排序、快照为副本、规范化（坐标 clamp / Size、Thickness 限幅 / 旋转归一化 / 颜色 / 文本截断 / 笔画点数）、未知类型兼容、并发写入 |
| `ImageTransferTests` | 64KB 分块数量与序号、乱序拼装、SHA-256 校验失败、越界与跨图分块拒绝、哈希与口令工具、颜色工具 |
| `NetworkIntegrationTests` | 回环 TCP：WELCOME 快照、增删改广播、权限拒绝、重复新增幂等、地图分块获取、口令错误、协议版本不匹配、首帧必须 HELLO、玩家加入/离开、**断网后自动重连并重建**、房主关闭通知不再重连 |
| `SessionIntegrationTests` | App 会话门面：房主+客户端全流程（图片分发与缓存命中）、标记同步与权限、口令错误抛异常、离线模式、网格密度按比例推导、缓存读写/损坏/路径穿越防护 |
| `RenderSnapshotTests` | 真实 SkiaSharp 渲染到 `SKBitmap` 并断言关键像素（七种标记 + 五种图标 + 网格 + 选中控制点）、**长箭头像素更多但杆粗不变**、**圆圈粗细只影响描边**、**笔画折线渲染**、**网格只有一级且竖线间距 = 格宽**、未知 IconId / 未知 MarkerType 回退 |
| `IconShapeTests` | 图标栅格化成 ASCII 字符画（写入 `artifacts/renders/icons-ascii.txt`），断言靶心环、钥匙孔、折角等几何特征 |
| `UiSmokeTests` | STA 线程加载 `App.xaml`、构造并显示首页与主窗口、**断言无数据绑定错误**、**菜单弹层真实渲染 + 深底浅字像素统计（防「白底白字」回归）**、房主↔客户端跨线程同步（Dispatcher 封送）、中键快速删除、撤销/重做、自由笔画、粗细联动、网格引用、**关闭窗口不抛异常（回归测试）** |

渲染快照与字符画会写入 `artifacts/renders/`（`scene-all-markers.png`、`scene-nested-grid.png`、`marker-stroke.png`、`scene-grid-selection.png`、`icons-sheet.png`、`icons-ascii.txt`、`menu-popup.png`），便于人工核对。

### CI

`.github/workflows/build.yml` 在 push / PR 时执行：Release 编译 → 无头测试（跳过需要交互式桌面的 `UiSmokeTests`）→
win-x64 自包含单文件发布 → 上传 `TarkovTacticalMap.exe` 产物。

---

## 里程碑完成情况

| 里程碑 | 状态 | 实现要点 |
| --- | --- | --- |
| **M1** 地图渲染与本地标记 | ✅ | WPF + `SKElement`，滚轮缩放 / **右键拖拽平移**，网格与 A~J、0~9 标签，七种标记类型（含自由笔画）+ 五种内置图标，拖拽创建与控制点调整，**中键快速删除**，选中/移动/编辑/删除 |
| **M2** TCP 骨架与全量同步 | ✅ | `TcpListener` / `TcpClient`，`HELLO` / `WELCOME` / `ERROR`，新客户端拿到 `MapInfo` + 全量 `Marker[]` |
| **M3** 增量广播 | ✅ | `MARKER_ADD/UPDATE/DELETE` 经房主校验后广播，权限校验，幂等处理 |
| **M4** 地图图片分发 | ✅ | SHA-256、本地缓存、`MAP_REQUEST` / `MAP_CHUNK` 64KB 分块、缓存命中跳过传输 |
| **M5** 健壮性 | ✅ | PING/PONG 心跳、断线检测、指数退避重连、重连后用快照重建、口令与版本校验、未知 IconId/MarkerType 回退、房主关闭提示 |
| **M6** 打磨 | ✅ | 虚拟局域网 IP 列表、设置持久化、清空缓存、状态栏信息完善、网络操作全异步不阻塞 UI |

---

## 设置与数据位置

| 内容 | 路径 |
| --- | --- |
| 设置（玩家名、上次端口、上次房主 IP、默认颜色/大小） | `%LocalAppData%\TarkovTacticalMap\settings.json` |
| 地图图片缓存（按 SHA-256 命名，天然去重） | `%LocalAppData%\TarkovTacticalMap\cache\{imageHash}.img` |
| 渲染快照（仅测试产物） | `artifacts\renders\` |

缓存不做自动清理，可在「文件 → 清空缓存」手动清理。

---

## 需要确认的假设 / 与文档的差异

1. **首页多一个「仅本机模式」入口**：文档只要求「创建房间 / 加入房间」两个按钮。为了在无网络的情况下也能使用与自测（M1 验收），额外提供了不联网的本地模式。
2. **网格是本机视图设置（只有一级）**：文档没有定义「修改网格」的网络消息，因此密度设置只影响本机显示，不广播；`MapInfo.GridCols/GridRows` 随 `WELCOME` 下发为初始值（行列数按图片宽高比推导，保持正方形格子）。次级网格已按使用方要求**取消**，引用只有一级，如 `C3`。
2.1 **行标签从 0 开始（按使用方要求）**：文档 4.1/6.4 写的是行 `1,2,3…`，实际改为 `0~9`（配合每边 10 格的上限）；列仍为 `A,B,C…`，上限 `A~J`。若上游要求 1 起始，只需改 `GridReference.RowLabel`。
3. **房间创建后地图固定**：文档消息表里没有「更换地图」消息，所以换图只在「还没有队友加入」时允许（离线模式随时可换）。若需要房间中途换图，需要新增一条 `MAP_UPDATE` 消息并递增协议版本。
4. **拖拽过程中的实时性**：拖动标记时本地实时预览（草稿层），**松开鼠标时**才发送一次 `MARKER_UPDATE`。局域网内延迟通常 < 10ms；如果希望队友看到「实时拖动轨迹」，需要额外的节流广播策略。
5. **客户端不做乐观更新**：客户端的所有变更都等房主权威广播回显后再更新本地状态（严格遵守「各端以房主广播为准」）。因此客户端会有一个网络往返的显示延迟（局域网内可忽略）。
6. **图标为手写 SVG 占位几何图形**：五种内置图标是自绘的单色 SVG path（已着色验证），未使用第三方图标库。替换 `src/TarkovTacticalMap.App/Assets/Icons/*.svg` 中的 path 即可，不影响协议与渲染逻辑。
7. **口令明文只存在于本机**：`HELLO` 只传 SHA-256；口令不落盘（设置文件里不保存口令，每次进房需重新输入）。
8. **控制点容差**：控制点绘制半径 7px、命中容差 10px（文档建议 6–8px），略放宽以便触控板操作。
9. **重连后的 UI 提示**：重连成功会清空本地标记并用全量快照重建，状态栏提示「已重新连接房主，状态已用全量快照重建」。
10. **`--offline` 命令行参数**：非文档要求的便捷入口，便于快速自测。
11. **鼠标按键与文档不同（按使用方要求调整）**：文档 6.3 写的是「中键拖拽平移」，实际实现为 **右键拖拽平移**（更符合常见地图软件习惯，也避免与触控板中键冲突），并把 **中键改成快速删除光标下的标记**（等价于「左键选中 + Delete」，但一步完成）。空格+左键平移仍然保留。
12. **菜单弹层配色为自定义模板**：WPF 默认菜单弹层使用系统浅色画刷，与深色主题的浅色文字叠加会「白底白字」。已为 `MenuItem` 编写完整深色模板（含勾选标记、子菜单箭头、分隔线），并用自动化测试断言「深底浅字」（像素统计 + WCAG 对比度 ≥ 4.5）。
13. **协议升到 v2**：新增 `Marker.Thickness`（独立粗细）、`Marker.Points`（自由笔画折线）与 `MarkerType.Stroke`。旧版（v1）客户端连入会被房主以「版本不匹配」拒绝，属预期行为。
14. **关闭窗口的清理方式**：`OnWindowClosing` 不再 `async void`，也不在 Closing 期间调用 `Close()`（那会抛 `InvalidOperationException：窗口关闭期间无法调用 Show/Close`）。现在的做法是：`e.Cancel = true` → 后台线程清理会话 → 通过 `Dispatcher.BeginInvoke` 排到下一轮再真正关闭；并有回归测试与真实 `WM_CLOSE` 验证。
15. **箭头语义修正（按使用方要求）**：文档 4.2 规定 Arrow 的 `Size` 既是长度也隐含粗细。现在 `Size` 只表示长度，粗细由 `Thickness` 单独控制，所以**拉长箭头不会再整体放大**；圆圈/直线/方框同理，半径或端点与描边粗细互相独立。
16. **撤销/重做只覆盖本机操作**：只记录自己发起的新增/修改/删除（远端玩家的广播不会进历史），最多 200 步；1.2 秒内对同一标记的连续微调（拖滑杆、拖手柄）会合并为一条。会话切换/重连后历史清空。
17. **自由笔画点数上限 2000**：拖动时按屏幕距离 ≥2.2px 抽稀，避免单帧过大与历史爆内存。
18. **已取消「放置后拖手柄调整大小」**：图标点与文本不提供控制点（实际手感不好，按使用方要求去掉），大小 / 粗细统一在右侧属性面板或工具栏滑块调整。

## 已知限制

- 单房间（一个房主进程一个房间）；多房间需要启动多个实例并使用不同端口。
- 地图图片走内存 + 本地缓存，超大图片（> 数十 MB）分块时间较长，但仍是异步的、不阻塞 UI。
- 不做自动房间发现、不做 NAT 打洞；跨网段必须依赖虚拟局域网工具。
- 文本标记的命中判定与选中框使用估算包围盒（按字号 × 字数），超长文本可能有少量误差。
