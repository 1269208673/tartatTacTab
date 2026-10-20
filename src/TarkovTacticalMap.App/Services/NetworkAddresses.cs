using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TarkovTacticalMap.App.Services;

/// <summary>
/// 遍历网卡，列出本机可用于队友连接的 IPv4 地址（过滤回环），
/// 并把常见虚拟局域网工具的网卡排在前面（ZeroTier / Tailscale / Radmin / Hamachi / WireGuard…）。
/// </summary>
public static class NetworkAddresses
{
    private static readonly string[] VpnHints =
    {
        "zerotier", "tailscale", "radmin", "hamachi", "wireguard", "openvpn", "tap", "vpn", "virtual",
    };

    /// <summary>获取候选地址（异步包装，避免阻塞 UI 线程的瞬时卡顿）。</summary>
    public static Task<IReadOnlyList<string>> GetLanAddressesAsync()
        => Task.Run(() => GetLanAddresses());

    /// <summary>获取候选地址与网卡名（异步包装）。</summary>
    public static Task<IReadOnlyList<(string Address, string Interface)>> GetDetailedAddressesAsync()
        => Task.Run(() => GetDetailedAddresses());

    /// <summary>获取候选地址：(地址, 网卡名)。</summary>
    public static IReadOnlyList<(string Address, string Interface)> GetDetailedAddresses()
    {
        var result = new List<(string Address, string Interface, int Priority)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var props = nic.GetIPProperties();
                var priority = 2;
                foreach (var hint in VpnHints)
                {
                    if (nic.Name.Contains(hint, StringComparison.OrdinalIgnoreCase) ||
                        nic.Description.Contains(hint, StringComparison.OrdinalIgnoreCase))
                    {
                        priority = 0;
                        break;
                    }
                }
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                {
                    priority = Math.Min(priority, 1);
                }

                foreach (var unicast in props.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = unicast.Address.ToString();
                    if (IPAddress.IsLoopback(unicast.Address)) continue;
                    if (ip.StartsWith("169.254.", StringComparison.Ordinal)) continue; // APIPA
                    result.Add((ip, nic.Name, priority));
                }
            }
        }
        catch
        {
            // 枚举失败时返回空列表
        }

        return result
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.Interface, StringComparer.OrdinalIgnoreCase)
            .Select(r => (r.Address, r.Interface))
            .Distinct()
            .ToList();
    }

    /// <summary>获取候选地址字符串列表。</summary>
    public static IReadOnlyList<string> GetLanAddresses()
        => GetDetailedAddresses().Select(r => r.Address).ToList();
}
