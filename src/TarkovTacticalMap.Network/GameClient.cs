using System.Collections.Concurrent;
using System.Net.Sockets;
using TarkovTacticalMap.Core.Models;
using TarkovTacticalMap.Core.Protocol;
using TarkovTacticalMap.Core.Serialization;
using TarkovTacticalMap.Core.Utils;

namespace TarkovTacticalMap.Network;

/// <summary>客户端配置。</summary>
public sealed class GameClientOptions
{
    /// <summary>房主虚拟局域网 IP。</summary>
    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = ProtocolConstants.DefaultPort;

    /// <summary>房间口令的 SHA-256 小写 hex（用 RoomPassword.Hash 生成）。</summary>
    public string RoomPasswordHash { get; set; } = string.Empty;

    public string PlayerName { get; set; } = "玩家";

    /// <summary>断线后是否自动重连（指数退避）。</summary>
    public bool EnableAutoReconnect { get; set; } = true;

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan HelloTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan HeartbeatInterval { get; set; } = ProtocolConstants.HeartbeatInterval;

    public TimeSpan HeartbeatTimeout { get; set; } = ProtocolConstants.HeartbeatTimeout;

    public TimeSpan InitialReconnectDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxReconnectDelay { get; set; } = ProtocolConstants.MaxReconnectDelay;

    public TimeSpan MapRequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>连接成功（收到 WELCOME）事件参数。</summary>
public sealed class WelcomeEventArgs : EventArgs
{
    public WelcomeEventArgs(WelcomeMessage welcome, bool isReconnect)
    {
        Welcome = welcome;
        IsReconnect = isReconnect;
    }

    public WelcomeMessage Welcome { get; }

    /// <summary>是否为断线重连（重连后上层需用全量快照重建本地状态）。</summary>
    public bool IsReconnect { get; }
}

/// <summary>房主返回的错误事件参数。</summary>
public sealed class ClientErrorEventArgs : EventArgs
{
    public ClientErrorEventArgs(ErrorCode code, string message)
    {
        Code = code;
        Message = message;
    }

    public ErrorCode Code { get; }

    public string Message { get; }
}

/// <summary>握手阶段的致命错误（不重试）。</summary>
internal sealed class FatalClientException : Exception
{
    public FatalClientException(ErrorCode code, string message) : base(message) => Code = code;

    public ErrorCode Code { get; }
}

/// <summary>
/// 客户端：TcpClient + 长连接读循环 + 心跳 + 指数退避重连。
/// 所有网络 I/O 均为 async，UI 层通过事件在 Dispatcher 上更新。
/// </summary>
public sealed class GameClient : IAsyncDisposable
{
    private readonly GameClientOptions _options;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<byte[]?>> _pendingMaps = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private long _lastReceivedTicks = DateTime.UtcNow.Ticks;
    private string? _lastDisconnectReason;
    private volatile bool _stopRequested;

    public GameClient(GameClientOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    // ── 事件 ────────────────────────────────────────────────────────────

    public event EventHandler<WelcomeEventArgs>? Connected;
    public event EventHandler<ConnectionEventArgs>? Disconnected;
    public event EventHandler<ClientErrorEventArgs>? Error;
    public event EventHandler<MarkerEventArgs>? MarkerAdded;
    public event EventHandler<MarkerEventArgs>? MarkerUpdated;
    public event EventHandler<MarkerDeletedEventArgs>? MarkerDeleted;
    public event EventHandler<PlayerEventArgs>? PlayerJoined;
    public event EventHandler<PlayerLeftEventArgs>? PlayerLeft;

    // ── 状态 ────────────────────────────────────────────────────────────

    public ConnectionState State { get; private set; } = ConnectionState.Idle;

    /// <summary>连接成功后可用的玩家 ID。</summary>
    public string? PlayerId { get; private set; }

    public bool IsConnected => State == ConnectionState.Connected && _stream is not null;

    /// <summary>最近一次 WELCOME 的服务器时间无关快照（可选读取）。</summary>
    public WelcomeMessage? LastWelcome { get; private set; }

    // ── 启停 ────────────────────────────────────────────────────────────

    /// <summary>启动连接循环（立即开始连接，失败按指数退避重试）。</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_runTask is not null) throw new InvalidOperationException("客户端已启动");
            _stopRequested = false;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _runTask = RunAsync(_cts.Token);
        }
        return Task.CompletedTask;
    }

    /// <summary>停止并断开连接（不会触发重连）。</summary>
    public async Task StopAsync()
    {
        _stopRequested = true;
        CancellationTokenSource? cts;
        Task? run;
        lock (_gate)
        {
            cts = _cts;
            run = _runTask;
            _runTask = null;
        }

        try { cts?.Cancel(); } catch { /* ignore */ }
        AbortConnection("已主动断开");
        if (run is not null)
        {
            try { await run.ConfigureAwait(false); } catch { /* ignore */ }
        }
        cts?.Dispose();
        _cts = null;
        SetState(ConnectionState.Stopped);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    // ── 发送 ────────────────────────────────────────────────────────────

    /// <summary>新增标记请求。</summary>
    public Task<bool> SendAddAsync(Marker marker, CancellationToken cancellationToken = default)
        => SendEnvelopeAsync(new Envelope(MessageType.MarkerAdd,
            MessagePackConfig.Serialize(new MarkerMessage { Marker = marker.Clone() })), cancellationToken);

    /// <summary>全量更新标记请求。</summary>
    public Task<bool> SendUpdateAsync(Marker marker, CancellationToken cancellationToken = default)
        => SendEnvelopeAsync(new Envelope(MessageType.MarkerUpdate,
            MessagePackConfig.Serialize(new MarkerUpdateMessage { Id = marker.Id, Marker = marker.Clone() })), cancellationToken);

    /// <summary>删除标记请求。</summary>
    public Task<bool> SendDeleteAsync(string id, CancellationToken cancellationToken = default)
        => SendEnvelopeAsync(new Envelope(MessageType.MarkerDelete,
            MessagePackConfig.Serialize(new MarkerIdMessage { Id = id })), cancellationToken);

    /// <summary>
    /// 请求地图图片：发送 MAP_REQUEST，等待分块收齐并校验 SHA-256。失败自动重试一次。
    /// 返回 null 表示获取失败（超时 / 房主没有该图片）。
    /// </summary>
    public async Task<byte[]?> RequestMapAsync(string imageHash, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var tcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingMaps[imageHash] = tcs;
            try
            {
                var sent = await SendEnvelopeAsync(new Envelope(MessageType.MapRequest,
                        MessagePackConfig.Serialize(new MapRequestMessage { ImageHash = imageHash })), cancellationToken)
                    .ConfigureAwait(false);
                if (!sent) continue;

                try
                {
                    return await tcs.Task.WaitAsync(_options.MapRequestTimeout, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    continue; // 重新请求
                }
            }
            finally
            {
                _pendingMaps.TryRemove(imageHash, out _);
            }
        }
        return null;
    }

    private async Task<bool> SendEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var stream = _stream;
        if (stream is null || State != ConnectionState.Connected) return false;

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FrameCodec.WriteAsync(stream, envelope, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            AbortConnection("发送失败，连接已断开");
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ── 连接循环 ────────────────────────────────────────────────────────

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested && !_stopRequested)
        {
            var isReconnect = attempt > 0;
            try
            {
                SetState(isReconnect ? ConnectionState.Reconnecting : ConnectionState.Connecting);
                var welcome = await ConnectOnceAsync(cancellationToken).ConfigureAwait(false);
                LastWelcome = welcome;
                PlayerId = welcome.PlayerId;
                attempt = 0;
                SetState(ConnectionState.Connected);

                Connected?.Invoke(this, new WelcomeEventArgs(welcome, isReconnect));

                await ReceiveLoopAsync(cancellationToken).ConfigureAwait(false);

                _lastDisconnectReason ??= "与房主的连接已断开";
                RaiseDisconnected(_lastDisconnectReason, ErrorCode.None);
            }
            catch (FatalClientException fatal)
            {
                SetState(ConnectionState.Faulted);
                Error?.Invoke(this, new ClientErrorEventArgs(fatal.Code, fatal.Message));
                Disconnected?.Invoke(this, new ConnectionEventArgs(fatal.Message, false, fatal.Code));
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _stopRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                RaiseDisconnected(_lastDisconnectReason ?? ex.Message, ErrorCode.None);
            }
            finally
            {
                CloseConnection();
            }

            if (_stopRequested || cancellationToken.IsCancellationRequested) break;

            if (!_options.EnableAutoReconnect || _fatalStop)
            {
                SetState(_fatalStop ? ConnectionState.Faulted : ConnectionState.Stopped);
                return;
            }

            attempt++;
            var delay = ComputeBackoff(attempt);
            SetState(ConnectionState.Reconnecting);
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            _lastDisconnectReason = null;
        }

        if (!_stopRequested) SetState(ConnectionState.Stopped);
    }

    private bool _fatalStop;

    /// <summary>1s、2s、4s… 上限 30s。</summary>
    private TimeSpan ComputeBackoff(int attempt)
    {
        var seconds = _options.InitialReconnectDelay.TotalSeconds * Math.Pow(2, Math.Clamp(attempt - 1, 0, 10));
        var capped = Math.Min(seconds, _options.MaxReconnectDelay.TotalSeconds);
        return TimeSpan.FromSeconds(capped);
    }

    private async Task<WelcomeMessage> ConnectOnceAsync(CancellationToken cancellationToken)
    {
        var tcp = new TcpClient();
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connectCts.CancelAfter(_options.ConnectTimeout);
            try
            {
                await tcp.ConnectAsync(_options.Host, _options.Port, connectCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                tcp.Dispose();
                throw new IOException($"连接 {_options.Host}:{_options.Port} 超时");
            }
            catch
            {
                tcp.Dispose();
                throw;
            }
        }

        tcp.NoDelay = true;
        _tcp = tcp;
        var stream = tcp.GetStream();
        _stream = stream;
        Interlocked.Exchange(ref _lastReceivedTicks, DateTime.UtcNow.Ticks);

        // HELLO
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FrameCodec.WriteAsync(stream, new Envelope(MessageType.Hello, MessagePackConfig.Serialize(new HelloMessage
            {
                ProtocolVersion = ProtocolConstants.ProtocolVersion,
                RoomPasswordHash = _options.RoomPasswordHash ?? string.Empty,
                PlayerName = _options.PlayerName ?? string.Empty,
            })), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        using var helloCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        helloCts.CancelAfter(_options.HelloTimeout);
        while (true)
        {
            Envelope? envelope;
            try
            {
                envelope = await FrameCodec.ReadAsync(stream, helloCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException("等待 WELCOME 超时");
            }
            catch (ProtocolException pex)
            {
                throw new FatalClientException(pex.Code, pex.Message);
            }

            if (envelope is null) throw new IOException("房主在握手阶段关闭了连接");

            switch (envelope.Type)
            {
                case MessageType.Welcome:
                {
                    WelcomeMessage welcome;
                    try
                    {
                        welcome = MessagePackConfig.DeserializeBody<WelcomeMessage>(envelope);
                    }
                    catch (Exception ex)
                    {
                        throw new FatalClientException(ErrorCode.ProtocolError, $"WELCOME 解析失败：{ex.Message}");
                    }
                    welcome.MapInfo ??= new MapInfo();
                    welcome.MapInfo.ClampGrid();
                    return welcome;
                }
                case MessageType.Error:
                {
                    var error = SafeDeserialize<ErrorMessage>(envelope) ??
                                new ErrorMessage { Code = ErrorCode.ProtocolError, Message = "房主返回未知错误" };
                    throw new FatalClientException(error.Code, error.Message);
                }
                case MessageType.Ping:
                {
                    await TryWriteSafeAsync(new Envelope(MessageType.Pong, MessagePackConfig.Serialize(new PongMessage())),
                        cancellationToken).ConfigureAwait(false);
                    break;
                }
                default:
                    break; // 握手阶段忽略其它消息
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new IOException("连接已失效");
        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pingTask = PingLoopAsync(loopCts.Token);

        try
        {
            Interlocked.Exchange(ref _lastReceivedTicks, DateTime.UtcNow.Ticks);
            while (!loopCts.IsCancellationRequested)
            {
                Envelope? envelope;
                try
                {
                    envelope = await FrameCodec.ReadAsync(stream, loopCts.Token).ConfigureAwait(false);
                }
                catch (ProtocolException pex)
                {
                    _lastDisconnectReason = $"协议错误：{pex.Message}";
                    return;
                }

                if (envelope is null)
                {
                    _lastDisconnectReason ??= "房主关闭了连接";
                    return;
                }

                Interlocked.Exchange(ref _lastReceivedTicks, DateTime.UtcNow.Ticks);
                if (_fatalStop) return;

                await DispatchAsync(envelope, loopCts.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            try { loopCts.Cancel(); } catch { /* ignore */ }
            try { await pingTask.ConfigureAwait(false); } catch { /* ignore */ }
        }
    }

    private async Task DispatchAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        switch (envelope.Type)
        {
            case MessageType.Ping:
                await TryWriteSafeAsync(new Envelope(MessageType.Pong, MessagePackConfig.Serialize(new PongMessage())),
                    cancellationToken).ConfigureAwait(false);
                break;

            case MessageType.Pong:
                break;

            case MessageType.MarkerAdded:
            {
                var msg = SafeDeserialize<MarkerMessage>(envelope);
                if (msg?.Marker is not null) MarkerAdded?.Invoke(this, new MarkerEventArgs(msg.Marker));
                break;
            }

            case MessageType.MarkerUpdated:
            {
                var msg = SafeDeserialize<MarkerMessage>(envelope);
                if (msg?.Marker is not null) MarkerUpdated?.Invoke(this, new MarkerEventArgs(msg.Marker));
                break;
            }

            case MessageType.MarkerDeleted:
            {
                var msg = SafeDeserialize<MarkerIdMessage>(envelope);
                if (msg?.Id is not null) MarkerDeleted?.Invoke(this, new MarkerDeletedEventArgs(msg.Id));
                break;
            }

            case MessageType.PlayerJoined:
            {
                var msg = SafeDeserialize<PlayerMessage>(envelope);
                if (msg?.Player is not null) PlayerJoined?.Invoke(this, new PlayerEventArgs(msg.Player));
                break;
            }

            case MessageType.PlayerLeft:
            {
                var msg = SafeDeserialize<PlayerLeftMessage>(envelope);
                if (msg?.PlayerId is not null) PlayerLeft?.Invoke(this, new PlayerLeftEventArgs(msg.PlayerId));
                break;
            }

            case MessageType.MapChunk:
            {
                var chunk = SafeDeserialize<MapChunkMessage>(envelope);
                if (chunk is not null) HandleMapChunk(chunk);
                break;
            }

            case MessageType.Error:
            {
                var error = SafeDeserialize<ErrorMessage>(envelope);
                if (error is null) break;
                Error?.Invoke(this, new ClientErrorEventArgs(error.Code, error.Message));
                if (error.Code == ErrorCode.ServerClosed)
                {
                    _fatalStop = true;
                    _lastDisconnectReason = error.Message;
                    AbortConnection(error.Message);
                }
                break;
            }

            default:
                break; // 未知消息类型：忽略（向前兼容）
        }
    }

    private void HandleMapChunk(MapChunkMessage chunk)
    {
        if (!_pendingMaps.TryGetValue(chunk.ImageHash, out var tcs)) return; // 没有等待中的请求，忽略

        ImageTransfer.MapAssembler? assembler = null;
        lock (_mapAssemblersGate)
        {
            if (!_mapAssemblers.TryGetValue(chunk.ImageHash, out assembler))
            {
                assembler = new ImageTransfer.MapAssembler(chunk.ImageHash);
                _mapAssemblers[chunk.ImageHash] = assembler;
            }
        }

        bool complete;
        try
        {
            complete = assembler.Add(chunk);
        }
        catch (Exception ex)
        {
            lock (_mapAssemblersGate) _mapAssemblers.Remove(chunk.ImageHash);
            tcs.TrySetResult(null);
            _ = ex;
            return;
        }

        if (!complete) return;

        lock (_mapAssemblersGate) _mapAssemblers.Remove(chunk.ImageHash);
        var bytes = assembler.TryAssemble();
        tcs.TrySetResult(bytes);
    }

    private readonly Dictionary<string, ImageTransfer.MapAssembler> _mapAssemblers = new(StringComparer.Ordinal);
    private readonly object _mapAssemblersGate = new();

    private async Task PingLoopAsync(CancellationToken cancellationToken)
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
                return;
            }

            var idle = TimeSpan.FromTicks(Math.Max(0, DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastReceivedTicks)));
            if (idle > _options.HeartbeatTimeout)
            {
                _lastDisconnectReason = "心跳超时：与房主的连接已断开";
                AbortConnection(_lastDisconnectReason);
                return;
            }

            if (!await TryWriteSafeAsync(ping, cancellationToken).ConfigureAwait(false)) return;
        }
    }

    private async Task<bool> TryWriteSafeAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var stream = _stream;
        if (stream is null) return false;
        try
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await FrameCodec.WriteAsync(stream, envelope, cancellationToken).ConfigureAwait(false);
                return true;
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch
        {
            return false;
        }
    }

    private void RaiseDisconnected(string message, ErrorCode code)
    {
        var willRetry = _options.EnableAutoReconnect && !_stopRequested && !_fatalStop;
        Disconnected?.Invoke(this, new ConnectionEventArgs(message, willRetry, code));
    }

    private void CloseConnection()
    {
        var tcp = _tcp;
        _tcp = null;
        _stream = null;
        if (tcp is null) return;
        try { tcp.Close(); } catch { /* ignore */ }
        try { tcp.Dispose(); } catch { /* ignore */ }
    }

    private void AbortConnection(string reason)
    {
        _lastDisconnectReason = reason;
        CloseConnection();
    }

    private void SetState(ConnectionState state) => State = state;

    private static T? SafeDeserialize<T>(Envelope envelope) where T : class
    {
        try
        {
            return MessagePackConfig.DeserializeBody<T>(envelope);
        }
        catch
        {
            return null;
        }
    }
}
