using System.Windows;
using System.Windows.Threading;
using TarkovTacticalMap.App.Services;
using TarkovTacticalMap.App.ViewModels;
using TarkovTacticalMap.App.Views;

namespace TarkovTacticalMap.App;

/// <summary>应用入口：先显示首页（创建 / 加入房间），再打开主窗口。</summary>
public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();

    public static ImageCache Cache { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => ShowCrash(args.ExceptionObject as Exception);

        Settings = AppSettings.Load();
        Cache = new ImageCache();

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 命令行快捷入口：--offline 直接用本地模式进入，跳过首页（便于自测）
        if (e.Args.Any(a => a is "--offline" or "-o" or "/offline"))
        {
            var offlineConfig = new SessionConfig
            {
                PlayerName = Settings.PlayerName,
                GridDensity = Settings.GridDensity,
            };
            var offline = TacticalSession.CreateOfflineAsync(offlineConfig, Cache).GetAwaiter().GetResult();
            ShowMainWindow(offline);
            return;
        }

        var startup = new StartupWindow(Settings, Cache);
        var accepted = startup.ShowDialog() == true;
        var session = startup.ResultSession;
        if (!accepted || session is null)
        {
            Shutdown();
            return;
        }

        ShowMainWindow(session);
    }

    private static void ShowMainWindow(TacticalSession session)
    {
        var viewModel = new MainViewModel(Current.Dispatcher, Settings, Cache);
        var window = new MainWindow(viewModel, session, Settings);
        Current.MainWindow = window;
        Current.ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowCrash(e.Exception);
        e.Handled = true;
    }

    private static void ShowCrash(Exception? exception)
    {
        if (exception is null) return;
        try
        {
            MessageBox.Show(
                $"程序遇到未处理的错误：\n\n{exception.GetType().Name}: {exception.Message}\n\n{exception.StackTrace}",
                "塔塔战术平板 · 错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // 忽略弹窗失败
        }
    }
}
