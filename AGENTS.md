塔科夫战术地图指挥平台 — 开发文档

1\. 项目概述

一个供小队内部使用的战术地图白板工具。玩家手动上传地图图片，在图片上放置语义图标、文本、箭头、线、圆圈、方框等标记，所有标记通过 TCP 在房主与队友之间可靠同步。



明确不做的：



不读取游戏内存、日志、网络封包，不注入进程；



不做玩家实时位置追踪（无高频数据流）；



不做 3D、物理、动画状态机；



不做自动房间发现（手动输入 IP + 端口）；



不引入图标库之外的贴图或自定义图案上传。



运行环境：Windows 桌面。队友之间通过第三方虚拟局域网工具（ZeroTier / Tailscale / Radmin VPN 等）互通，应用层直接 TCP 连接房主的虚拟局域网 IP。



2\. 技术栈

层	选型

语言 / 运行时	C# / .NET 8（或更高）

UI 框架	WPF

2D 渲染	SkiaSharp（SkiaSharp.Views.WPF 的 SKElement）

网络	System.Net.Sockets 的 TcpListener / TcpClient + async/await

序列化	MessagePack-CSharp（MessagePack NuGet 包）

MVVM（可选）	CommunityToolkit.Mvvm

目标平台	Windows x64

不使用 Unity、ENet、任何游戏引擎或 P2P 打洞库。



3\. 架构

房主权威（Listen Server）模式：



text

队友A ──TCP──┐

队友B ──TCP──┼──> 房主（TcpListener，监听 0.0.0.0:端口）

队友C ──TCP──┘

房主：启动时创建 TcpListener，绑定 0.0.0.0:端口，维护一份权威标记状态表和在线玩家列表。房主自己也是玩家，本地操作直接改状态并广播。



客户端：输入房主虚拟局域网 IP + 端口 + 房间口令，TCP 连接。



所有标记变更：客户端发给房主 → 房主校验权限和合法性 → 房主广播给所有客户端（包括发起者）→ 各端以房主广播为准更新本地状态。



状态冲突：房主串行处理请求，天然无并发冲突，不需要 CRDT 或锁协议。



4\. 数据模型

4.1 地图元数据 MapInfo

csharp

public class MapInfo

{

&#x20;   public string MapId { get; set; }       // GUID，房主创建时生成

&#x20;   public string ImageHash { get; set; }   // 原图 SHA-256，小写 hex

&#x20;   public int ImageWidth { get; set; }     // 原图像素宽

&#x20;   public int ImageHeight { get; set; }    // 原图像素高

&#x20;   public int GridCols { get; set; }       // 网格列数，默认 8

&#x20;   public int GridRows { get; set; }       // 网格行数，默认 6

&#x20;   public bool GridVisible { get; set; }   // 网格是否显示，默认 true

}

网格标签规则（固定）：列从左到右 A, B, C...，行从上到下 1, 2, 3...。网格只是视觉参考和辅助显示，不参与存储；标记坐标永远存归一化值。



4.2 标记 Marker

csharp

public enum MarkerType

{

&#x20;   Point = 0,      // 语义图标（配合 IconId）或默认圆点

&#x20;   Text = 1,       // 文本

&#x20;   Arrow = 2,      // 箭头，可拉长、可变换方向

&#x20;   Line = 3,       // 直线段

&#x20;   Circle = 4,     // 圆，圆心 + 半径，可自由缩放

&#x20;   Rectangle = 5   // 方框，左上角 + 右下角，可自由变换宽高

}



public class Marker

{

&#x20;   public string Id { get; set; }          // GUID，创建端生成

&#x20;   public MarkerType Type { get; set; }

&#x20;   public float X { get; set; }            // 0\~1，相对图片宽度

&#x20;   public float Y { get; set; }            // 0\~1，相对图片高度

&#x20;   public float Size { get; set; }         // 归一化大小/线宽，相对图宽

&#x20;   public string Color { get; set; }       // "#RRGGBB" 或 "#AARRGGBB"

&#x20;   public float Rotation { get; set; }     // 0\~360 度，顺时针

&#x20;   public string Text { get; set; }        // 仅 Type=Text 使用

&#x20;   public float EndX { get; set; }         // 仅 Type=Line/Rectangle 使用

&#x20;   public float EndY { get; set; }         // 仅 Type=Line/Rectangle 使用

&#x20;   public string IconId { get; set; }      // 仅 Type=Point 使用，null 表示默认圆点

&#x20;   public string OwnerId { get; set; }     // 创建者玩家 ID

&#x20;   public long CreatedAt { get; set; }     // Unix 毫秒，用于排序

}

各字段与类型的对应关系：



Type	X/Y 语义	Size 语义	EndX/EndY	Rotation	IconId

Point	图标中心	图标外接宽度	忽略	图标旋转	语义图标，null 为圆点

Text	文本左上角	字号	忽略	文本旋转	忽略

Arrow	箭尾	箭头长度	忽略	方向角	忽略

Line	起点	线宽	终点	忽略	忽略

Circle	圆心	半径	忽略	忽略	忽略

Rectangle	左上角	线宽（描边）	右下角	忽略	忽略

坐标约定：所有坐标归一化到 0\~1，Size 相对图宽，渲染时统一乘以当前图片显示宽度。圆圈是正圆，半径由 Size 决定；方框的宽高由 (X,Y) 和 (EndX,EndY) 共同决定，拖拽右下角即可自由变换。



屏幕坐标换算：



text

屏幕X = 图片屏幕矩形.Left + X \* 图片屏幕矩形.Width

屏幕Y = 图片屏幕矩形.Top  + Y \* 图片屏幕矩形.Height

反向变换用于点击放置和拖动。



4.3 内置图标集

IconId 的取值固定为以下五种，资源随客户端打包，不走网络传输：



IconId	语义	图标说明	工具栏标签

"kill"	击杀	骷髅或交叉准星	击杀

"scout"	踩点 / 侦察	眼睛或望远镜	踩点

"document"	收集文件	纸张 / 文件夹	文件

"key"	需要钥匙	钥匙轮廓	钥匙

"target"	标记目标	靶心或十字准星	目标

图标以单色 SVG path 或白色 PNG 蒙版形式内置，渲染时用 Color 着色。同一图标可任意改色，不需要为每种颜色准备资源。



IconId 为 null 或未识别的值时，回退为默认圆点绘制，保证向后兼容。



4.4 玩家信息 PlayerInfo

csharp

public class PlayerInfo

{

&#x20;   public string PlayerId { get; set; }

&#x20;   public string PlayerName { get; set; }

&#x20;   public bool IsHost { get; set; }

}

5\. 网络协议

5.1 帧格式

每条消息：



text

\[4 字节大端 int32：payload 长度 N]\[N 字节 payload]

payload = MessagePack 序列化的一个 Envelope 对象：



csharp

public class Envelope

{

&#x20;   public MessageType Type { get; set; }

&#x20;   public byte\[] Data { get; set; }   // 具体消息体的 MessagePack 字节

}

顶层用 Envelope 包一层，便于统一分发和版本演进。消息体本身也是 MessagePack 对象。



单帧 payload 上限 8MB，超过视为协议错误并断开连接。地图图片必须走分块传输，不塞进单帧。



5.2 消息表（完整最小集）

方向	类型	消息体	说明

C→S	HELLO	{ ProtocolVersion:int, RoomPasswordHash:string, PlayerName:string }	连接后第一条消息，口令用 SHA-256 hex

S→C	WELCOME	{ PlayerId:string, MapInfo:MapInfo, Markers:Marker\[], Players:PlayerInfo\[] }	认证成功，含全量快照

S→C	ERROR	{ Code:int, Message:string }	认证失败/版本不匹配，随后房主断开连接

C→S	MAP\_REQUEST	{ ImageHash:string }	请求地图图片

S→C	MAP\_CHUNK	{ ImageHash:string, Index:int, Total:int, Data:byte\[] }	图片分块，每块 64KB

C→S	MARKER\_ADD	Marker	请求新增

C→S	MARKER\_UPDATE	{ Id:string, Marker:Marker }	请求全量更新（简单可靠，不做字段级 diff）

C→S	MARKER\_DELETE	{ Id:string }	请求删除

S→C	MARKER\_ADDED	Marker	权威广播

S→C	MARKER\_UPDATED	Marker	权威广播（全量对象）

S→C	MARKER\_DELETED	{ Id:string }	权威广播

S→C	PLAYER\_JOINED	PlayerInfo	在线列表变更

S→C	PLAYER\_LEFT	{ PlayerId:string }	在线列表变更

双向	PING	{}	心跳，每 5 秒

双向	PONG	{}	心跳响应

5.3 可靠性机制

传输：TCP 本身保证有序、可靠、不重复。应用层只需处理帧边界。



幂等：MARKER\_ADD 若房主已存在同 Id 则忽略；MARKER\_DELETE 若不存在则忽略。重发安全。



权限：客户端只能 UPDATE / DELETE 自己 OwnerId 的标记；房主可操作全部。违规请求房主回 ERROR 并忽略该请求。



心跳：每 5 秒发 PING，30 秒未收到 PONG 视为断线，主动断开并触发重连。



重连：客户端指数退避重连（1s、2s、4s... 上限 30s）。重连成功后重新走 HELLO → WELCOME，客户端清空本地标记，用全量快照重建。地图图片若 ImageHash 相同则复用本地缓存，不发 MAP\_REQUEST。



认证：HELLO.RoomPasswordHash 与房主本地口令的 SHA-256 比对，失败回 ERROR 并断开。虚拟局域网内的轻量认证，不上 TLS。



版本：HELLO.ProtocolVersion 与房主期望值不一致时回 ERROR，提示客户端版本不匹配。



5.4 地图图片分发

房主选择图片时计算 SHA-256，得到 ImageHash，填入 MapInfo。



客户端收到 WELCOME 后，检查本地缓存目录 %LocalAppData%/TarkovTacticalMap/cache/{imageHash}.img：



存在且校验通过：直接加载。



不存在：发 MAP\_REQUEST。



房主按 64KB 分块发 MAP\_CHUNK，客户端按 Index 拼装，全部收齐后校验 SHA-256，通过则写缓存并加载，失败则重新请求。



缓存按哈希命名，天然去重。缓存目录不做自动清理，提供设置里的“清空缓存”按钮即可。



6\. 渲染与交互

6.1 画布分层（SkiaSharp 绘制顺序）

底图：用户上传的图片，按当前缩放/平移绘制。



网格：按 GridCols / GridRows 等分绘制半透明线；边缘绘制 A/B/C... 和 1/2/3... 标签。



标记：按 CreatedAt 排序绘制，保证新标记在上层。



选中高亮：选中标记绘制外框和控制点。



临时绘制层：正在拖拽中的箭头/线/圆圈/方框预览。



6.2 各类型渲染规则

Point：



IconId == null：实心圆，半径 Size × 图片显示宽度，填充 Color。



IconId != null：取内置图标，缩放到宽度 Size × 图片显示宽度，以 (X,Y) 为中心，按 Rotation 旋转，用 Color 着色。



Text：以 (X,Y) 为左上角绘制 Text，字号 Size × 图片显示宽度，按 Rotation 旋转，Color 为字色。



Arrow：从 (X,Y) 沿 Rotation 方向绘制长度 Size × 图片显示宽度 的箭头，带三角箭头头部，Color 为填充色。拖拽时实时更新长度和方向。



Line：从 (X,Y) 到 (EndX,EndY) 绘制线段，线宽 Size × 图片显示宽度，Color 为线色。



Circle：以 (X,Y) 为圆心，半径 Size × 图片显示宽度，绘制 Color 描边圆（线宽固定 2px 或按 Size 的 10% 取较小值）。



Rectangle：以 (X,Y) 和 (EndX,EndY) 为对角绘制矩形，Color 描边，线宽 Size × 图片显示宽度。



颜色统一规则：所有类型的 Color 都直接用于填充或描边，不做二次映射。支持 #RRGGBB 和 #AARRGGBB，透明度用于让底层地图可见。



6.3 交互操作

操作	行为

鼠标滚轮	以鼠标位置为中心缩放，0.2x \~ 10x

中键拖拽 / 空格+左键拖拽	平移画布

左侧工具栏选择图标 + 左键点击	放置对应 IconId 的 Point 标记，默认 Size=0.02

箭头：按下拖拽释放	起点为按下位置，长度和 Rotation 由释放位置计算

圆圈：按下拖拽释放	按下位置为圆心，释放位置到圆心的距离为半径

方框：按下拖拽释放	按下位置为左上角，释放位置为右下角

线：按下拖拽释放	按下位置为起点，释放位置为终点

文本：点击	放置文本标记并弹出输入框

左键点击已有标记	选中，右侧属性面板显示颜色、大小、旋转、文本、删除

选中后拖拽	移动标记；Circle 拖拽圆心，Rectangle 拖拽整体，Arrow/Line 拖拽端点可改长度/方向

选中后拖拽控制点	Circle 拖拽半径控制点改大小；Rectangle 拖拽右下角控制点改宽高；Arrow 拖拽箭头头部改长度和方向

Delete 键	删除选中标记（需有权限）

双击文本标记	编辑文本内容

控制点交互：选中 Circle / Rectangle / Arrow 时，在关键位置绘制小方块控制点，拖拽控制点直接修改 Size / EndX/EndY / Rotation。这样“自由变化”不需要进属性面板输入数字。



控制点命中检测：控制点半径建议为 6–8 屏幕像素（不随缩放变化），命中检测在屏幕坐标系做，避免缩放后难以点中。



6.4 网格引用显示

鼠标悬停时，状态栏显示当前网格引用，例如 C3：



text

col = clamp(floor(mouseX / imageWidth \* GridCols), 0, GridCols - 1)

row = clamp(floor(mouseY / imageHeight \* GridRows), 0, GridRows - 1)

label = (char)('A' + col) + (row + 1).ToString()

6.5 左侧工具栏

工具栏按语义分组，每组一个按钮，选中后左键放置：



text

\[选择] | \[击杀] \[踩点] \[文件] \[钥匙] \[目标] | \[箭头] \[圆圈] \[方框] \[线] \[文本] | \[颜色] \[大小]

前五个按钮对应 Type=Point + 对应 IconId，图标直接显示在按钮上。



箭头 / 圆圈 / 方框 / 线 / 文本分别对应其余 MarkerType。



颜色选择器对所有类型生效；大小滑块调整默认 Size，选中标记时调整该标记的 Size。



6.6 主界面布局

text

┌─────────────────────────────────────────────────┐

│ 菜单栏：文件 | 房间 | 视图 | 帮助                    │

├──────┬──────────────────────────────────┬───────┤

│ 左侧 │                                  │ 右侧  │

│ 工具栏│          SkiaSharp 画布           │ 属性  │

│      │                                  │ 面板  │

│      │                                  │       │

├──────┴──────────────────────────────────┴───────┤

│ 状态栏：连接状态 | 当前网格 | 在线玩家 | 鼠标坐标   │

└─────────────────────────────────────────────────┘

左侧工具栏：选择、五种语义图标、箭头、圆圈、方框、线、文本、颜色、大小。



右侧属性面板：选中标记的详细属性编辑（颜色、大小、旋转、文本内容、删除）。



菜单：



文件：新建房间（房主）、加入房间（客户端）、上传地图图片、清空缓存、退出。



房间：玩家列表、断开连接。



视图：显示/隐藏网格、设置网格行列数、重置视图。



帮助：关于。



6.7 房主 / 客户端模式

启动后首页两个按钮：



创建房间：输入端口（默认 27015）、房间口令、玩家名。选择本地图片或使用默认空白网格。点击创建后显示本机虚拟局域网 IP 列表（遍历网卡，过滤回环），方便队友输入。



加入房间：输入房主 IP、端口、房间口令、玩家名。连接成功后等待 WELCOME。



7\. 项目结构

text

TarkovTacticalMap/

├── TarkovTacticalMap.sln

├── AGENTS.md                        # 本文件

├── src/

│   ├── TarkovTacticalMap.Core/      # 数据模型、协议定义、序列化，无 UI 依赖

│   │   ├── Models/                  # MapInfo, Marker, PlayerInfo, MarkerType

│   │   ├── Protocol/                # Envelope, MessageType, 各消息体

│   │   └── Serialization/           # MessagePack 配置

│   ├── TarkovTacticalMap.Network/   # TCP 服务端与客户端，无 UI 依赖

│   │   ├── HostServer.cs            # TcpListener，房间状态，广播

│   │   ├── GameClient.cs            # TcpClient，重连，消息分发

│   │   ├── FrameCodec.cs            # 长度前缀帧编解码

│   │   └── ImageTransfer.cs         # 地图图片分块收发

│   └── TarkovTacticalMap.App/       # WPF 应用

│       ├── Assets/Icons/            # 内置图标资源（svg 或 png）

│       ├── Views/                   # 窗口与对话框

│       ├── ViewModels/              # MVVM

│       ├── Rendering/               # SkiaSharp 绘制逻辑

│       └── Services/                # 本地缓存、设置持久化

└── tests/

&#x20;   └── TarkovTacticalMap.Tests/     # 协议编解码、坐标变换、状态机单测

分层约束：



Core 不引用任何 UI 或网络库。



Network 只引用 Core，不引用 UI。



App 引用 Core 和 Network。



网络层通过事件（MarkerAdded, MarkerUpdated, MarkerDeleted, PlayerJoined, PlayerLeft, Disconnected）通知上层，UI 层订阅并更新 ViewModel。



8\. 开发里程碑

每个里程碑完成后跑通验收标准，再进入下一个。



M1：地图渲染与本地标记

WPF 窗口 + SkiaSharp 画布。



加载本地图片，滚轮缩放、中键平移。



绘制网格与 A/B/C、1/2/3 标签。



实现六种标记类型（含五种内置图标）的放置、选中、拖动、编辑、删除，全部本地状态。



箭头/线/圆圈/方框的拖拽创建与控制点调整。



验收：不连网的情况下，所有标记操作正确，缩放平移后标记位置不偏移，图标着色正确。



M2：TCP 骨架与全量同步

TcpListener 房主 + TcpClient 客户端。



HELLO / WELCOME / ERROR 流程。



新客户端加入后拿到 MapInfo + 全量 Marker\[] 并正确渲染。



验收：房主本机开两个实例，客户端加入后看到与房主完全一致的标记（含图标、颜色、大小、方向）。



M3：增量广播

MARKER\_ADD/UPDATE/DELETE 经房主校验后广播。



权限校验：客户端只能改自己的标记。



幂等处理。



验收：三端同时在线，任意一端增删改，所有端实时一致；客户端尝试修改他人标记被拒。



M4：地图图片分发

SHA-256 计算、本地缓存、MAP\_REQUEST / MAP\_CHUNK 分块传输。



缓存命中时跳过传输。



验收：新客户端加入时自动获取地图图片并加载；第二次加入直接命中缓存。



M5：健壮性

心跳 PING/PONG、断线检测、指数退避重连。



重连后清空本地标记并用全量快照重建。



口令认证失败处理、协议版本不匹配处理。



未知 IconId 或未知 MarkerType 的安全回退。



房主关闭时客户端收到断开提示。



验收：手动断开网络再恢复，客户端自动重连并恢复一致状态；错误口令被拒绝；发送未知类型不导致崩溃。



M6：打磨

虚拟局域网 IP 列表展示。



设置持久化（上次端口、玩家名、缓存目录）。



清空缓存按钮。



状态栏信息完善。



验收：整体流程顺畅，无阻塞 UI 的耗时操作（网络操作全部异步）。



9\. 关键实现约定

所有网络 I/O 用 async/await，不阻塞 UI 线程。读循环用 Task.Run 或纯异步 ReadAsync，回调通过 Dispatcher.Invoke 回到 UI 线程更新 ViewModel。



帧长度上限 8MB，超过视为协议错误并断开连接。地图图片必须走分块传输，不塞进单帧。



MessagePack 配置：使用 MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolver.Instance)，模型类加 \[MessagePackObject] 和 \[Key(n)] 特性，保证跨版本兼容。新增字段追加 \[Key(n)] 编号，不复用旧编号。



坐标归一化：所有存储和网络传输的坐标都是 0\~1 的 float，只有渲染时才转像素。防止不同窗口尺寸导致标记错位。



Size 归一化：同样相对图宽，渲染时乘以当前显示宽度。不要存像素值。



房主监听 0.0.0.0，不是 127.0.0.1，确保虚拟网卡上的队友能连进来。



防火墙提示：首次监听端口时 Windows 可能弹窗，文档不要求自动处理，但 README 中提示房主放行 TCP 入站。



图片缓存路径：%LocalAppData%/TarkovTacticalMap/cache/，按 {imageHash}.img 命名。



不引入额外依赖：除了 SkiaSharp、SkiaSharp.Views.WPF、MessagePack、CommunityToolkit.Mvvm（可选），不引入其他大型框架。



UI 语言：界面文本用简体中文，代码注释用中文或英文均可。



图标资源内置：五种 IconId 的 SVG path 或 PNG 蒙版作为嵌入资源打包，路径固定为 Assets/Icons/{iconId}.svg 或 .png，渲染层按 IconId 查找。不通过网络传输图标，协议只传字符串。



图标着色：优先使用 SVG path + SKPaint.Color 直接着色；若用 PNG 蒙版，使用 SKColorFilter.CreateBlendMode(color, SKBlendMode.SrcIn) 染色。两种方式选一种统一实现，不要混用。



未知 IconId 回退：收到不认识的 IconId 时按 null 处理，绘制默认圆点，不报错、不断连，保证新旧客户端混用时的兼容性。



未知 MarkerType 回退：收到不认识的 MarkerType 时跳过渲染或绘制默认圆点，不崩溃；编辑操作忽略该标记。



控制点命中检测：在屏幕坐标系做，控制点半径 6–8 屏幕像素，不随缩放变化。



协议版本：HELLO.ProtocolVersion 随数据模型变更递增，房主据此提示客户端版本不一致。



10\. 验收总标准

房主创建房间后，队友在虚拟局域网内输入 IP + 端口 + 口令即可加入。



任意玩家在地图上放置、移动、修改、删除六种类型的标记，所有在线玩家在 1 秒内看到一致结果。



五种语义图标（击杀、踩点、文件、钥匙、目标）正确显示且可改色。



箭头可自由拉长和变换方向；圆圈可自由缩放；方框可自由变换宽高。



缩放、平移、窗口大小变化后，所有标记位置、大小、方向保持正确。



断网重连后，状态自动恢复一致，无需手动重画。



地图图片只需传输一次，后续加入命中本地缓存。



全程不读取任何游戏数据，所有标记完全由玩家手动放置。



程序打包为独立可执行文件，队友无需安装额外运行时（自包含发布）。



11\. 给 Harness 的执行提示

按里程碑顺序推进，每个里程碑完成后先自测验收标准，再进入下一个。



遇到需求歧义时，优先按本文档的显式约定实现；文档未覆盖的细节选择最简方案，并在完成后列出“需要确认的假设”。



网络层和渲染层必须解耦，便于单独测试。



提交代码时保持 Core / Network / App 分层不被破坏。



不要引入本文档未列出的第三方库。



图标资源如果暂时找不到合适的 SVG，先放占位路径（简单几何图形），保证 IconId 机制跑通，后续替换资源不影响协议和渲染逻辑。



每个里程碑结束后运行 dotnet build 和单元测试，确保无编译错误和回归。

