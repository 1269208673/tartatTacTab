using TarkovTacticalMap.Core.Models;
using TarkovTacticalMap.Core.Protocol;
using TarkovTacticalMap.Core.State;
using TarkovTacticalMap.Core.Utils;
using TarkovTacticalMap.Network;

namespace TarkovTacticalMap.App.Services;

/// <summary>会话运行模式。</summary>
public enum SessionMode
{
    /// <summary>仅本地编辑（不联网，用于 M1 自测 / 单人使用）。</summary>
    Offline = 0,

    /// <summary>房主：本机监听 TCP，本机是权威。</summary>
    Host = 1,

    /// <summary>客户端：连接房主。</summary>
    Client = 2,
}

/// <summary>建立会话所需的配置。</summary>
public sealed class SessionConfig
{
    public string PlayerName { get; set; } = "玩家";

    /// <summary>地图图片字节（房主 / 本地模式必填；客户端会通过协议获取）。</summary>
    public byte[]? ImageBytes { get; set; }

    public int ImageWidth { get; set; }

    public int ImageHeight { get; set; }

    public int GridCols { get; set; } = MapInfo.DefaultGridCols;

    public int GridRows { get; set; } = MapInfo.DefaultGridRows;

    /// <summary>房主监听端口。</summary>
    public int Port { get; set; } = ProtocolConstants.DefaultPort;

    /// <summary>房间口令明文（发送时转为 SHA-256）。</summary>
    public string RoomPassword { get; set; } = string.Empty;

    /// <summary>房主虚拟局域网 IP。</summary>
    public string HostIp { get; set; } = "127.0.0.1";

    /// <summary>地图图片来源路径（仅用于设置记忆）。</summary>
    public string? ImageSourcePath { get; set; }
}

/// <summary>
/// 会话门面：把「房主服务端 / 客户端 / 纯本地」三种模式收敛成同一套状态与事件，
/// 上层（ViewModel / 画布）只与它交互，不直接碰网络层。
///
/// 状态真源约定：
///   - 客户端：所有变更先发给房主，收到房主权威广播后才更新本地状态；
///   - 房主 / 本地：变更立即生效并（房主）广播。
/// </summary>
public sealed class TacticalSession : IAsyncDisposable
{
    private readonly ImageCache _cache;
    private readonly MarkerStore _markers = new();
    private readonly string _roomPasswordHash;
    private HostServer? _server;
    private GameClient? _client;
    private TaskCompletionSource<bool>? _joinGate;
    private CancellationTokenSource? _imageCts;
    private string? _lastError;
    private volatile bool _disposed;

    private TacticalSession(SessionMode mode, SessionConfig config, ImageCache cache)
    {
        Mode = mode;
        Config = config;
        _cache = cache;
        PlayerName = string.IsNullOrWhiteSpace(config.PlayerName) ? "玩家" : config.PlayerName.Trim();
        PlayerId = Guid.NewGuid().ToString("N");
        _roomPasswordHash = RoomPassword.Hash(config.RoomPassword);
        Players = Array.Empty<PlayerInfo>();
        Map = new MapInfo
        {
            MapId = Guid.NewGuid().ToString("D"),
            GridCols = Math.Clamp(config.GridCols, MapInfo.MinGrid, MapInfo.MaxGrid),
            GridRows = Math.Clamp(config.GridRows, MapInfo.MinGrid, MapInfo.MaxGrid),
            GridVisible = true,
        };
    }

    // ── 事件（上层订阅后抛回 UI 线程）──────────────────────────────────

    public event EventHandler<MarkerEventArgs>? MarkerAdded;
    public event EventHandler<MarkerEventArgs>? MarkerUpdated;
    public event EventHandler<MarkerDeletedEventArgs>? MarkerDeleted;

    /// <summary>全量快照被替换（首次连接 / 重连后）。</summary>
    public event EventHandler? MarkersReplaced;

    /// <summary>地图元数据变化（首次连接、网格设置变化）。</summary>
    public event EventHandler? MapChanged;

    /// <summary>地图图片字节就绪（客户端从房主获取或命中缓存）。</summary>
    public event EventHandler? ImageChanged;

    public event EventHandler? PlayersChanged;

    /// <summary>连接状态变化（文案给状态栏用）。</summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>需要提示用户的非致命信息。</summary>
    public event EventHandler<string>? Notice;

    /// <summary>需要提示用户的错误。</summary>
    public event EventHandler<string>? Error;

    // ── 状态 ────────────────────────────────────────────────────────────

    public SessionMode Mode { get; }

    public SessionConfig Config { get; }

    public string PlayerId { get; private set; }

    public string PlayerName { get; }

    public bool IsHost => Mode == SessionMode.Host;

    public MapInfo Map { get; private set; }

    public byte[]? ImageBytes { get; private set; }

    public bool HasImage => ImageBytes is { Length: > 0 };

    public ConnectionState ConnectionState { get; private set; } = ConnectionState.Idle;

    public IReadOnlyList<PlayerInfo> Players { get; private set; }

    /// <summary>房主实际监听端口。</summary>
    public int Port { get; private set; }

    public HostServer? Server => _server;

    public GameClient? Client => _client;

    public IReadOnlyList<Marker> MarkerSnapshot() => _markers.Snapshot();

    public Marker? GetMarker(string id) => _markers.Get(id);

    public string ModeLabel => Mode switch
    {
        SessionMode.Host => "房主",
        SessionMode.Client => "客户端",
        _ => "本地",
    };

    // ── 创建 ────────────────────────────────────────────────────────────

    /// <summary>创建房主会话（监听 0.0.0.0:端口）。</summary>
    public static async Task<TacticalSession> CreateHostAsync(SessionConfig config, ImageCache cache)
    {
        var session = new TacticalSession(SessionMode.Host, config, cache);
        try
        {
            await session.StartHostAsyncCore().ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>创建纯本地会话（不联网）。</summary>
    public static Task<TacticalSession> CreateOfflineAsync(SessionConfig config, ImageCache cache)
    {
        var session = new TacticalSession(SessionMode.Offline, config, cache);
        session.InitializeLocalMap();
        session.ConnectionState = ConnectionState.Connected;
        session.Players = new List<PlayerInfo>
        {
            new() { PlayerId = session.PlayerId, PlayerName = session.PlayerName, IsHost = true },
        };
        return Task.FromResult(session);
    }

    /// <summary>连接房主；认证失败 / 超时会抛异常。</summary>
    public static async Task<TacticalSession> JoinAsync(SessionConfig config, ImageCache cache,
        CancellationToken cancellationToken = default)
    {
        var session = new TacticalSession(SessionMode.Client, config, cache);
        try
        {
            await session.StartClientAsyncCore(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void InitializeLocalMap()
    {
        var bytes = Config.ImageBytes;
        if (bytes is null || bytes.Length == 0)
        {
            bytes = Rendering.BlankMapFactory.CreatePng();
            Config.ImageWidth = Rendering.BlankMapFactory.DefaultWidth;
            Config.ImageHeight = Rendering.BlankMapFactory.DefaultHeight;
        }

        var width = Config.ImageWidth > 0 ? Config.ImageWidth : 1600;
        var height = Config.ImageHeight > 0 ? Config.ImageHeight : 1200;
        var hash = HashUtil.Sha256Hex(bytes);

        Map = new MapInfo
        {
            MapId = Map.MapId,
            ImageHash = hash,
            ImageWidth = width,
            ImageHeight = height,
            GridCols = Map.GridCols,
            GridRows = Map.GridRows,
            GridVisible = true,
        };
        ImageBytes = bytes;
        _cache.Save(hash, bytes);
    }

    private async Task StartHostAsyncCore()
    {
        InitializeLocalMap();

        var hostPlayer = new PlayerInfo { PlayerId = PlayerId, PlayerName = PlayerName, IsHost = true };
        _server = new HostServer(new HostServerOptions
        {
            Port = Config.Port,
            RoomPasswordHash = _roomPasswordHash,
            HostPlayer = hostPlayer,
            Map = Map.Clone(),
            ImageProvider = hash => _cache.TryLoad(hash, out var bytes) ? bytes : null,
        });

        _server.MarkerAdded += (_, e) => ApplyRemoteAdd(e.Marker);
        _server.MarkerUpdated += (_, e) => ApplyRemoteUpdate(e.Marker);
        _server.MarkerDeleted += (_, e) => ApplyRemoteDelete(e.Id);
        _server.PlayerJoined += (_, e) =>
        {
            RefreshPlayers();
            Notice?.Invoke(this, $"{e.Player.PlayerName} 加入了房间");
        };
        _server.PlayerLeft += (_, _) => RefreshPlayers();
        _server.ClientError += (_, e) => Notice?.Invoke(this, $"客户端问题：{e.Reason}");
        _server.ClientDisconnected += (_, e) => Notice?.Invoke(this, e.Reason ?? "客户端断开");

        await _server.StartAsync().ConfigureAwait(false);
        Port = _server.Port;
        ConnectionState = ConnectionState.Connected;
        RefreshPlayers();
        StatusChanged?.Invoke(this, $"房间已开启（端口 {Port}）");
        MapChanged?.Invoke(this, EventArgs.Empty);
        ImageChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task StartClientAsyncCore(CancellationToken cancellationToken)
    {
        _joinGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _client = new GameClient(new GameClientOptions
        {
            Host = Config.HostIp,
            Port = Config.Port,
            RoomPasswordHash = _roomPasswordHash,
            PlayerName = PlayerName,
            EnableAutoReconnect = true,
        });

        _client.Connected += OnClientConnected;
        _client.Disconnected += OnClientDisconnected;
        _client.Error += OnClientError;
        _client.MarkerAdded += (_, e) => ApplyRemoteAdd(e.Marker);
        _client.MarkerUpdated += (_, e) => ApplyRemoteUpdate(e.Marker);
        _client.MarkerDeleted += (_, e) => ApplyRemoteDelete(e.Id);
        _client.PlayerJoined += (_, e) =>
        {
            var list = Players.ToList();
            if (list.All(p => !string.Equals(p.PlayerId, e.Player.PlayerId, StringComparison.Ordinal)))
            {
                list.Add(e.Player);
                Players = list;
                PlayersChanged?.Invoke(this, EventArgs.Empty);
                Notice?.Invoke(this, $"{e.Player.PlayerName} 加入了房间");
            }
        };
        _client.PlayerLeft += (_, e) =>
        {
            Players = Players.Where(p => !string.Equals(p.PlayerId, e.PlayerId, StringComparison.Ordinal)).ToList();
            PlayersChanged?.Invoke(this, EventArgs.Empty);
        };

        ConnectionState = ConnectionState.Connecting;
        StatusChanged?.Invoke(this, "正在连接房主…");

        await _client.StartAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var joined = await _joinGate.Task.WaitAsync(TimeSpan.FromSeconds(25), cancellationToken).ConfigureAwait(false);
            if (!joined) throw new InvalidOperationException(_lastError ?? "无法加入房间");
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("连接房主超时，请检查 IP / 端口是否正确，以及房主是否已放行防火墙");
        }
    }

    // ── 客户端事件 ──────────────────────────────────────────────────────

    private void OnClientConnected(object? sender, WelcomeEventArgs e)
    {
        var welcome = e.Welcome;
        PlayerId = welcome.PlayerId; // 客户端身份由房主分配
        Map = welcome.MapInfo.Clone();
        _markers.ReplaceAll(welcome.Markers);
        Players = welcome.Players.Length > 0
            ? welcome.Players
            : new List<PlayerInfo> { new() { PlayerId = PlayerId, PlayerName = PlayerName, IsHost = false } };

        ConnectionState = e.IsReconnect ? ConnectionState.Connected : ConnectionState.Connected;
        MapChanged?.Invoke(this, EventArgs.Empty);
        MarkersReplaced?.Invoke(this, EventArgs.Empty);
        PlayersChanged?.Invoke(this, EventArgs.Empty);
        StatusChanged?.Invoke(this, e.IsReconnect
            ? "已重新连接房主，状态已用全量快照重建"
            : "已连接房主");

        _joinGate?.TrySetResult(true);

        // 地图图片：先查缓存，未命中再向房主请求
        BeginImageSync(Map.ImageHash);
    }

    private void OnClientDisconnected(object? sender, ConnectionEventArgs e)
    {
        ConnectionState = e.WillRetry ? ConnectionState.Reconnecting : ConnectionState.Faulted;
        var message = e.WillRetry
            ? $"{e.Reason}，正在重连…"
            : e.Reason ?? "与房主的连接已断开";
        StatusChanged?.Invoke(this, message);

        if (!e.WillRetry)
        {
            _lastError = message;
            _joinGate?.TrySetResult(false);
        }
    }

    private void OnClientError(object? sender, ClientErrorEventArgs e)
    {
        var text = $"房主返回错误（{DescribeError(e.Code)}）：{e.Message}";
        _lastError = text;
        if (ConnectionState is ConnectionState.Faulted)
        {
            Error?.Invoke(this, text);
        }
        else
        {
            Notice?.Invoke(this, text);
        }
    }

    public static string DescribeError(ErrorCode code) => code switch
    {
        ErrorCode.AuthFailed => "口令错误",
        ErrorCode.ProtocolVersionMismatch => "版本不匹配",
        ErrorCode.PermissionDenied => "无权限",
        ErrorCode.InvalidRequest => "请求不合法",
        ErrorCode.ProtocolError => "协议错误",
        ErrorCode.MapUnavailable => "地图不可用",
        ErrorCode.ServerClosed => "房间已关闭",
        ErrorCode.HandshakeTimeout => "握手超时",
        _ => code.ToString(),
    };

    private void BeginImageSync(string imageHash)
    {
        if (string.IsNullOrEmpty(imageHash)) return;

        if (_cache.TryLoad(imageHash, out var cached) && cached is not null && cached.Length > 0)
        {
            SetImage(cached);
            return;
        }

        try { _imageCts?.Cancel(); } catch { /* ignore */ }
        _imageCts = new CancellationTokenSource();
        var token = _imageCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                if (_client is null) return;
                var bytes = await _client.RequestMapAsync(imageHash, token).ConfigureAwait(false);
                if (bytes is null || bytes.Length == 0)
                {
                    Notice?.Invoke(this, "地图图片获取失败，请稍后重试或重新加入房间");
                    return;
                }
                _cache.Save(imageHash, bytes);
                SetImage(bytes);
            }
            catch (OperationCanceledException)
            {
                // 会话已关闭
            }
            catch (Exception ex)
            {
                Notice?.Invoke(this, $"地图图片获取失败：{ex.Message}");
            }
        }, CancellationToken.None);
    }

    private void SetImage(byte[] bytes)
    {
        ImageBytes = bytes;
        ImageChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── 状态更新（网络层事件 → 本地状态真源）────────────────────────────

    private void ApplyRemoteAdd(Marker marker)
    {
        _markers.Add(marker);
        MarkerAdded?.Invoke(this, new MarkerEventArgs(marker.Clone()));
    }

    private void ApplyRemoteUpdate(Marker marker)
    {
        var result = _markers.Update(marker.Id, marker, marker.OwnerId, true);
        if (result.Outcome == MutationOutcome.Applied && result.Marker is not null)
        {
            MarkerUpdated?.Invoke(this, new MarkerEventArgs(result.Marker.Clone()));
        }
    }

    private void ApplyRemoteDelete(string id)
    {
        if (_markers.Delete(id, PlayerId, true).Outcome == MutationOutcome.Applied)
        {
            MarkerDeleted?.Invoke(this, new MarkerDeletedEventArgs(id));
        }
    }

    private void RefreshPlayers()
    {
        if (_server is not null)
        {
            Players = _server.Players;
            PlayersChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // ── 变更入口 ────────────────────────────────────────────────────────

    /// <summary>新增标记（客户端会等待房主广播回显后才更新本地状态）。</summary>
    public async Task<MutationResult> AddMarkerAsync(Marker marker)
    {
        Prepare(marker);
        return Mode switch
        {
            SessionMode.Offline => AddOffline(marker),
            SessionMode.Host => await (_server?.AddMarkerAsync(marker) ?? Task.FromResult(MutationResult.Invalid())).ConfigureAwait(false),
            _ => await SendClientAsync(() => _client!.SendAddAsync(marker)).ConfigureAwait(false),
        };
    }

    /// <summary>全量更新标记（需要权限：客户端只能改自己的标记）。</summary>
    public async Task<MutationResult> UpdateMarkerAsync(Marker marker)
    {
        Prepare(marker);
        return Mode switch
        {
            SessionMode.Offline => UpdateOffline(marker),
            SessionMode.Host => await (_server?.UpdateMarkerAsync(marker) ?? Task.FromResult(MutationResult.Invalid())).ConfigureAwait(false),
            _ => await SendClientAsync(() => _client!.SendUpdateAsync(marker)).ConfigureAwait(false),
        };
    }

    /// <summary>删除标记。</summary>
    public async Task<MutationResult> DeleteMarkerAsync(string id)
    {
        return Mode switch
        {
            SessionMode.Offline => DeleteOffline(id),
            SessionMode.Host => await (_server?.DeleteMarkerAsync(id) ?? Task.FromResult(MutationResult.Invalid())).ConfigureAwait(false),
            _ => await SendClientAsync(() => _client!.SendDeleteAsync(id)).ConfigureAwait(false),
        };
    }

    /// <summary>客户端能否操作该标记（房主可操作全部）。</summary>
    public bool CanModify(Marker marker) => MarkerStore.CanModify(marker, PlayerId, Mode != SessionMode.Client);

    private static async Task<MutationResult> SendClientAsync(Func<Task<bool>> send)
    {
        var ok = await send().ConfigureAwait(false);
        // 客户端不自行改状态：请求已发出，真正生效要等房主的权威广播
        return ok
            ? MutationResult.SentToHost()
            : new MutationResult(MutationOutcome.Invalid, null);
    }

    private MutationResult AddOffline(Marker marker)
    {
        var result = _markers.Add(marker);
        if (result.Outcome == MutationOutcome.Applied && result.Marker is not null)
        {
            MarkerAdded?.Invoke(this, new MarkerEventArgs(result.Marker.Clone()));
        }
        return result;
    }

    private MutationResult UpdateOffline(Marker marker)
    {
        var result = _markers.Update(marker.Id, marker, PlayerId, true);
        if (result.Outcome == MutationOutcome.Applied && result.Marker is not null)
        {
            MarkerUpdated?.Invoke(this, new MarkerEventArgs(result.Marker.Clone()));
        }
        return result;
    }

    private MutationResult DeleteOffline(string id)
    {
        var result = _markers.Delete(id, PlayerId, true);
        if (result.Outcome == MutationOutcome.Applied)
        {
            MarkerDeleted?.Invoke(this, new MarkerDeletedEventArgs(id));
        }
        return result;
    }

    private void Prepare(Marker marker)
    {
        if (string.IsNullOrWhiteSpace(marker.Id)) marker.Id = Guid.NewGuid().ToString("D");
        if (Mode != SessionMode.Client && string.IsNullOrWhiteSpace(marker.OwnerId)) marker.OwnerId = PlayerId;
        if (marker.CreatedAt <= 0) marker.CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    // ── 网格设置（本地视图设置，不写入协议）──────────────────────────────

    public void SetGrid(int cols, int rows, bool? visible = null)
    {
        var clone = Map.Clone();
        clone.GridCols = Math.Clamp(cols, MapInfo.MinGrid, MapInfo.MaxGrid);
        clone.GridRows = Math.Clamp(rows, MapInfo.MinGrid, MapInfo.MaxGrid);
        if (visible.HasValue) clone.GridVisible = visible.Value;
        Map = clone;
        MapChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleGrid()
    {
        var clone = Map.Clone();
        clone.GridVisible = !clone.GridVisible;
        Map = clone;
        MapChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 替换地图图片。
    /// 本地模式任何时刻都可以；房主模式仅在没有队友加入时允许（之后地图固定，避免客户端缓存哈希不一致）。
    /// </summary>
    public bool ReplaceLocalImage(byte[] bytes, int width, int height)
    {
        if (bytes is null || bytes.Length == 0) return false;

        if (Mode == SessionMode.Offline)
        {
            Config.ImageBytes = bytes;
            Config.ImageWidth = width;
            Config.ImageHeight = height;
            InitializeLocalMap();
            MapChanged?.Invoke(this, EventArgs.Empty);
            ImageChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        if (Mode == SessionMode.Host && _server is { ClientCount: 0 })
        {
            Config.ImageBytes = bytes;
            Config.ImageWidth = width;
            Config.ImageHeight = height;
            InitializeLocalMap();
            _server.UpdateMap(Map);
            MapChanged?.Invoke(this, EventArgs.Empty);
            ImageChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        return false;
    }

    /// <summary>当前是否允许替换地图图片。</summary>
    public bool CanReplaceImage => Mode == SessionMode.Offline ||
                                   (Mode == SessionMode.Host && _server is { ClientCount: 0 });

    // ── 关闭 ────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try { _imageCts?.Cancel(); } catch { /* ignore */ }
        _imageCts?.Dispose();
        _imageCts = null;

        if (_client is not null)
        {
            try { await _client.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
            _client = null;
        }

        if (_server is not null)
        {
            try { await _server.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
            _server = null;
        }

        ConnectionState = ConnectionState.Stopped;
    }
}
