using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using TarkovTacticalMap.Core.Models;
using TarkovTacticalMap.Core.Protocol;
using TarkovTacticalMap.Core.Serialization;
using TarkovTacticalMap.Core.State;

namespace TarkovTacticalMap.Network;

/// <summary>房主服务端配置。</summary>
public sealed class HostServerOptions
{
    /// <summary>监听端口，0 表示由系统分配（测试用）。</summary>
    public int Port { get; set; } = ProtocolConstants.DefaultPort;

    /// <summary>房间口令的 SHA-256 小写 hex。</summary>
    public string RoomPasswordHash { get; set; } = string.Empty;

    /// <summary>房主玩家信息。</summary>
    public PlayerInfo HostPlayer { get; set; } = new() { PlayerName = "房主", IsHost = true };

    /// <summary>地图元数据（房间创建后固定）。</summary>
    public MapInfo Map { get; set; } = new();

    /// <summary>按 ImageHash 提供地图图片字节；返回 null 表示没有该图片。</summary>
    public Func<string, byte[]?>? ImageProvider { get; set; }

    public TimeSpan HeartbeatInterval { get; set; } = ProtocolConstants.HeartbeatInterval;

    public TimeSpan HeartbeatTimeout { get; set; } = ProtocolConstants.HeartbeatTimeout;

    /// <summary>连接建立后等待 HELLO 的超时。</summary>
    public TimeSpan HelloTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// 房主权威服务端（Listen Server）：
/// TcpListener 监听 0.0.0.0:端口，串行处理客户端标记变更，校验权限后广播给所有客户端。
/// 网络层不引用任何 UI 类型，通过事件通知上层。
/// </summary>
public sealed class HostServer : IAsyncDisposable
{
    private readonly HostServerOptions _options;
    private readonly MarkerStore _markers = new();
    private readonly List<ClientSession> _sessions = new();
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly object _sessionsGate = new();
    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private Task? _acceptTask;
    private Task? _heartbeatTask;
    private volatile bool _started;

    public HostServer(HostServerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Map ??= new MapInfo();
        _options.Map.ClampGrid();
        CurrentMap = _options.Map.Clone();
    }

    // ── 事件 ────────────────────────────────────────────────────────────

    public event EventHandler<MarkerEventArgs>? MarkerAdded;
    public event EventHandler<MarkerEventArgs>? MarkerUpdated;
    public event EventHandler<MarkerDeletedEventArgs>? MarkerDeleted;
    public event EventHandler<PlayerEventArgs>? PlayerJoined;
    public event EventHandler<PlayerLeftEventArgs>? PlayerLeft;
    public event EventHandler<ConnectionEventArgs>? ClientError;
    public event EventHandler<ConnectionEventArgs>? ClientDisconnected;

    // ── 状态 ────────────────────────────────────────────────────────────

    /// <summary>是否为运行中。</summary>
    public bool IsRunning => _started;

    /// <summary>实际监听端口（Port=0 时由系统分配）。</summary>
    public int Port { get; private set; }

    /// <summary>权威标记表。</summary>
    public MarkerStore Markers => _markers;

    /// <summary>当前地图元数据。</summary>
    public MapInfo CurrentMap { get; private set; }

    /// <summary>
    /// 更新房间地图（仅在还没有客户端加入时允许，避免已缓存旧图的客户端出现哈希不一致）。
    /// </summary>
    public bool UpdateMap(MapInfo map)
    {
        if (map is null) return false;
        if (ClientCount > 0) return false;

        var clone = map.Clone();
        clone.ClampGrid();
        CurrentMap = clone;
        _options.Map = clone;
        return true;
    }

    /// <summary>房主玩家信息。</summary>
    public PlayerInfo HostPlayer => _options.HostPlayer;

    /// <summary>在线玩家（含房主）。</summary>
    public IReadOnlyList<PlayerInfo> Players
    {
        get
        {
            var list = new List<PlayerInfo> { _options.HostPlayer.Clone() };
            lock (_sessionsGate)
            {
                foreach (var s in _sessions)
                {
                    if (s.IsAuthenticated) list.Add(s.Player.Clone());
                }
            }
            return list;
        }
    }

    /// <summary>已连接客户端数量（不含房主）。</summary>
    public int ClientCount
    {
        get { lock (_sessionsGate) return _sessions.Count; }
    }

    // ── 启停 ────────────────────────────────────────────────────────────

    /// <summary>绑定 0.0.0.0:端口并开始接受连接。</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started) throw new InvalidOperationException("服务器已启动");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Any, _options.Port);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _started = true;

        _acceptTask = AcceptLoopAsync(_cts.Token);
        _heartbeatTask = HeartbeatLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 停止服务并断开所有客户端。
    /// notifyClients=true 时会先发送 ERROR(ServerClosed)，客户端据此提示「房间已关闭」并停止重连；
    /// 模拟断网时用 false（直接断链，客户端会自动重连）。
    /// </summary>
    public async Task StopAsync(bool notifyClients = true)
    {
        if (!_started) return;
        _started = false;

        // 先关监听，再通知客户端，最后才取消令牌（否则写线程会被提前终止，ERROR 发不出去）
        try { _listener?.Stop(); } catch { /* ignore */ }

        ClientSession[] sessions;
        lock (_sessionsGate)
        {
            sessions = _sessions.ToArray();
            _sessions.Clear();
        }

        foreach (var s in sessions)
        {
            if (!notifyClients) continue;
            s.TrySend(new Envelope(MessageType.Error, MessagePackConfig.Serialize(new ErrorMessage
            {
                Code = ErrorCode.ServerClosed,
                Message = "房主已关闭房间",
            })));
        }

        if (notifyClients && sessions.Length > 0)
        {
            // 给写线程一点时间把 ERROR 发出去
            try { await Task.Delay(80).ConfigureAwait(false); } catch { /* ignore */ }
        }

        try { _cts?.Cancel(); } catch { /* ignore */ }

        foreach (var s in sessions)
        {
            await s.DisposeAsync().ConfigureAwait(false);
        }

        await WaitQuietlyAsync(_acceptTask).ConfigureAwait(false);
        await WaitQuietlyAsync(_heartbeatTask).ConfigureAwait(false);

        _acceptTask = null;
        _heartbeatTask = null;
        _cts?.Dispose();
        _cts = null;
        _listener = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private static async Task WaitQuietlyAsync(Task? task)
    {
        if (task is null) return;
        try { await task.ConfigureAwait(false); }
        catch { /* 关闭流程中的异常可忽略 */ }
    }

    // ── 房主本地操作（房主也是玩家，本地操作直接改状态并广播）────────────

    /// <summary>房主新增标记。</summary>
    public async Task<MutationResult> AddMarkerAsync(Marker marker, CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return ApplyAdd(marker, _options.HostPlayer.PlayerId);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>房主更新标记（可操作全部标记）。</summary>
    public async Task<MutationResult> UpdateMarkerAsync(Marker marker, CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return ApplyUpdate(marker?.Id ?? string.Empty, marker, _options.HostPlayer.PlayerId, true);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>房主删除标记（可操作全部标记）。</summary>
    public async Task<MutationResult> DeleteMarkerAsync(string id, CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return ApplyDelete(id, _options.HostPlayer.PlayerId, true);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    // ── 接受连接 ────────────────────────────────────────────────────────

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await _listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                if (cancellationToken.IsCancellationRequested) break;
                await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(tcp, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient tcp, CancellationToken serverToken)
    {
        var session = new ClientSession(tcp);
        try
        {
            tcp.NoDelay = true;

            // 1) 握手：首帧必须是 HELLO
            Envelope? first;
            using (var helloCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken))
            {
                helloCts.CancelAfter(_options.HelloTimeout);
                try
                {
                    first = await FrameCodec.ReadAsync(session.Stream, helloCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await SendFatalAsync(session, ErrorCode.HandshakeTimeout, "握手超时：未在限定时间内收到 HELLO")
                        .ConfigureAwait(false);
                    return;
                }
            }

            if (first is null) return; // 对端直接关闭
            if (first.Type != MessageType.Hello)
            {
                await SendFatalAsync(session, ErrorCode.ProtocolError, "首帧必须是 HELLO").ConfigureAwait(false);
                return;
            }

            HelloMessage hello;
            try
            {
                hello = MessagePackConfig.DeserializeBody<HelloMessage>(first);
            }
            catch (Exception ex)
            {
                await SendFatalAsync(session, ErrorCode.ProtocolError, $"HELLO 解析失败：{ex.Message}").ConfigureAwait(false);
                return;
            }

            if (hello.ProtocolVersion != ProtocolConstants.ProtocolVersion)
            {
                await SendFatalAsync(session, ErrorCode.ProtocolVersionMismatch,
                    $"版本不匹配：房主协议 v{ProtocolConstants.ProtocolVersion}，客户端 v{hello.ProtocolVersion}，请更新客户端")
                    .ConfigureAwait(false);
                return;
            }

            if (!string.Equals(hello.RoomPasswordHash ?? string.Empty, _options.RoomPasswordHash ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase))
            {
                await SendFatalAsync(session, ErrorCode.AuthFailed, "房间口令错误").ConfigureAwait(false);
                return;
            }

            session.Player = new PlayerInfo
            {
                PlayerId = Guid.NewGuid().ToString("N"),
                PlayerName = SanitizeName(hello.PlayerName),
                IsHost = false,
            };
            session.IsAuthenticated = true;
            session.StartWriter(serverToken);

            // 2) 注册并下发全量快照（与标记变更串行，保证快照与后续广播无缝衔接）
            await _mutationGate.WaitAsync(serverToken).ConfigureAwait(false);
            try
            {
                var snapshot = _markers.Snapshot();
                var players = new List<PlayerInfo> { _options.HostPlayer.Clone() };
                lock (_sessionsGate)
                {
                    foreach (var s in _sessions) players.Add(s.Player.Clone());
                }
                players.Add(session.Player.Clone());

                var welcome = new WelcomeMessage
                {
                    PlayerId = session.Player.PlayerId,
                    MapInfo = CurrentMap.Clone(),
                    Markers = snapshot.ToArray(),
                    Players = players.ToArray(),
                };
                session.TrySend(new Envelope(MessageType.Welcome, MessagePackConfig.Serialize(welcome)));

                lock (_sessionsGate) _sessions.Add(session);
            }
            finally
            {
                _mutationGate.Release();
            }

            PlayerJoined?.Invoke(this, new PlayerEventArgs(session.Player.Clone()));
            Broadcast(MessageType.PlayerJoined, new PlayerMessage { Player = session.Player.Clone() }, except: session);

            // 3) 读循环
            while (!serverToken.IsCancellationRequested && !session.Closed)
            {
                Envelope? envelope;
                try
                {
                    envelope = await FrameCodec.ReadAsync(session.Stream, session.Cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (envelope is null) break; // 客户端关闭连接
                session.Touch();

                await DispatchAsync(session, envelope, serverToken).ConfigureAwait(false);
            }
        }
        catch (ProtocolException pex)
        {
            await SendFatalAsync(session, pex.Code, pex.Message).ConfigureAwait(false);
            ClientError?.Invoke(this, new ConnectionEventArgs(pex.Message, false, pex.Code));
        }
        catch (Exception) when (session.Closed)
        {
            // 主动关闭导致的异常，忽略
        }
        catch (OperationCanceledException)
        {
            // 服务端停止
        }
        catch (Exception ex)
        {
            ClientError?.Invoke(this, new ConnectionEventArgs(ex.Message, false));
        }
        finally
        {
            await RemoveSessionAsync(session).ConfigureAwait(false);
        }
    }

    private async Task DispatchAsync(ClientSession session, Envelope envelope, CancellationToken cancellationToken)
    {
        switch (envelope.Type)
        {
            case MessageType.Ping:
                session.TrySend(new Envelope(MessageType.Pong, MessagePackConfig.Serialize(new PongMessage())));
                break;

            case MessageType.Pong:
                break;

            case MessageType.MapRequest:
                await HandleMapRequestAsync(session, envelope, cancellationToken).ConfigureAwait(false);
                break;

            case MessageType.MarkerAdd:
            case MessageType.MarkerUpdate:
            case MessageType.MarkerDelete:
                await HandleMutationAsync(session, envelope, cancellationToken).ConfigureAwait(false);
                break;

            default:
                session.TrySend(MakeError(ErrorCode.InvalidRequest, $"不支持的客户端消息类型：{envelope.Type}"));
                break;
        }
    }

    private async Task HandleMapRequestAsync(ClientSession session, Envelope envelope, CancellationToken cancellationToken)
    {
        MapRequestMessage request;
        try
        {
            request = MessagePackConfig.DeserializeBody<MapRequestMessage>(envelope);
        }
        catch (Exception ex)
        {
            session.TrySend(MakeError(ErrorCode.InvalidRequest, $"MAP_REQUEST 解析失败：{ex.Message}"));
            return;
        }

        if (!string.Equals(request.ImageHash, CurrentMap.ImageHash, StringComparison.OrdinalIgnoreCase))
        {
            session.TrySend(MakeError(ErrorCode.MapUnavailable, "地图哈希与当前房间不一致"));
            return;
        }

        var bytes = _options.ImageProvider?.Invoke(request.ImageHash);
        if (bytes is null || bytes.Length == 0)
        {
            session.TrySend(MakeError(ErrorCode.MapUnavailable, "房主端没有该地图图片"));
            return;
        }

        foreach (var chunk in ImageTransfer.CreateChunks(request.ImageHash, bytes))
        {
            var ok = session.TrySend(new Envelope(MessageType.MapChunk, MessagePackConfig.Serialize(chunk)));
            if (!ok)
            {
                await CloseSessionAsync(session, "发送队列已满", cancellationToken).ConfigureAwait(false);
                return;
            }
        }
    }

    private async Task HandleMutationAsync(ClientSession session, Envelope envelope, CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MutationResult result;
            try
            {
                switch (envelope.Type)
                {
                    case MessageType.MarkerAdd:
                    {
                        var msg = MessagePackConfig.DeserializeBody<MarkerMessage>(envelope);
                        result = ApplyAdd(msg.Marker, session.Player.PlayerId);
                        break;
                    }
                    case MessageType.MarkerUpdate:
                    {
                        var msg = MessagePackConfig.DeserializeBody<MarkerUpdateMessage>(envelope);
                        result = ApplyUpdate(msg.Id ?? msg.Marker?.Id ?? string.Empty, msg.Marker, session.Player.PlayerId, false);
                        break;
                    }
                    default:
                    {
                        var msg = MessagePackConfig.DeserializeBody<MarkerIdMessage>(envelope);
                        result = ApplyDelete(msg.Id, session.Player.PlayerId, false);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                session.TrySend(MakeError(ErrorCode.InvalidRequest, $"消息解析失败：{ex.Message}", envelope.Type));
                return;
            }

            switch (result.Outcome)
            {
                case MutationOutcome.PermissionDenied:
                    session.TrySend(MakeError(ErrorCode.PermissionDenied, "无权操作他人创建的标记", envelope.Type));
                    break;
                case MutationOutcome.Invalid:
                    session.TrySend(MakeError(ErrorCode.InvalidRequest, "标记内容不合法", envelope.Type));
                    break;
                case MutationOutcome.NotFound:
                    session.TrySend(MakeError(ErrorCode.InvalidRequest, "标记不存在", envelope.Type));
                    break;
                case MutationOutcome.Applied:
                case MutationOutcome.Ignored:
                default:
                    break;
            }
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    // ── 权威变更（调用方必须已持有 _mutationGate）───────────────────────

    private MutationResult ApplyAdd(Marker? marker, string actorId)
    {
        if (marker is null) return MutationResult.Invalid();
        var prepared = marker.Clone();
        if (string.IsNullOrWhiteSpace(prepared.Id)) prepared.Id = Guid.NewGuid().ToString("D");
        prepared.OwnerId = actorId;
        prepared.CreatedAt = prepared.CreatedAt > 0 ? prepared.CreatedAt : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var result = _markers.Add(prepared);
        if (result.Outcome == MutationOutcome.Applied && result.Marker is not null)
        {
            Broadcast(MessageType.MarkerAdded, new MarkerMessage { Marker = result.Marker });
            MarkerAdded?.Invoke(this, new MarkerEventArgs(result.Marker.Clone()));
        }
        return result;
    }

    private MutationResult ApplyUpdate(string id, Marker? marker, string actorId, bool actorIsHost)
    {
        var result = _markers.Update(id, marker, actorId, actorIsHost);
        if (result.Outcome == MutationOutcome.Applied && result.Marker is not null)
        {
            Broadcast(MessageType.MarkerUpdated, new MarkerMessage { Marker = result.Marker });
            MarkerUpdated?.Invoke(this, new MarkerEventArgs(result.Marker.Clone()));
        }
        return result;
    }

    private MutationResult ApplyDelete(string id, string actorId, bool actorIsHost)
    {
        var result = _markers.Delete(id, actorId, actorIsHost);
        if (result.Outcome == MutationOutcome.Applied)
        {
            Broadcast(MessageType.MarkerDeleted, new MarkerIdMessage { Id = id });
            MarkerDeleted?.Invoke(this, new MarkerDeletedEventArgs(id));
        }
        return result;
    }

    // ── 广播 ────────────────────────────────────────────────────────────

    private void Broadcast<TBody>(MessageType type, TBody body, ClientSession? except = null)
        => BroadcastEnvelope(new Envelope(type, MessagePackConfig.Serialize(body)), except);

    private void BroadcastEnvelope(Envelope envelope, ClientSession? except = null)
    {
        ClientSession[] snapshot;
        lock (_sessionsGate) snapshot = _sessions.ToArray();
        foreach (var s in snapshot)
        {
            if (except is not null && ReferenceEquals(except, s)) continue;
            s.TrySend(envelope);
        }
    }

    private static Envelope MakeError(ErrorCode code, string message, MessageType? related = null)
        => new(MessageType.Error, MessagePackConfig.Serialize(new ErrorMessage
        {
            Code = code,
            Message = message,
            RelatedType = related,
        }));

    private static async Task SendFatalAsync(ClientSession session, ErrorCode code, string message)
    {
        try
        {
            await FrameCodec.WriteAsync(session.Stream, MakeError(code, message), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch { /* 对端可能已断开 */ }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task CloseSessionAsync(ClientSession session, string reason, CancellationToken cancellationToken)
    {
        await session.DisposeAsync().ConfigureAwait(false);
        ClientDisconnected?.Invoke(this, new ConnectionEventArgs(reason, false));
    }

    private async Task RemoveSessionAsync(ClientSession session)
    {
        var removed = false;
        lock (_sessionsGate) removed = _sessions.Remove(session);

        var playerId = session.Player.PlayerId;
        await session.DisposeAsync().ConfigureAwait(false);

        if (removed && session.IsAuthenticated)
        {
            Broadcast(MessageType.PlayerLeft, new PlayerLeftMessage { PlayerId = playerId });
            PlayerLeft?.Invoke(this, new PlayerLeftEventArgs(playerId));
        }
    }

    // ── 心跳 ────────────────────────────────────────────────────────────

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        var ping = new Envelope(MessageType.Ping, MessagePackConfig.Serialize(new PingMessage()));
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.HeartbeatInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            ClientSession[] snapshot;
            lock (_sessionsGate) snapshot = _sessions.ToArray();

            var now = DateTime.UtcNow.Ticks;
            foreach (var s in snapshot)
            {
                var idle = TimeSpan.FromTicks(Math.Max(0, now - s.LastActivityTicks));
                if (idle > _options.HeartbeatTimeout)
                {
                    _ = CloseSessionAsync(s, "心跳超时，已断开该客户端", cancellationToken);
                    continue;
                }
                s.TrySend(ping);
            }
        }
    }

    private static string SanitizeName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) return "玩家";
        return trimmed.Length <= 24 ? trimmed : trimmed[..24];
    }

    // ── 会话 ────────────────────────────────────────────────────────────

    private sealed class ClientSession : IAsyncDisposable
    {
        private readonly TcpClient _tcp;
        private readonly Channel<Envelope> _outbox;
        private long _lastActivityTicks = DateTime.UtcNow.Ticks;
        private volatile bool _closed;

        public ClientSession(TcpClient tcp)
        {
            _tcp = tcp;
            Stream = tcp.GetStream();
            _outbox = Channel.CreateBounded<Envelope>(new BoundedChannelOptions(8192)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
        }

        public NetworkStream Stream { get; }

        public PlayerInfo Player { get; set; } = new();

        public volatile bool IsAuthenticated;

        public bool Closed => _closed;

        public CancellationTokenSource Cts { get; } = new();

        public long LastActivityTicks => Interlocked.Read(ref _lastActivityTicks);

        public void Touch() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

        public void StartWriter(CancellationToken serverToken)
        {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(serverToken, Cts.Token);
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await _outbox.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false))
                    {
                        while (_outbox.Reader.TryRead(out var envelope))
                        {
                            await FrameCodec.WriteAsync(Stream, envelope, linked.Token).ConfigureAwait(false);
                        }
                    }
                }
                catch
                {
                    // 写失败意味着连接已断，直接关闭
                    _closed = true;
                    try { Cts.Cancel(); } catch { /* ignore */ }
                }
            }, CancellationToken.None);
        }

        public bool TrySend(Envelope envelope)
        {
            if (_closed) return false;
            if (_outbox.Writer.TryWrite(envelope)) return true;
            _closed = true;
            try { Cts.Cancel(); } catch { /* ignore */ }
            return false;
        }

        public async ValueTask DisposeAsync()
        {
            if (_closed && _disposed) return;
            _closed = true;
            _disposed = true;
            try { _outbox.Writer.TryComplete(); } catch { /* ignore */ }
            try { Cts.Cancel(); } catch { /* ignore */ }
            try { _tcp.Close(); } catch { /* ignore */ }
            try { _tcp.Dispose(); } catch { /* ignore */ }
            Cts.Dispose();
        }

        private bool _disposed;
    }
}
