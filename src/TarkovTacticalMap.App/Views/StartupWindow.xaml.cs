using System.Windows;
using Microsoft.Win32;
using TarkovTacticalMap.App.Rendering;
using TarkovTacticalMap.App.Services;

namespace TarkovTacticalMap.App.Views;

/// <summary>首页：创建房间 / 加入房间 / 仅本机模式。</summary>
public partial class StartupWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ImageCache _cache;
    private byte[]? _imageBytes;
    private int _imageWidth;
    private int _imageHeight;
    private bool _busy;

    public StartupWindow(AppSettings settings, ImageCache cache)
    {
        InitializeComponent();
        _settings = settings;
        _cache = cache;

        HostNameBox.Text = settings.PlayerName;
        JoinNameBox.Text = settings.PlayerName;
        HostPortBox.Text = settings.LastPort.ToString();
        JoinPortBox.Text = settings.LastPort.ToString();
        JoinHostBox.Text = settings.LastHostIp;
        HostDensityBox.Text = settings.GridDensity.ToString();

        Loaded += async (_, _) => await RefreshAddressesAsync();
    }

    /// <summary>创建成功后带回主窗口的会话。</summary>
    public TacticalSession? ResultSession { get; private set; }

    private async Task RefreshAddressesAsync()
    {
        try
        {
            var addresses = await NetworkAddresses.GetDetailedAddressesAsync();
            HostAddressText.Text = addresses.Count == 0
                ? "（未发现可用 IPv4 地址，请先连接虚拟局域网）"
                : string.Join("\n", addresses.Select(a => $"{a.Address}   [{a.Interface}]"));
            if (addresses.Count > 0)
            {
                _settings.LastHostIp = _settings.LastHostIp == "127.0.0.1" ? addresses[0].Address : _settings.LastHostIp;
            }
        }
        catch
        {
            HostAddressText.Text = "（获取本机地址失败）";
        }
    }

    private async void OnPickHostImageClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择地图图片",
            Filter = ImageFile.Filter,
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true) return;

        SetBusy(true, "正在读取图片…");
        var loaded = await ImageFile.LoadAsync(dialog.FileName);
        SetBusy(false, null);

        if (loaded is null)
        {
            StatusText.Text = "无法读取该图片（格式不支持或文件损坏）。";
            return;
        }

        _imageBytes = loaded.Value.Bytes;
        _imageWidth = loaded.Value.Width;
        _imageHeight = loaded.Value.Height;
        HostImageText.Text = $"{System.IO.Path.GetFileName(dialog.FileName)} · {_imageWidth}×{_imageHeight} · {_imageBytes.Length / 1024} KB";
        _settings.LastImagePath = dialog.FileName;
        StatusText.Text = "";
    }

    private void OnUseBlankImageClick(object sender, RoutedEventArgs e)
    {
        _imageBytes = null;
        _imageWidth = 0;
        _imageHeight = 0;
        HostImageText.Text = "未选择（将使用空白网格）";
    }

    private async void OnCreateRoomClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var name = HostNameBox.Text.Trim();
        if (name.Length == 0)
        {
            StatusText.Text = "请填写玩家名。";
            return;
        }
        if (!TryParsePort(HostPortBox.Text, out var port))
        {
            StatusText.Text = "端口必须是 1~65535 的整数。";
            return;
        }
        if (!TryParseDensity(HostDensityBox.Text, out var density))        {
            StatusText.Text = "网格密度必须是 2~26 的整数。";
            return;
        }

        var bytes = _imageBytes;
        var width = _imageWidth;
        var height = _imageHeight;
        if (bytes is null || bytes.Length == 0)
        {
            SetBusy(true, "正在生成空白地图…");
            bytes = await Task.Run(() => BlankMapFactory.CreatePng());
            width = BlankMapFactory.DefaultWidth;
            height = BlankMapFactory.DefaultHeight;
            SetBusy(false, null);
        }

        var config = new SessionConfig
        {
            PlayerName = name,
            ImageBytes = bytes,
            ImageWidth = width,
            ImageHeight = height,
            Port = port,
            RoomPassword = HostPasswordBox.Password,
            GridDensity = density,
        };

        SetBusy(true, "正在启动房间（如弹出防火墙提示请允许）…");
        try
        {
            ResultSession = await TacticalSession.CreateHostAsync(config, _cache);
            PersistCreate(config);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"创建房间失败：{ex.Message}";
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private async void OnJoinRoomClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var name = JoinNameBox.Text.Trim();
        if (name.Length == 0)
        {
            StatusText.Text = "请填写玩家名。";
            return;
        }
        var hostIp = JoinHostBox.Text.Trim();
        if (hostIp.Length == 0)
        {
            StatusText.Text = "请填写房主的虚拟局域网 IP。";
            return;
        }
        if (!TryParsePort(JoinPortBox.Text, out var port))
        {
            StatusText.Text = "端口必须是 1~65535 的整数。";
            return;
        }

        var config = new SessionConfig
        {
            PlayerName = name,
            HostIp = hostIp,
            Port = port,
            RoomPassword = JoinPasswordBox.Password,
        };

        SetBusy(true, "正在连接房主…");
        try
        {
            ResultSession = await TacticalSession.JoinAsync(config, _cache);
            _settings.PlayerName = name;
            _settings.LastHostIp = hostIp;
            _settings.LastPort = port;
            _settings.Save();
            DialogResult = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"加入房间失败：{ex.Message}";
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private async void OnLocalModeClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var name = JoinNameBox.Text.Trim();
        if (name.Length == 0) name = HostNameBox.Text.Trim();
        if (name.Length == 0) name = _settings.PlayerName;

        var bytes = _imageBytes;
        var width = _imageWidth;
        var height = _imageHeight;
        if (bytes is null || bytes.Length == 0)
        {
            SetBusy(true, "正在生成空白地图…");
            bytes = await Task.Run(() => BlankMapFactory.CreatePng());
            width = BlankMapFactory.DefaultWidth;
            height = BlankMapFactory.DefaultHeight;
            SetBusy(false, null);
        }

        var config = new SessionConfig
        {
            PlayerName = name,
            ImageBytes = bytes,
            ImageWidth = width,
            ImageHeight = height,
            GridDensity = _settings.GridDensity,
        };

        try
        {
            ResultSession = await TacticalSession.CreateOfflineAsync(config, _cache);
            _settings.PlayerName = name;
            _settings.Save();
            DialogResult = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"启动本地模式失败：{ex.Message}";
        }
    }

    private void PersistCreate(SessionConfig config)
    {
        _settings.PlayerName = config.PlayerName;
        _settings.LastPort = config.Port;
        _settings.Save();
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void SetBusy(bool busy, string? message)
    {
        _busy = busy;
        if (message is not null) StatusText.Text = message;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
        IsEnabled = !busy;
        Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
    }

    private static bool TryParsePort(string text, out int port)
        => int.TryParse(text.Trim(), out port) && port is >= 1 and <= 65535;

    private static bool TryParseDensity(string text, out int density)
        => int.TryParse(text.Trim(), out density) && density is >= 2 and <= TarkovTacticalMap.Core.Models.MapInfo.MaxGrid;
}
