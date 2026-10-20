using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TarkovTacticalMap.App.Rendering;
using TarkovTacticalMap.App.Services;
using TarkovTacticalMap.Core.Geometry;
using TarkovTacticalMap.Core.Models;
using TarkovTacticalMap.App.ViewModels;
using TarkovTacticalMap.App.Views;
using Xunit;
using Xunit.Abstractions;

namespace TarkovTacticalMap.Tests;

/// <summary>
/// WPF 冒烟测试：在 STA 线程上真正加载 App.xaml 资源、构造首页与主窗口、显示并布局，
/// 并捕获数据绑定错误。可以覆盖 XAML 资源键、转换器类型、SkiaSharp 控件命名空间等静态检查不到的坑。
/// </summary>
public class UiSmokeTests
{
    private readonly ITestOutputHelper _output;

    public UiSmokeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void WpfShell_LoadsAndBindsWithoutErrors()
    {
        Exception? failure = null;
        var bindingErrors = new List<string>();

        var thread = new Thread(() =>
        {
            try
            {
                var app = new TarkovTacticalMap.App.App();
                app.InitializeComponent(); // 加载 App.xaml 中的样式与转换器
                // 测试里手动控制生命周期，避免关闭窗口时触发 Application.Shutdown
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                PresentationTraceSources.Refresh();
                var listener = new CollectingTraceListener(bindingErrors);
                PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;

                var cache = TestHelpers.CreateTempCache();
                var settings = new AppSettings { PlayerName = "冒烟测试" };

                // 首页
                var startup = new StartupWindow(settings, cache);
                startup.Show();
                startup.UpdateLayout();
                Pump();
                Assert.False(string.IsNullOrWhiteSpace(startup.Title));
                startup.Hide();

                // 主窗口（本地模式会话）
                var session = TacticalSession.CreateOfflineAsync(new SessionConfig
                {
                    PlayerName = "冒烟测试",
                    ImageBytes = TestHelpers.CreateTestImage(),
                    ImageWidth = 320,
                    ImageHeight = 240,
                    GridDensity = 26,
                }, cache).GetAwaiter().GetResult();

                var viewModel = new MainViewModel(Dispatcher.CurrentDispatcher, settings, cache);
                var window = new MainWindow(viewModel, session, settings);
                window.Show();
                window.UpdateLayout();
                Pump(10);

                // 会话已接入视图模型
                Assert.NotNull(viewModel.Session);
                Assert.Equal(SessionMode.Offline, viewModel.Session!.Mode);
                // 选择 + 5 语义图标 + 箭头/圆圈/方框/线/文本/画笔
                Assert.Equal(12, viewModel.Tools.Count);
                Assert.Contains(viewModel.Tools, t => t.Kind == ToolKind.Freehand && t.Label == "画笔");
                Assert.Equal(8, viewModel.Swatches.Count);

                // 视图模型交互路径
                viewModel.SelectTool(ToolKind.Circle);
                Assert.Equal(ToolKind.Circle, viewModel.ActiveTool);
                viewModel.SetColor("#FF5252");
                Assert.Equal("#FFFF5252", viewModel.CurrentColor);

                var prototype = viewModel.CreatePrototype(ToolKind.Kill, new Core.Geometry.MapPoint(0.4f, 0.4f));
                viewModel.AddMarkerAsync(prototype).GetAwaiter().GetResult();
                Pump();
                Assert.Single(viewModel.Markers);

                viewModel.SelectMarker(prototype.Id);
                Assert.True(viewModel.HasSelection);
                Assert.True(viewModel.CanEditSelection);
                Assert.Equal("图标点 · 击杀", viewModel.SelectionTypeText);

                viewModel.NudgeRotation(30f);
                Pump();
                Assert.Equal(30f, viewModel.Markers[0].Rotation, 3);

                // 菜单弹层配色：必须是深底浅字（回归测试「白底白字」）
                var menu = window.FindName("MainMenu") as Menu;
                Assert.NotNull(menu);
                var fileMenu = (MenuItem)menu!.Items[0];
                Assert.Equal(MenuItemRole.TopLevelHeader, fileMenu.Role);
                fileMenu.ApplyTemplate();

                var popup = fileMenu.Template.FindName("PART_Popup", fileMenu) as Popup;
                Assert.NotNull(popup);
                var popupBorder = popup!.Child as Border;
                Assert.NotNull(popupBorder);

                var popupBackground = ((SolidColorBrush)popupBorder!.Background).Color;
                var headerForeground = ((SolidColorBrush)fileMenu.Foreground).Color;
                Assert.True(RelativeLuminance(popupBackground) < 0.25,
                    $"菜单弹层背景应为深色，实际 {popupBackground}");
                Assert.True(ContrastRatio(headerForeground, popupBackground) >= 4.5,
                    $"一级菜单文字对比度不足：前景 {headerForeground} / 背景 {popupBackground}（{ContrastRatio(headerForeground, popupBackground):F2}）");

                var subItems = fileMenu.Items.OfType<MenuItem>().ToList();
                Assert.NotEmpty(subItems);
                foreach (var subItem in subItems)
                {
                    var subForeground = ((SolidColorBrush)subItem.Foreground).Color;
                    Assert.True(ContrastRatio(subForeground, popupBackground) >= 4.5,
                        $"子菜单「{subItem.Header}」对比度不足：前景 {subForeground} / 背景 {popupBackground}（{ContrastRatio(subForeground, popupBackground):F2}）");
                }

                // 真正把弹层渲染一次，用像素统计客观验证「深底浅字」，并落盘截图供人工核对
                fileMenu.IsSubmenuOpen = true;
                Pump(10);
                var popupWidth = (int)Math.Round(popupBorder.ActualWidth);
                var popupHeight = (int)Math.Round(popupBorder.ActualHeight);
                Assert.True(popupWidth > 40 && popupHeight > 20,
                    $"菜单弹层未完成布局：{popupWidth}x{popupHeight}");

                var rtb = new RenderTargetBitmap(popupWidth, popupHeight, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(popupBorder);
                var stride = popupWidth * 4;
                var pixels = new byte[stride * popupHeight];
                rtb.CopyPixels(pixels, stride, 0);

                var total = popupWidth * popupHeight;
                var darkPixels = 0;
                var lightPixels = 0;
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    if (pixels[i + 3] < 32) continue; // 透明像素不计
                    var luminance = (0.2126 * pixels[i + 2] + 0.7152 * pixels[i + 1] + 0.0722 * pixels[i]) / 255.0;
                    if (luminance < 0.35) darkPixels++;
                    else if (luminance > 0.6) lightPixels++;
                }

                _output.WriteLine($"菜单弹层像素统计：{popupWidth}x{popupHeight}，深色 {darkPixels}，浅色 {lightPixels}");
                Assert.True(darkPixels > total * 0.5, $"菜单弹层背景应占多数深色像素（{darkPixels}/{total}）");
                Assert.True(lightPixels > 30, $"菜单弹层应有浅色文字像素（{lightPixels}/{total}）");
                Assert.True(lightPixels < total * 0.3, $"浅色像素过多，疑似白底（{lightPixels}/{total}）");

                // 浅色像素应分布在多行上（说明是多行菜单文字，而不是一处噪点）
                var rowsWithText = 0;
                for (var y = 0; y < popupHeight; y++)
                {
                    var rowLight = 0;
                    for (var x = 0; x < popupWidth; x++)
                    {
                        var i = (y * popupWidth + x) * 4;
                        if (pixels[i + 3] < 32) continue;
                        var luminance = (0.2126 * pixels[i + 2] + 0.7152 * pixels[i + 1] + 0.0722 * pixels[i]) / 255.0;
                        if (luminance > 0.6) rowLight++;
                    }
                    if (rowLight >= 4) rowsWithText++;
                    _output.WriteLine($"  第 {y,3} 行浅色像素 {rowLight}");
                }
                Assert.True(rowsWithText >= 6, $"菜单文字行数过少（{rowsWithText}），疑似没有渲染出文字");

                var renderDir = ArtifactDirectory();
                Directory.CreateDirectory(renderDir);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));
                var menuPng = Path.Combine(renderDir, "menu-popup.png");
                using (var stream = File.Create(menuPng)) encoder.Save(stream);
                _output.WriteLine("菜单弹层截图：" + menuPng);

                fileMenu.IsSubmenuOpen = false;
                Pump(3);

                // 中键快速删除：命中屏幕坐标后删除最上层标记
                viewModel.Viewport.SetCanvas(800f, 600f);
                viewModel.Viewport.SetImage(320, 240);
                var screen = viewModel.Viewport.NormalizedToScreen(prototype.X, prototype.Y);
                Assert.True(viewModel.DeleteMarkerAtAsync(screen.X, screen.Y).GetAwaiter().GetResult());
                Pump();
                Assert.Empty(viewModel.Markers);

                // 中键点在空白处：什么也不删
                Assert.False(viewModel.DeleteMarkerAtAsync(5f, 5f).GetAwaiter().GetResult());
                Pump();

                // Delete 键路径（先重新放一个）
                var second = viewModel.CreatePrototype(ToolKind.Text, new Core.Geometry.MapPoint(0.6f, 0.6f), "文本标记");
                viewModel.AddMarkerAsync(second).GetAwaiter().GetResult();
                Pump();
                Assert.Single(viewModel.Markers);
                Assert.Equal(second.Id, viewModel.SelectedMarkerId);

                viewModel.DeleteSelectedAsync().GetAwaiter().GetResult();
                Pump();
                Assert.Empty(viewModel.Markers);

                // ── 撤销 / 重做 ──
                Assert.True(viewModel.CanUndo);
                Assert.True(viewModel.UndoAsync().GetAwaiter().GetResult());
                WaitFor(() => viewModel.Markers.Count == 1, TimeSpan.FromSeconds(5));
                Assert.Equal(second.Id, viewModel.Markers[0].Id);

                Assert.True(viewModel.CanRedo);
                Assert.True(viewModel.RedoAsync().GetAwaiter().GetResult());
                WaitFor(() => viewModel.Markers.Count == 0, TimeSpan.FromSeconds(5));

                // 撤销「新增」→ 标记消失；再撤销一次 → 回到空状态
                Assert.True(viewModel.UndoAsync().GetAwaiter().GetResult()); // 撤销 delete
                WaitFor(() => viewModel.Markers.Count == 1, TimeSpan.FromSeconds(5));
                Assert.True(viewModel.UndoAsync().GetAwaiter().GetResult()); // 撤销 add
                WaitFor(() => viewModel.Markers.Count == 0, TimeSpan.FromSeconds(5));

                // ── 自由笔画：直接构造带折线的原型并提交 ──
                viewModel.SelectTool(ToolKind.Freehand);
                Assert.Equal(MarkerType.Stroke, ToolKinds.MarkerTypeOf(ToolKind.Freehand));
                var stroke = viewModel.CreatePrototype(ToolKind.Freehand, new Core.Geometry.MapPoint(0.2f, 0.2f));
                stroke.Points = new[]
                {
                    new MarkerPoint(0.2f, 0.2f),
                    new MarkerPoint(0.3f, 0.25f),
                    new MarkerPoint(0.4f, 0.2f),
                };
                stroke.Thickness = 0.006f;
                viewModel.AddMarkerAsync(stroke).GetAwaiter().GetResult();
                Pump();
                var storedStroke = Assert.Single(viewModel.Markers);
                Assert.Equal(MarkerType.Stroke, storedStroke.Type);
                Assert.Equal(3, storedStroke.PointCount);
                Assert.Equal(0.006f, storedStroke.Thickness, 4);

                // 笔画命中检测
                viewModel.Viewport.SetCanvas(800f, 600f);
                viewModel.Viewport.SetImage(320, 240);
                var onStroke = viewModel.Viewport.NormalizedToScreen(0.3f, 0.25f);
                Assert.Equal(stroke.Id, viewModel.HitTestMarker(onStroke.X, onStroke.Y));

                // ── 大小控制点已取消：图标/文本不再提供控制点 ──
                viewModel.SelectTool(ToolKind.Kill);
                var icon = viewModel.CreatePrototype(ToolKind.Kill, new Core.Geometry.MapPoint(0.7f, 0.7f));
                viewModel.AddMarkerAsync(icon).GetAwaiter().GetResult();
                Pump();
                viewModel.SelectMarker(icon.Id);
                Assert.Empty(MarkerGeometry.GetHandles(viewModel.FindMarker(icon.Id)!, viewModel.Viewport));

                // ── 粗细：选中圆圈后改 Thickness ──
                viewModel.SelectTool(ToolKind.Circle);
                var circle = viewModel.CreatePrototype(ToolKind.Circle, new Core.Geometry.MapPoint(0.5f, 0.4f));
                viewModel.AddMarkerAsync(circle).GetAwaiter().GetResult();
                Pump();
                viewModel.SelectMarker(circle.Id);
                Assert.True(viewModel.CanEditThickness);
                viewModel.CurrentThickness = 0.012f;
                viewModel.ApplyCurrentThicknessToSelection();
                WaitFor(() =>
                {
                    var m = viewModel.FindMarker(circle.Id);
                    return m is not null && Math.Abs(m.Thickness - 0.012f) < 1e-4f;
                }, TimeSpan.FromSeconds(5));
                Assert.Equal(0.012f, viewModel.FindMarker(circle.Id)!.Thickness, 4);

                // ── 网格：只有一级，引用形如 C3（不含冒号） ──
                viewModel.Viewport.SetCanvas(900f, 700f);
                viewModel.Viewport.SetImage(320, 240);
                viewModel.UpdateHover(450f, 350f);
                var reference = viewModel.GridLabel;
                _output.WriteLine($"网格引用：{reference}");
                Assert.NotEqual("—", reference);
                Assert.DoesNotContain(':', reference);
                Assert.Matches("^[A-J][0-9]$", reference);

                window.UpdateLayout();
                Pump();

                // ── 回归：点右上角 X（Close()）关闭窗口不得抛异常 ──
                // 旧实现是 async void + 在 Closing 里再次 Close()，会抛
                // InvalidOperationException「在窗口关闭期间…无法调用 Show/Close」。
                var closeErrors = new List<Exception>();
                var windowClosed = false;
                window.Closed += (_, _) => windowClosed = true;
                window.Dispatcher.UnhandledException += (_, e) =>
                {
                    closeErrors.Add(e.Exception);
                    e.Handled = true;
                };

                window.Close();
                WaitFor(() => windowClosed, TimeSpan.FromSeconds(20));

                Assert.True(windowClosed, "窗口应当已被关闭");
                Assert.False(window.IsVisible, "关闭后窗口不应仍可见");
                foreach (var error in closeErrors) _output.WriteLine("关闭时异常: " + error);
                Assert.Empty(closeErrors);

                // ── 房主 + 客户端在同一 UI 线程上跨线程同步（验证 Dispatcher 封送）──
                var hostCache = TestHelpers.CreateTempCache();
                var clientCache = TestHelpers.CreateTempCache();
                var host = TacticalSession.CreateHostAsync(new SessionConfig
                {
                    PlayerName = "UI房主",
                    ImageBytes = TestHelpers.CreateTestImage(),
                    ImageWidth = 320,
                    ImageHeight = 240,
                    Port = 0,
                    RoomPassword = "pw",
                }, hostCache).GetAwaiter().GetResult();

                var client = TacticalSession.JoinAsync(new SessionConfig
                {
                    PlayerName = "UI客户端",
                    HostIp = "127.0.0.1",
                    Port = host.Port,
                    RoomPassword = "pw",
                }, clientCache).GetAwaiter().GetResult();

                var hostVm = new MainViewModel(Dispatcher.CurrentDispatcher, settings, hostCache);
                var clientVm = new MainViewModel(Dispatcher.CurrentDispatcher, settings, clientCache);
                PumpUntil(hostVm.AttachSessionAsync(host), TimeSpan.FromSeconds(20));
                PumpUntil(clientVm.AttachSessionAsync(client), TimeSpan.FromSeconds(20));
                WaitFor(() => clientVm.Session is not null && clientVm.Markers.Count == 0, TimeSpan.FromSeconds(10));

                // 房主新增标记 → 客户端视图模型（网络线程 → Dispatcher → 集合）收到
                var hostedMarker = hostVm.CreatePrototype(ToolKind.Arrow, new Core.Geometry.MapPoint(0.3f, 0.3f));
                hostVm.AddMarkerAsync(hostedMarker).GetAwaiter().GetResult();
                WaitFor(() => clientVm.Markers.Count == 1, TimeSpan.FromSeconds(15));
                Assert.Single(clientVm.Markers);
                Assert.Equal(MarkerType.Arrow, clientVm.Markers[0].Type);

                // 客户端移动自己的标记 → 房主视图模型跟进
                var clientMarker = clientVm.CreatePrototype(ToolKind.Target, new Core.Geometry.MapPoint(0.6f, 0.6f));
                clientVm.AddMarkerAsync(clientMarker).GetAwaiter().GetResult();
                WaitFor(() => hostVm.Markers.Any(m => m.OwnerId == client.PlayerId), TimeSpan.FromSeconds(15));

                var own = clientVm.Markers.First(m => m.OwnerId == client.PlayerId).Clone();
                own.X = 0.85f;
                clientVm.UpdateMarkerAsync(own).GetAwaiter().GetResult();
                WaitFor(() =>
                {
                    var onHost = hostVm.Markers.FirstOrDefault(m => m.Id == own.Id);
                    return onHost is not null && Math.Abs(onHost.X - 0.85f) < 1e-3f;
                }, TimeSpan.FromSeconds(15));

                // 客户端尝试改房主的标记：房主侧不应变化
                var foreign = hostedMarker.Clone();
                foreign.Y = 0.95f;
                clientVm.UpdateMarkerAsync(foreign).GetAwaiter().GetResult();
                Thread.Sleep(300);
                Pump();
                var foreignOnHost = hostVm.Markers.First(m => m.Id == hostedMarker.Id);
                Assert.NotEqual(0.95f, foreignOnHost.Y);

                clientVm.DetachSessionAsync().GetAwaiter().GetResult();
                hostVm.DetachSessionAsync().GetAwaiter().GetResult();
                WaitFor(() => hostVm.Markers.Count == 0 && clientVm.Markers.Count == 0, TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(90)), "WPF 冒烟测试超时");

        foreach (var error in bindingErrors) _output.WriteLine("绑定错误: " + error);
        if (failure is not null) _output.WriteLine("UI 冒烟失败: " + failure);
        Assert.True(failure is null, failure?.ToString());
        Assert.Empty(bindingErrors);
    }

    private static void Pump(int rounds = 6)
    {
        for (var i = 0; i < rounds; i++)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
    }

    /// <summary>WCAG 相对亮度。</summary>
    private static double RelativeLuminance(Color color)
    {
        static double Channel(byte value)
        {
            var s = value / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    /// <summary>WCAG 对比度（4.5 以上才算正文可读）。</summary>
    private static double ContrastRatio(Color foreground, Color background)
    {
        var a = RelativeLuminance(foreground);
        var b = RelativeLuminance(background);
        var lighter = Math.Max(a, b);
        var darker = Math.Min(a, b);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static string ArtifactDirectory()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            if (File.Exists(Path.Combine(dir, "TarkovTacticalMap.sln")))
            {
                return Path.Combine(dir, "artifacts", "renders");
            }
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar)) ?? string.Empty;
        }
        return Path.Combine(Path.GetTempPath(), "tarkov-tactical-map-renders");
    }

    /// <summary>在 STA 线程上等待任务完成，期间持续泵消息（不能直接阻塞，否则会自我死锁）。</summary>
    private static void PumpUntil(Task task, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("等待任务超时");
            Thread.Sleep(15);
            Pump(2);
        }
        task.GetAwaiter().GetResult();
    }

    /// <summary>在 STA 线程上轮询条件，期间持续泵消息。</summary>
    private static void WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("等待条件超时");
            Thread.Sleep(20);
            Pump(2);
        }
    }

    private sealed class CollectingTraceListener : TraceListener
    {
        private readonly List<string> _sink;

        public CollectingTraceListener(List<string> sink) => _sink = sink;

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message)) _sink.Add(message);
        }
    }
}
