using System.Text;
using System.Windows;
using TarkovTacticalMap.App.Services;

namespace TarkovTacticalMap.App.Views;

/// <summary>玩家列表 / 房间信息窗口。</summary>
public partial class PlayerListDialog : Window
{
    public PlayerListDialog(TacticalSession session)
    {
        InitializeComponent();

        var sb = new StringBuilder();
        foreach (var player in session.Players)
        {
            sb.AppendLine(player.ToString());
        }
        PlayerText.Text = sb.Length == 0 ? "（暂无在线玩家）" : sb.ToString().TrimEnd();

        var modeText = session.Mode switch
        {
            SessionMode.Host => $"房主模式 · 监听端口 {session.Port}",
            SessionMode.Client => $"客户端模式 · 房主 {session.Config.HostIp}:{session.Config.Port}",
            _ => "本地模式（不联网）",
        };
        ModeText.Text = modeText;

        var addresses = NetworkAddresses.GetDetailedAddresses();
        HostAddressText.Text = addresses.Count == 0
            ? "（未发现可用 IPv4 地址）"
            : string.Join("\n", addresses.Select(a => $"{a.Address}    [{a.Interface}]"));
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
