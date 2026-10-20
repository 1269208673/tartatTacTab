using TarkovTacticalMap.App.Services;
using TarkovTacticalMap.Core.Models;
using TarkovTacticalMap.Core.State;
using TarkovTacticalMap.Core.Utils;
using Xunit;
using Xunit.Abstractions;

namespace TarkovTacticalMap.Tests;

/// <summary>
/// App 层会话门面（TacticalSession）的端到端测试：
/// 房主 + 客户端在回环上跑完整流程（WELCOME 快照、图片分发与缓存、标记同步、权限、重连语义）。
/// </summary>
public class SessionIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public SessionIntegrationTests(ITestOutputHelper output) => _output = output;

    private static SessionConfig HostConfig(string password = "pw")
        => new()
        {
            PlayerName = "房主",
            ImageBytes = TestHelpers.CreateTestImage(),
            ImageWidth = 320,
            ImageHeight = 240,
            Port = 0, // 由系统分配，避免端口冲突
            RoomPassword = password,
            GridCols = 8,
            GridRows = 6,
        };

    private static SessionConfig JoinConfig(int port, string password = "pw", string name = "队友")
        => new()
        {
            PlayerName = name,
            HostIp = "127.0.0.1",
            Port = port,
            RoomPassword = password,
        };

    [Fact]
    public async Task HostAndClient_SyncMarkersAndFetchMapImage()
    {
        var hostCache = TestHelpers.CreateTempCache();
        var clientCache = TestHelpers.CreateTempCache();

        await using var host = await TacticalSession.CreateHostAsync(HostConfig(), hostCache);
        Assert.True(host.Port > 0);
        Assert.Equal(SessionMode.Host, host.Mode);
        Assert.True(host.HasImage);

        await using var client = await TacticalSession.JoinAsync(JoinConfig(host.Port), clientCache);
        Assert.Equal(SessionMode.Client, client.Mode);
        Assert.Equal(host.Map.ImageHash, client.Map.ImageHash);

        // 地图图片经 MAP_REQUEST / MAP_CHUNK 传输并写入客户端缓存
        await TestHelpers.WaitUntilAsync(() => client.HasImage, TimeSpan.FromSeconds(15), "客户端未获取到地图图片");
        Assert.True(clientCache.TryLoad(client.Map.ImageHash, out var cached));
        Assert.NotNull(cached);
        Assert.True(HashUtil.VerifySha256(cached!, client.Map.ImageHash));

        // 房主新增 → 客户端收到权威广播
        var addedOnClient = new TaskCompletionSource<Marker>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MarkerAdded += (_, e) => addedOnClient.TrySetResult(e.Marker);

        var marker = new Marker
        {
            Type = MarkerType.Point,
            X = 0.5f,
            Y = 0.5f,
            Size = 0.02f,
            Color = "#FF5252",
            IconId = "kill",
            OwnerId = host.PlayerId,
        };
        var addResult = await host.AddMarkerAsync(marker);
        Assert.True(addResult.Applied);

        var arrived = await addedOnClient.Task.WithTimeout();
        Assert.Equal("kill", arrived.IconId);
        Assert.Equal(0.5f, arrived.X, 3);
        Assert.Equal(host.PlayerId, arrived.OwnerId);

        // 客户端更新自己的标记：先创建一个自己的标记
        var clientMarkerOnHost = new TaskCompletionSource<Marker>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.MarkerAdded += (_, e) =>
        {
            if (e.Marker.OwnerId != host.PlayerId) clientMarkerOnHost.TrySetResult(e.Marker);
        };
        var updatedOnHost = new TaskCompletionSource<Marker>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.MarkerUpdated += (_, e) => updatedOnHost.TrySetResult(e.Marker);

        await client.AddMarkerAsync(new Marker
        {
            Type = MarkerType.Circle,
            X = 0.3f,
            Y = 0.3f,
            Size = 0.05f,
            Color = "#40C4FF",
        });
        var ownMarker = await clientMarkerOnHost.Task.WithTimeout();
        Assert.Equal(client.PlayerId, ownMarker.OwnerId); // OwnerId 由房主按连接身份写入

        var edit = ownMarker.Clone();
        edit.Size = 0.09f;
        await client.UpdateMarkerAsync(edit);

        var applied = await updatedOnHost.Task.WithTimeout();
        Assert.Equal(0.09f, applied.Size, 4);
        await TestHelpers.WaitUntilAsync(
            () => Math.Abs(host.MarkerSnapshot().First(m => m.Id == ownMarker.Id).Size - 0.09f) < 1e-4f,
            TimeSpan.FromSeconds(5));

        // 客户端不能改房主的标记：请求被房主拒绝，房主侧状态不变
        var foreign = host.MarkerSnapshot().First(m => m.OwnerId == host.PlayerId).Clone();
        var originalX = foreign.X;
        foreign.X = 0.9f;
        var denied = await client.UpdateMarkerAsync(foreign);
        Assert.Equal(MutationOutcome.Sent, denied.Outcome); // 客户端只负责发出请求
        await Task.Delay(200);
        Assert.Equal(originalX, host.MarkerSnapshot().First(m => m.Id == foreign.Id).X, 4);
    }

    [Fact]
    public async Task Join_WithWrongPassword_Throws()
    {
        var hostCache = TestHelpers.CreateTempCache();
        await using var host = await TacticalSession.CreateHostAsync(HostConfig("correct"), hostCache);

        var clientCache = TestHelpers.CreateTempCache();
        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => TacticalSession.JoinAsync(JoinConfig(host.Port, "wrong"), clientCache));
        Assert.Contains("口令", ex.Message);
    }

    [Fact]
    public async Task OfflineSession_KeepsMarkersLocally()
    {
        var cache = TestHelpers.CreateTempCache();
        await using var session = await TacticalSession.CreateOfflineAsync(new SessionConfig
        {
            PlayerName = "本地",
            ImageBytes = TestHelpers.CreateTestImage(),
            ImageWidth = 320,
            ImageHeight = 240,
        }, cache);

        Assert.Equal(SessionMode.Offline, session.Mode);
        Assert.True(session.HasImage);

        var added = new TaskCompletionSource<Marker>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.MarkerAdded += (_, e) => added.TrySetResult(e.Marker);

        await session.AddMarkerAsync(new Marker
        {
            Type = MarkerType.Arrow,
            X = 0.2f,
            Y = 0.2f,
            Size = 0.1f,
            Rotation = 45f,
            Color = "#69F0AE",
        });

        var marker = await added.Task.WithTimeout();
        Assert.Equal(45f, marker.Rotation, 3);
        Assert.Equal(session.PlayerId, marker.OwnerId);
        Assert.Single(session.MarkerSnapshot());
        Assert.True(session.CanModify(marker));

        // 网格设置（本地视图设置）
        session.SetGrid(10, 7);
        Assert.Equal(10, session.Map.GridCols);
        session.ToggleGrid();
        Assert.False(session.Map.GridVisible);
    }

    [Fact]
    public void ImageCache_StoresLoadsAndClearsByHash()
    {
        var cache = TestHelpers.CreateTempCache();
        var image = TestHelpers.CreateTestImage();
        var hash = HashUtil.Sha256Hex(image);

        Assert.False(cache.TryLoad(hash, out _));
        Assert.True(cache.Save(hash, image));
        Assert.True(cache.TryLoad(hash, out var bytes));
        Assert.Equal(image, bytes);
        Assert.Equal(1, cache.GetFileCount());
        Assert.True(cache.GetTotalSizeBytes() > 0);

        // 损坏的缓存视为未命中
        var path = cache.GetPath(hash);
        var corrupted = (byte[])image.Clone();
        corrupted[0] ^= 0xFF;
        File.WriteAllBytes(path, corrupted);
        Assert.False(cache.TryLoad(hash, out _));

        Assert.Equal(1, cache.Clear());
        Assert.Equal(0, cache.GetFileCount());
    }

    [Fact]
    public void ImageCache_RejectsPathTraversalHash()
    {
        var cache = TestHelpers.CreateTempCache();
        var path = cache.GetPath("../../evil");
        Assert.StartsWith(cache.Root, path, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("..", Path.GetFileName(path));
    }

    [Fact]
    public void ImageFile_DetectsPngDimensions()
    {
        var bytes = TestHelpers.CreateTestImage();
        var size = ImageFile.DetectSize(bytes);
        Assert.NotNull(size);
        Assert.Equal(320, size!.Value.Width);
        Assert.Equal(240, size.Value.Height);
        Assert.Null(ImageFile.DetectSize(new byte[] { 1, 2, 3, 4 }));
    }

    [Fact]
    public void Settings_RoundTripsToLocalAppDataShape()
    {
        var settings = new AppSettings
        {
            PlayerName = "  测试玩家  ",
            LastHostIp = "10.147.20.5",
            LastPort = 27016,
            DefaultColor = "ff5252",
            DefaultPointSize = 0.03f,
        };
        settings.Save();

        var loaded = AppSettings.Load();
        Assert.Equal("测试玩家", loaded.PlayerName);
        Assert.Equal("10.147.20.5", loaded.LastHostIp);
        Assert.Equal(27016, loaded.LastPort);
        Assert.Equal("#FFFF5252", ColorHex.Normalize(loaded.DefaultColor));
        Assert.Equal(0.03f, loaded.DefaultPointSize, 4);
    }
}
