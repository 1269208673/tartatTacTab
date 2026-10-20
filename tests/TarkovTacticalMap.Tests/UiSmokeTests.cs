using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using TarkovTacticalMap.App.Rendering;
using TarkovTacticalMap.App.Services;
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
                    GridCols = 8,
                    GridRows = 6,
                }, cache).GetAwaiter().GetResult();

                var viewModel = new MainViewModel(Dispatcher.CurrentDispatcher, settings, cache);
                var window = new MainWindow(viewModel, session, settings);
                window.Show();
                window.UpdateLayout();
                Pump(10);

                // 会话已接入视图模型
                Assert.NotNull(viewModel.Session);
                Assert.Equal(SessionMode.Offline, viewModel.Session!.Mode);
                Assert.Equal(11, viewModel.Tools.Count); // 选择 + 5 语义图标 + 箭头/圆圈/方框/线/文本
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

                viewModel.DeleteSelectedAsync().GetAwaiter().GetResult();
                Pump();
                Assert.Empty(viewModel.Markers);

                window.UpdateLayout();
                Pump();
                window.Hide();
                session.DisposeAsync().AsTask().GetAwaiter().GetResult();

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
