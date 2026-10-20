# 塔塔战术平板 · Tarkov Tactical Map

小队内部使用的**战术地图白板**。玩家手动上传地图图片，在图上放置语义图标、文本、箭头、直线、圆圈、方框，
所有标记通过 TCP 在房主与队友之间可靠同步；房主是唯一权威，冲突串行处理，不需要 CRDT。

> 需求与设计依据：仓库根目录的 [AGENTS.md](AGENTS.md)。

## 明确不做

- 不读取游戏内存 / 日志 / 网络封包，不注入任何进程；
- 不做玩家实时位置追踪（无高频数据流）；
- 不做 3D、物理、动画状态机；
- 不做自动房间发现（手动输入 IP + 端口）；
- 除内置五种语义图标外，不引入图标库之外的贴图或自定义图案上传。

运行环境：Windows x64 桌面。队友之间先用 ZeroTier / Tailscale / Radmin VPN 等第三方虚拟局域网工具组网，
应用层直接 TCP 连接房主的虚拟局域网 IP。

---

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
& .\scripts\publish.ps1 -FrameworkDependent # 框架依赖（体积小，需装 .NET 8 桌面运行时）
```

等价的 dotnet 命令：

```powershell
dotnet publish src/TarkovTacticalMap.App/TarkovTacticalMap.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish
```

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
| 创建房间（房主） | 玩家名、监听端口、房间口令、地图图片（可选「使用空白网格」）、网格行列数 |
| 加入房间（客户端） | 玩家名、房主 IP、端口、房间口令 |
| 仅本机模式 | 不联网，只在本机做标记（单人使用 / 自测） |

### 主界面

```
┌──────────────────────────────────────────────────────┐
│ 菜单栏：文件 | 房间 | 视图 | 帮助                       │
├──────┬──────────────────────────────────┬────────────┤
│ 左侧 │                                  │ 右侧属性   │
│ 工具栏│        SkiaSharp 画布             │ 面板       │
├──────┴──────────────────────────────────┴────────────┤
│ 状态栏：连接状态 | 网格 C3 | 在线玩家 | 鼠标坐标 | 提示 │
└──────────────────────────────────────────────────────┘
```

左侧工具栏顺序：`选择` | `击杀 踩点 文件 钥匙 目标` | `箭头 圆圈 方框 直线 文本` | `颜色` `大小`。
五种语义图标直接显示在按钮上，用 `Color` 着色，同一图标可任意改色。

### 交互

| 操作 | 行为 |
| --- | --- |
| 鼠标滚轮 | 以鼠标位置为中心缩放，0.2x ~ 10x |
| 中键拖拽 / 空格+左键拖拽 | 平移画布 |
| 工具栏选图标 + 左键点击 | 放置该 IconId 的 Point 标记 |
| 箭头 / 圆圈 / 方框 / 直线 | 按下拖拽释放创建（方向、半径、宽高由释放点决定） |
| 文本工具 + 左键点击 | 弹出输入框，确定后放置文本标记 |
| 左键点击标记 | 选中，右侧属性面板可改颜色 / 大小 / 旋转 / 文本 / 删除 |
| 拖动选中标记 | 移动；圆圈拖圆心，方框/直线整体移动 |
| 拖动控制点 | 箭头改长度与方向；圆圈改半径；方框改宽高；直线改终点 |
| 双击文本标记 | 编辑文本内容 |
| `Delete` | 删除选中标记（需权限） |
| `Esc` | 取消选中 / 放弃当前拖拽 |
| `Ctrl+0` | 重置视图 |

控制点命中检测在**屏幕坐标系**完成，控制点半径固定 7px，不随缩放变化。

### 菜单

- **文件**：新建房间（房主）、加入房间（客户端）、上传地图图片、清空缓存、退出
- **房间**：玩家列表（含本机地址）、断开连接
- **视图**：显示/隐藏网格、设置网格行列数、重置视图
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
│   │   ├── Geometry/             # ViewportState（归一化↔屏幕）、GridReference、MarkerGeometry
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

- 所有坐标归一化到 `0~1`（相对图片宽高），`Size` 相对图宽（长度 / 半径 / 字号 / 线宽），渲染时才乘当前显示宽度。
- 圆圈是正圆（`Size` 为半径）；方框由 `(X,Y)` 与 `(EndX,EndY)` 决定；箭头由 `(X,Y)` + `Rotation` + `Size` 决定，方向按屏幕角度（与窗口宽高比无关）。
- 网格（默认 8×6）只是视觉参考，列 `A/B/C…`、行 `1/2/3…`，**不参与存储**。
- `IconId` 固定五种：`kill`（击杀）、`scout`（踩点）、`document`（文件）、`key`（钥匙）、`target`（目标）；未知值回退为默认圆点。
- 未知 `MarkerType` 安全回退为默认圆点，不崩溃、不断连。
- MessagePack：`[MessagePackObject]` + `[Key(n)]`，`ContractlessStandardResolver` 兜底；新增字段追加 `[Key(n)]`，不复用旧编号。当前 `ProtocolVersion = 1`。

---

## 测试

```powershell
dotnet test TarkovTacticalMap.sln                 # 全部
dotnet test TarkovTacticalMap.sln --filter "FullyQualifiedName~NetworkIntegrationTests"
```

当前 **100 个测试**全部通过，覆盖：

| 测试类 | 覆盖内容 |
| --- | --- |
| `ProtocolFrameTests` | 帧格式（大端长度、4 字节头）、超长帧拒绝、截断帧报错、Envelope / Marker / Welcome / MapChunk 序列化往返 |
| `GeometryTests` | 适应窗口比例、居中矩形、归一化↔屏幕可逆、滚轮缩放锚点不动、缩放限幅、窗口尺寸变化后位置正确、网格标签、箭头角度/长度反算、控制点与命中检测 |
| `MarkerStoreTests` | 新增/删除幂等、权限（客户端 vs 房主）、OwnerId 与 CreatedAt 不可篡改、CreatedAt 排序、快照为副本、规范化（坐标 clamp / Size 限幅 / 旋转归一化 / 颜色 / 文本截断）、未知类型兼容、并发写入 |
| `ImageTransferTests` | 64KB 分块数量与序号、乱序拼装、SHA-256 校验失败、越界与跨图分块拒绝、哈希与口令工具、颜色工具 |
| `NetworkIntegrationTests` | 回环 TCP：WELCOME 快照、增删改广播、权限拒绝、重复新增幂等、地图分块获取、口令错误、协议版本不匹配、首帧必须 HELLO、玩家加入/离开、**断网后自动重连并重建**、房主关闭通知不再重连 |
| `SessionIntegrationTests` | App 会话门面：房主+客户端全流程（图片分发与缓存命中）、标记同步与权限、口令错误抛异常、离线模式、缓存读写/损坏/路径穿越防护 |
| `RenderSnapshotTests` | 真实 SkiaSharp 渲染到 `SKBitmap` 并断言关键像素颜色（六种标记 + 五种图标 + 网格 + 选中控制点）、未知 IconId / 未知 MarkerType 回退 |
| `IconShapeTests` | 图标栅格化成 ASCII 字符画（写入 `artifacts/renders/icons-ascii.txt`），断言靶心环、钥匙孔、折角等几何特征 |
| `UiSmokeTests` | STA 线程加载 `App.xaml`、构造并显示首页与主窗口、**断言无数据绑定错误**、房主↔客户端在同一 UI 线程上的跨线程同步（Dispatcher 封送） |

渲染快照会写入 `artifacts/renders/`（`scene-all-markers.png`、`scene-grid-selection.png`、`icons-sheet.png`、`icons-ascii.txt`），便于人工核对。

### CI

`.github/workflows/build.yml` 在 push / PR 时执行：Release 编译 → 无头测试（跳过需要交互式桌面的 `UiSmokeTests`）→
win-x64 自包含单文件发布 → 上传 `TarkovTacticalMap.exe` 产物。

---

## 里程碑完成情况

| 里程碑 | 状态 | 实现要点 |
| --- | --- | --- |
| **M1** 地图渲染与本地标记 | ✅ | WPF + `SKElement`，滚轮缩放 / 中键平移，网格与 A/B/C、1/2/3 标签，六种标记类型 + 五种内置图标，拖拽创建与控制点调整，选中/移动/编辑/删除 |
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
2. **网格行列数是本机视图设置**：文档没有定义「修改网格」的网络消息，因此网格设置只影响本机显示，不广播；`MapInfo.GridCols/GridRows` 仍随 `WELCOME` 下发为初始值。
3. **房间创建后地图固定**：文档消息表里没有「更换地图」消息，所以换图只在「还没有队友加入」时允许（离线模式随时可换）。若需要房间中途换图，需要新增一条 `MAP_UPDATE` 消息并递增协议版本。
4. **拖拽过程中的实时性**：拖动标记时本地实时预览（草稿层），**松开鼠标时**才发送一次 `MARKER_UPDATE`。局域网内延迟通常 < 10ms；如果希望队友看到「实时拖动轨迹」，需要额外的节流广播策略。
5. **客户端不做乐观更新**：客户端的所有变更都等房主权威广播回显后再更新本地状态（严格遵守「各端以房主广播为准」）。因此客户端会有一个网络往返的显示延迟（局域网内可忽略）。
6. **图标为手写 SVG 占位几何图形**：五种内置图标是自绘的单色 SVG path（已着色验证），未使用第三方图标库。替换 `src/TarkovTacticalMap.App/Assets/Icons/*.svg` 中的 path 即可，不影响协议与渲染逻辑。
7. **口令明文只存在于本机**：`HELLO` 只传 SHA-256；口令不落盘（设置文件里不保存口令，每次进房需重新输入）。
8. **控制点容差**：控制点绘制半径 7px、命中容差 10px（文档建议 6–8px），略放宽以便触控板操作。
9. **重连后的 UI 提示**：重连成功会清空本地标记并用全量快照重建，状态栏提示「已重新连接房主，状态已用全量快照重建」。
10. **`--offline` 命令行参数**：非文档要求的便捷入口，便于快速自测。

## 已知限制

- 单房间（一个房主进程一个房间）；多房间需要启动多个实例并使用不同端口。
- 地图图片走内存 + 本地缓存，超大图片（> 数十 MB）分块时间较长，但仍是异步的、不阻塞 UI。
- 不做自动房间发现、不做 NAT 打洞；跨网段必须依赖虚拟局域网工具。
- 文本标记的命中判定与选中框使用估算包围盒（按字号 × 字数），超长文本可能有少量误差。
