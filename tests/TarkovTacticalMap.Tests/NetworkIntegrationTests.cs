using System.Net.Sockets;
using TarkovTacticalMap.Core.Models;
using TarkovTacticalMap.Core.Protocol;
using TarkovTacticalMap.Core.Serialization;
using TarkovTacticalMap.Core.Utils;
using TarkovTacticalMap.Network;
using Xunit;

namespace TarkovTacticalMap.Tests;

/// <summary>回环 TCP 端到端测试：握手、快照、广播、权限、地图分块、断线重连。</summary>
public class NetworkIntegrationTests
{
    private static readonly byte[] Image = TestHelpers.CreateTestImage();

    [Fact]
    public async Task Client_Join_ReceivesWelcomeWithFullSnapshot()
    {
        await using var host = new HostServer(TestHelpers.HostOptions(0, Image));
        await host.StartAsync();
        await host.AddMarkerAsync(TestHelpers.NewMarker(MarkerType.Circle, ownerId: host.HostPlayer.PlayerId));

        await using var client = new GameClient(TestHelpers.ClientOptions(host.Port));
        var welcome = await ConnectAsync(client);

        Assert.Single(welcome.Markers);
        Assert.Equal(host.CurrentMap.ImageHash, welcome.MapInfo.ImageHash);
        Assert.Equal(320, welcome.MapInfo.ImageWidth);
        Assert.Contains(welcome.Players, p => p.PlayerId == welcome.PlayerId);
        Assert.Contains(welcome.Players, p => p.IsHost);
        Assert.Equal(ConnectionState.Connected, client.State);
    }

    [Fact]
    public async Task Client_AddMarker_IsStoredByHostAndBroadcastBack()
    {
        await using var host = new HostServer(TestHelpers.HostOptions(0, Image));
        await host.StartAsync();
        await using var client = new GameClient(TestHelpers.ClientOptions(host.Port));
        var welcome = await ConnectAsync(client);

        var addedOnHost = new TaskCompletionSource<Marker>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.MarkerAdded += (_, e) => addedOnHost.TrySetResult(e.Marker);

        var echoOnClient = new TaskCompletionSource<Marker>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MarkerAdded += (_, e) => echoOnClient.TrySetResult(e.Marker);

        var marker = TestHelpers.NewMarker(MarkerType.Point, ownerId: welcome.PlayerId);
        marker.IconId = "kill";
        Assert.True(await client.SendAddAsync(marker));

        var hostMarker = await addedOnHost.Task.WithTimeout();
        var clientMarker = await echoOnClient.Task.WithTimeout();

        Assert.Equal(marker.Id, hostMarker.Id);
        Assert.Equal("kill", hostMarker.IconId);
        Assert.Equal(welcome.PlayerId, hostMarker.OwnerId); // 房主以连接身份标记 OwnerId
        Assert.Equal(marker.Id, clientMarker.Id);
        Assert.Equal(1, host.Markers.Count);
    }

    [Fact]
    public async Task Client_UpdateAndDelete_PropagateToEveryone()
    {
        await using var host = new HostServer(TestHelpers.HostOptions(0, Image));
        await host.StartAsync();
        await using var clientA = new GameClient(TestHelpers.ClientOptions(host.Port, name: "队友A"));
        var welcomeA = await ConnectAsync(clientA);

        await using var clientB = new GameClient(TestHelpers.ClientOptions(host.Port, name: "队友B"));
        await ConnectAsync(clientB);

        var addedOnB = new TaskCompletionSource<Marker>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientB.MarkerAdded += (_, e) => addedOnB.TrySetResult(e.Marker);

        var marker = TestHelpers.NewMarker(MarkerType.Arrow, ownerId: welcomeA.PlayerId);
        marker.Size = 0.1f;
        await clientA.SendAddAsync(marker);
        var seenByB = await addedOnB.Task.WithTimeout();
        Assert.Equal(marker.Id, seenByB.Id);

        var updatedOnB = new TaskCompletionSource<Marker>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientB.MarkerUpdated += (_, e) => updatedOnB.TrySetResult(e.Marker);

        var updated = seenByB.Clone();
        updated.Rotation = 135f;
        updated.X = 0.2f;
        await clientA.SendUpdateAsync(updated);

        var afterUpdate = await updatedOnB.Task.WithTimeout();
        Assert.Equal(135f, afterUpdate.Rotation, 3);
        Assert.Equal(0.2f, afterUpdate.X, 3);

        var deletedOnB = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientB.MarkerDeleted += (_, e) => deletedOnB.TrySetResult(e.Id);
        await clientA.SendDeleteAsync(marker.Id);
        Assert.Equal(marker.Id, await deletedOnB.Task.WithTimeout());
        await TestHelpers.WaitUntilAsync(() => host.Markers.Count == 0, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Client_CannotModifyOtherPlayersMarker()
    {
        await using var host = new HostServer(TestHelpers.HostOptions(0, Image));
        await host.StartAsync();

        var hostMarker = TestHelpers.NewMarker(MarkerType.Rectangle, ownerId: host.HostPlayer.PlayerId);
        await host.AddMarkerAsync(hostMarker);

        await using var client = new GameClient(TestHelpers.ClientOptions(host.Port));
        await ConnectAsync(client);

        var error = new TaskCompletionSource<ClientErrorEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Error += (_, e) => error.TrySetResult(e);

        var attempt = hostMarker.Clone();
        attempt.X = 0.1f;
        await client.SendUpdateAsync(attempt);

        var clientError = await error.Task.WithTimeout();
        Assert.Equal(ErrorCode.PermissionDenied, clientError.Code);

        // 房主侧状态未被修改
        Assert.Equal(hostMarker.X, host.Markers.Get(hostMarker.Id)!.X, 4);

        // 删除同样被拒绝
        var deleteError = new TaskCompletionSource<ClientErrorEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Error += (_, e) => deleteError.TrySetResult(e);
        await client.SendDeleteAsync(hostMarker.Id);
        var deleteClientError = await deleteError.Task.WithTimeout();
        Assert.Equal(ErrorCode.PermissionDenied, deleteClientError.Code);
        Assert.Equal(1, host.Markers.Count);
    }

    [Fact]
    public async Task DuplicateAdd_IsIdempotent()
    {
        await using var host = new HostServer(TestHelpers.HostOptions(0, Image));
        await host.StartAsync();
        await using var client = new GameClient(TestHelpers.ClientOptions(host.Port));
        var welcome = await ConnectAsync(client);

        var marker = TestHelpers.NewMarker(MarkerType.Point, id: "fixed-id", ownerId: welcome.PlayerId);
        await client.SendAddAsync(marker);
        await TestHelpers.WaitUntilAsync(() => host.Markers.Count == 1, TimeSpan.FromSeconds(5));
        await client.SendAddAsync(marker);
        await Task.Delay(200);
        Assert.Equal(1, host.Markers.Count);
    }

    [Fact]
    public async Task Client_RequestsMapImage_AndReceivesExactBytes()
    {
        await using var host = new HostServer(TestHelpers.HostOptions(0, Image));
        await host.StartAsync();
        await using var client = new GameClient(TestHelpers.ClientOptions(host.Port));
        var welcome = await ConnectAsync(client);

        var bytes = await client.RequestMapAsync(welcome.MapInfo.ImageHash).WithTimeout(20);
        Assert.NotNull(bytes);
        Assert.Equal(Image, bytes!);
        Assert.True(HashUtil.VerifySha256(bytes!, welcome.MapInfo.ImageHash));
    }

    [Fact]
    public async Task WrongPassword_IsRejectedWithAuthFailed()
    {
        await using var host = new HostServer(TestHelpers.HostOptions(0, Image, password: "correct"));
        await host.StartAsync();

        await using var client = new GameClient(TestHelpers.ClientOptions(host.Port, password: "wrong"));
        var error = new TaskCompletionSource<ClientErrorEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Error += (_, e) => error.TrySetResult(e);

        await client.StartAsync();
        var clientError = await error.Task.WithTimeout();
        Assert.Equal(ErrorCode.AuthFailed, clientError.Code);
        await TestHelpers.WaitUntilAsync(() => client.State == ConnectionState.Faulted, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ProtocolVersionMismatch_IsRejected()
    {
        await using var host = new HostServer(TestHelpers.HostOptions(0, Image));
        await host.StartAsync();

        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", host.Port);
        var stream = tcp.GetStream();

        await FrameCodec.WriteAsync(stream, new Envelope(MessageType.Hello, MessagePackConfig.Serialize(new HelloMessage
        {
            ProtocolVersion = ProtocolConstants.ProtocolVersion + 99,
            RoomPasswordHash = RoomPassword.Hash("pw"),
            PlayerName = "老版本客户端",
        })));

        var envelope = await FrameCodec.ReadAsync(stream).AsTask().WithTimeout(10);
        Assert.NotNull(envelope);
        Assert.Equal(MessageType.Error, envelope!.Type);
        var error = MessagePackConfig.DeserializeBody<ErrorMessage>(envelope);
        Assert.Equal(ErrorCode.ProtocolVersionMismatch, error.Code);
    }

    [Fact]
    public async Task FirstFrameMustBeHello()
    {
        await using var host = new HostServer(TestHelpers.HostOptions(0, Image));
        await host.StartAsync();

        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", host.Port);
        var stream = tcp.GetStream();

        await FrameCodec.WriteAsync(stream, new Envelope(MessageType.Ping, MessagePackConfig.Serialize(new PingMessage())));
        var envelope = await FrameCodec.ReadAsync(stream).AsTask().WithTimeout(10);
        Assert.NotNull(envelope);
        var error = MessagePackConfig.DeserializeBody<ErrorMessage>(envelope!);
        Assert.Equal(ErrorCode.ProtocolError, error.Code);
    }

    [Fact]
    public async Task HostBroadcastsPlayerJoinAndLeave()
    {
        await using var host = new HostServer(TestHelpers.HostOptions(0, Image));
        await host.StartAsync();

        await using var clientA = new GameClient(TestHelpers.ClientOptions(host.Port, name: "队友A"));
        await ConnectAsync(clientA);

        var joined = new TaskCompletionSource<PlayerInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientA.PlayerJoined += (_, e) => joined.TrySetResult(e.Player);

        var clientB = new GameClient(TestHelpers.ClientOptions(host.Port, name: "队友B"));
        var left = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientA.PlayerLeft += (_, e) => left.TrySetResult(e.PlayerId);
        await ConnectAsync(clientB);

        var joinedPlayer = await joined.Task.WithTimeout();
        Assert.Equal("队友B", joinedPlayer.PlayerName);

        await clientB.StopAsync();
        var leftId = await left.Task.WithTimeout();
        Assert.Equal(joinedPlayer.PlayerId, leftId);
    }

    [Fact]
    public async Task Client_ReconnectsAfterNetworkDropAndRebuildsState()
    {
        var options = TestHelpers.HostOptions(0, Image);
        var host = new HostServer(options);
        await host.StartAsync();
        var port = host.Port;
        await host.AddMarkerAsync(TestHelpers.NewMarker(MarkerType.Circle, ownerId: host.HostPlayer.PlayerId));

        await using var client = new GameClient(TestHelpers.ClientOptions(port));
        var connects = new List<WelcomeEventArgs>();
        var reconnectTcs = new TaskCompletionSource<WelcomeEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Connected += (_, e) =>
        {
            lock (connects) connects.Add(e);
            if (e.IsReconnect) reconnectTcs.TrySetResult(e);
        };

        await client.StartAsync();
        await TestHelpers.WaitUntilAsync(() =>
        {
            lock (connects) return connects.Count >= 1;
        }, TimeSpan.FromSeconds(10));

        // 模拟断网：不发送 ERROR，直接断链
        await host.StopAsync(notifyClients: false);
        await TestHelpers.WaitUntilAsync(() => client.State != ConnectionState.Connected, TimeSpan.FromSeconds(10));

        // 房主恢复，客户端应自动重连并拿到全量快照
        await using var host2 = new HostServer(TestHelpers.HostOptions(port, Image));
        await host2.StartAsync();
        await host2.AddMarkerAsync(TestHelpers.NewMarker(MarkerType.Point, ownerId: host2.HostPlayer.PlayerId));

        var reconnectWelcome = await reconnectTcs.Task.WithTimeout(20);
        Assert.True(reconnectWelcome.IsReconnect);
        Assert.Single(reconnectWelcome.Welcome.Markers);
        Assert.Equal(ConnectionState.Connected, client.State);
    }

    [Fact]
    public async Task HostShutdown_NotifiesClientsToStopRetrying()
    {
        await using var host = new HostServer(TestHelpers.HostOptions(0, Image));
        await host.StartAsync();

        await using var client = new GameClient(TestHelpers.ClientOptions(host.Port));
        await ConnectAsync(client);

        var disconnected = new TaskCompletionSource<ConnectionEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += (_, e) => disconnected.TrySetResult(e);

        await host.StopAsync(notifyClients: true);

        var args = await disconnected.Task.WithTimeout(15);
        Assert.False(args.WillRetry);
    }

    private static async Task<WelcomeMessage> ConnectAsync(GameClient client, int seconds = 15)
    {
        var welcome = new TaskCompletionSource<WelcomeMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, WelcomeEventArgs e) => welcome.TrySetResult(e.Welcome);
        client.Connected += Handler;
        await client.StartAsync();
        try
        {
            return await welcome.Task.WaitAsync(TimeSpan.FromSeconds(seconds));
        }
        finally
        {
            client.Connected -= Handler;
        }
    }
}
