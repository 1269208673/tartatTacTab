using TarkovTacticalMap.App.Services;
using TarkovTacticalMap.Core.Models;
using TarkovTacticalMap.Core.Utils;
using TarkovTacticalMap.Network;

namespace TarkovTacticalMap.Tests;

/// <summary>测试公用工具。</summary>
internal static class TestHelpers
{
    /// <summary>轮询等待条件成立。</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string? message = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        if (!condition())
        {
            throw new TimeoutException(message ?? $"等待条件超时（{timeout.TotalSeconds:F1}s）");
        }
    }

    /// <summary>等待 Task 完成并带超时。</summary>
    public static Task<T> WithTimeout<T>(this Task<T> task, int seconds = 15)
        => task.WaitAsync(TimeSpan.FromSeconds(seconds));

    public static Task WithTimeout(this Task task, int seconds = 15)
        => task.WaitAsync(TimeSpan.FromSeconds(seconds));

    /// <summary>创建临时缓存目录。</summary>
    public static ImageCache CreateTempCache()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TarkovTacticalMapTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new ImageCache(dir);
    }

    /// <summary>造一个标记。</summary>
    public static Marker NewMarker(MarkerType type, string? id = null, string ownerId = "host", float x = 0.5f, float y = 0.5f)
        => new()
        {
            Id = id ?? Guid.NewGuid().ToString("D"),
            Type = type,
            X = x,
            Y = y,
            Size = MarkerDefaults.DefaultSizeFor(type),
            Color = "#FFE066",
            OwnerId = ownerId,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

    /// <summary>小体积测试图片（PNG 字节）。</summary>
    public static byte[] CreateTestImage()
        => TarkovTacticalMap.App.Rendering.BlankMapFactory.CreatePng(320, 240, "测试地图");

    /// <summary>测试用客户端配置。</summary>
    public static GameClientOptions ClientOptions(int port, string password = "pw", string name = "队友")
        => new()
        {
            Host = "127.0.0.1",
            Port = port,
            RoomPasswordHash = RoomPassword.Hash(password),
            PlayerName = name,
            EnableAutoReconnect = true,
            InitialReconnectDelay = TimeSpan.FromMilliseconds(150),
            MaxReconnectDelay = TimeSpan.FromMilliseconds(500),
            HeartbeatInterval = TimeSpan.FromMilliseconds(300),
            HeartbeatTimeout = TimeSpan.FromSeconds(4),
            ConnectTimeout = TimeSpan.FromSeconds(5),
            HelloTimeout = TimeSpan.FromSeconds(5),
            MapRequestTimeout = TimeSpan.FromSeconds(10),
        };

    /// <summary>测试用房主配置。</summary>
    public static HostServerOptions HostOptions(int port, byte[] image, string password = "pw")
    {
        var map = MapInfo.CreateBlank(HashUtil.Sha256Hex(image), 320, 240);
        return new HostServerOptions
        {
            Port = port,
            RoomPasswordHash = RoomPassword.Hash(password),
            HostPlayer = new PlayerInfo { PlayerId = "host-player", PlayerName = "房主", IsHost = true },
            Map = map,
            ImageProvider = hash => HashUtil.VerifySha256(image, hash) ? image : null,
            HeartbeatInterval = TimeSpan.FromMilliseconds(300),
            HeartbeatTimeout = TimeSpan.FromSeconds(4),
            HelloTimeout = TimeSpan.FromSeconds(5),
        };
    }
}
