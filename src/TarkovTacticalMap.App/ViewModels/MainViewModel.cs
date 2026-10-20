using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SkiaSharp;
using TarkovTacticalMap.App.Rendering;
using TarkovTacticalMap.App.Services;
using TarkovTacticalMap.Core.Geometry;
using TarkovTacticalMap.Core.Models;
using TarkovTacticalMap.Core.State;
using TarkovTacticalMap.Core.Utils;
using TarkovTacticalMap.Network;

namespace TarkovTacticalMap.App.ViewModels;

/// <summary>工具栏按钮的视图模型。</summary>
public sealed partial class ToolButtonViewModel : ObservableObject
{
    public ToolButtonViewModel(ToolKind kind, string label, string? iconId)
    {
        Kind = kind;
        Label = label;
        IconId = iconId;
    }

    public ToolKind Kind { get; }

    public string Label { get; }

    public string? IconId { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// 主窗口视图模型：持有视图状态（缩放/平移/工具/选中）与标记缓存，
/// 并把 TacticalSession 的事件转成可绑定属性。所有网络回调都回到 UI 线程。
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    /// <summary>预设调色板（点击即用）。</summary>
    public static readonly string[] Palette =
    {
        "#FF5252", "#FF9800", "#FFE066", "#69F0AE", "#40C4FF", "#7C4DFF", "#FF80AB", "#FFFFFF",
    };

    private readonly Dispatcher _dispatcher;
    private readonly AppSettings _settings;
    private readonly List<Marker> _markers = new();
    private TacticalSession? _session;
    private SKImage? _background;
    private bool _suppressSelectionSync;

    public MainViewModel(Dispatcher dispatcher, AppSettings settings, ImageCache cache)
    {
        _dispatcher = dispatcher;
        _settings = settings;
        Cache = cache;
        CurrentColor = ColorHex.Normalize(settings.DefaultColor) ?? MarkerDefaults.DefaultColor;
        CurrentSize = settings.DefaultPointSize;
        BuildToolButtons();
        RefreshSwatchSelection();
        ConnectionText = "未连接";
    }

    public ImageCache Cache { get; }

    public IconLibrary Icons { get; } = IconLibrary.Default;

    public ViewportState Viewport { get; } = new();

    public ObservableCollection<ToolButtonViewModel> Tools { get; } = new();

    public ObservableCollection<string> Swatches { get; } = new();

    public ObservableCollection<PlayerInfo> Players { get; } = new();

    public TacticalSession? Session => _session;

    public MapInfo? Map => _session?.Map;

    public SKImage? Background => _background;

    /// <summary>已按 CreatedAt 升序（新标记在上层）。</summary>
    public IReadOnlyList<Marker> Markers => _markers;

    /// <summary>临时绘制层：正在拖拽创建 / 编辑中的标记。</summary>
    public Marker? Draft { get; set; }

    /// <summary>鼠标所在网格标签（用于高亮）。</summary>
    public string? HoverGridLabel { get; set; }

    // ── 可绑定状态 ──────────────────────────────────────────────────────

    [ObservableProperty]
    private ToolKind _activeTool = ToolKind.Select;

    [ObservableProperty]
    private string _currentColor = MarkerDefaults.DefaultColor;

    [ObservableProperty]
    private float _currentSize = MarkerDefaults.PointSize;

    [ObservableProperty]
    private string? _selectedMarkerId;

    [ObservableProperty]
    private bool _hasSelection;

    [ObservableProperty]
    private string _statusText = "就绪";

    [ObservableProperty]
    private string _connectionText = "未连接";

    [ObservableProperty]
    private string _playerSummary = "—";

    [ObservableProperty]
    private string _gridLabel = "—";

    [ObservableProperty]
    private string _coordinateText = "—";

    [ObservableProperty]
    private bool _isGridVisible = true;

    [ObservableProperty]
    private string _selectionTypeText = "";

    [ObservableProperty]
    private string _selectionColorText = MarkerDefaults.DefaultColor;

    [ObservableProperty]
    private double _selectionSize;

    [ObservableProperty]
    private double _selectionRotation;

    [ObservableProperty]
    private string _selectionText = "";

    [ObservableProperty]
    private bool _canEditSelection;

    [ObservableProperty]
    private string _mapTitle = "（未加载地图）";

    partial void OnActiveToolChanged(ToolKind value)
    {
        foreach (var tool in Tools) tool.IsSelected = tool.Kind == value;
    }

    partial void OnSelectedMarkerIdChanged(string? value)
    {
        SyncSelection();
    }

    partial void OnCurrentColorChanged(string value) => RefreshSwatchSelection();

    // ── 工具与颜色 ──────────────────────────────────────────────────────

    private void BuildToolButtons()
    {
        Tools.Clear();
        foreach (var kind in Enum.GetValues<ToolKind>())
        {
            Tools.Add(new ToolButtonViewModel(kind, ToolKinds.LabelOf(kind), ToolKinds.IconIdOf(kind)));
        }
        OnActiveToolChanged(ActiveTool);

        Swatches.Clear();
        foreach (var c in Palette) Swatches.Add(c);
        RefreshSwatchSelection();
    }

    private void RefreshSwatchSelection()
    {
        SelectedSwatch = ColorHex.Normalize(CurrentColor)?.ToUpperInvariant();
    }

    [ObservableProperty]
    private string? _selectedSwatch;

    /// <summary>选择工具（供按钮 / 快捷键调用）。</summary>
    public void SelectTool(ToolKind kind)
    {
        ActiveTool = kind;
        if (!ToolKinds.IsPointTool(kind) && kind != ToolKind.Select)
        {
            CurrentSize = MarkerDefaults.DefaultSizeFor(ToolKinds.MarkerTypeOf(kind));
        }
        else if (ToolKinds.IsPointTool(kind))
        {
            CurrentSize = _settings.DefaultPointSize > 0 ? _settings.DefaultPointSize : MarkerDefaults.PointSize;
        }
        SetStatus($"当前工具：{ToolKinds.LabelOf(kind)}");
    }

    /// <summary>按名称选择工具（XAML CommandParameter 用）。</summary>
    public void SelectToolByName(string? name)
    {
        if (Enum.TryParse<ToolKind>(name, true, out var kind)) SelectTool(kind);
    }

    public void SetColor(string color)
    {
        var normalized = ColorHex.Normalize(color);
        if (normalized is null)
        {
            SetStatus($"颜色格式不合法：{color}（应为 #RRGGBB 或 #AARRGGBB）");
            return;
        }
        CurrentColor = normalized;
        if (SelectedMarker is { } marker && CanEditSelection) SetSelectionColor(normalized);
    }

    // ── 会话接入 ────────────────────────────────────────────────────────

    /// <summary>接管一个会话：订阅事件并刷新全部状态（状态应用在 UI 线程完成）。</summary>
    public async Task AttachSessionAsync(TacticalSession session)
    {
        await DetachSessionAsync().ConfigureAwait(false);

        _session = session;
        session.MarkerAdded += OnMarkerAdded;
        session.MarkerUpdated += OnMarkerUpdated;
        session.MarkerDeleted += OnMarkerDeleted;
        session.MarkersReplaced += OnMarkersReplaced;
        session.MapChanged += OnMapChanged;
        session.ImageChanged += OnImageChanged;
        session.PlayersChanged += OnPlayersChanged;
        session.StatusChanged += OnStatusChanged;
        session.Notice += OnNotice;
        session.Error += OnError;

        var snapshot = session.MarkerSnapshot();
        var mode = session.Mode;

        Post(() =>
        {
            _markers.Clear();
            _markers.AddRange(snapshot);
            Draft = null;
            SelectedMarkerId = null;
            RefreshMapState();
            DecodeBackground();
            RefreshPlayers();
            UpdateConnectionText();
            OnPropertyChanged(nameof(Session));
            OnPropertyChanged(nameof(Map));
            SetStatus(mode switch
            {
                SessionMode.Host => "房间已创建，等待队友加入",
                SessionMode.Client => "已连接房主",
                _ => "本地模式：所有标记只保存在本机",
            });
        });

        if (mode == SessionMode.Host)
        {
            var text = await FetchHostAddressTextAsync().ConfigureAwait(false);
            Post(() => HostAddressText = text);
        }
    }

    public async Task DetachSessionAsync()
    {
        var session = _session;
        if (session is null) return;

        session.MarkerAdded -= OnMarkerAdded;
        session.MarkerUpdated -= OnMarkerUpdated;
        session.MarkerDeleted -= OnMarkerDeleted;
        session.MarkersReplaced -= OnMarkersReplaced;
        session.MapChanged -= OnMapChanged;
        session.ImageChanged -= OnImageChanged;
        session.PlayersChanged -= OnPlayersChanged;
        session.StatusChanged -= OnStatusChanged;
        session.Notice -= OnNotice;
        session.Error -= OnError;

        _session = null;
        await session.DisposeAsync().ConfigureAwait(false);

        Post(() =>
        {
            _markers.Clear();
            Draft = null;
            SelectedMarkerId = null;
            Players.Clear();
            SetBackground(null);
            MapTitle = "（未加载地图）";
            ConnectionText = "未连接";
            PlayerSummary = "—";
            SetStatus("会话已结束");
            OnPropertyChanged(nameof(Session));
            OnPropertyChanged(nameof(Map));
        });
    }

    // ── 事件处理（网络线程 → UI 线程）───────────────────────────────────

    private void OnMarkerAdded(object? sender, MarkerEventArgs e)
    {
        Post(() =>
        {
            InsertSorted(e.Marker);
            // 客户端新建标记时先拿到了 Id，等房主回显后补齐属性面板
            if (string.Equals(e.Marker.Id, SelectedMarkerId, StringComparison.Ordinal)) SyncSelection();
            SetStatus($"新增 {ToolKinds.LabelOf(e.Marker.Type switch
            {
                MarkerType.Arrow => ToolKind.Arrow,
                MarkerType.Circle => ToolKind.Circle,
                MarkerType.Rectangle => ToolKind.Rectangle,
                MarkerType.Line => ToolKind.Line,
                MarkerType.Text => ToolKind.Text,
                _ => ToolKind.Kill,
            })} 标记");
        });
    }

    private void OnMarkerUpdated(object? sender, MarkerEventArgs e)
    {
        Post(() =>
        {
            ReplaceSorted(e.Marker);
            if (string.Equals(e.Marker.Id, SelectedMarkerId, StringComparison.Ordinal)) SyncSelection();
        });
    }

    private void OnMarkerDeleted(object? sender, MarkerDeletedEventArgs e)
    {
        Post(() =>
        {
            _markers.RemoveAll(m => string.Equals(m.Id, e.Id, StringComparison.Ordinal));
            if (string.Equals(e.Id, SelectedMarkerId, StringComparison.Ordinal))
            {
                SelectedMarkerId = null;
                Draft = null;
            }
            SetStatus("标记已删除");
        });
    }

    private void OnMarkersReplaced(object? sender, EventArgs e)
    {
        Post(() =>
        {
            _markers.Clear();
            if (_session is not null)
            {
                foreach (var marker in _session.MarkerSnapshot()) _markers.Add(marker);
            }
            Draft = null;
            if (SelectedMarkerId is not null && _markers.All(m => m.Id != SelectedMarkerId)) SelectedMarkerId = null;
            SetStatus("已用房主的全量快照重建本地状态");
        });
    }

    private void OnMapChanged(object? sender, EventArgs e) => Post(RefreshMapState);

    private void OnImageChanged(object? sender, EventArgs e) => Post(DecodeBackground);

    private void OnPlayersChanged(object? sender, EventArgs e) => Post(RefreshPlayers);

    private void OnStatusChanged(object? sender, string message)
    {
        Post(() =>
        {
            SetStatus(message);
            UpdateConnectionText();
        });
    }

    private void OnNotice(object? sender, string message) => Post(() => SetStatus(message));

    private void OnError(object? sender, string message) => Post(() => SetStatus("错误：" + message));

    /// <summary>需要重绘画布。</summary>
    public event EventHandler? RenderInvalidated;

    private void InvalidateRender() => RenderInvalidated?.Invoke(this, EventArgs.Empty);

    private void Post(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            InvalidateRender();
        }
        else
        {
            _dispatcher.BeginInvoke(() =>
            {
                action();
                InvalidateRender();
            }, DispatcherPriority.Background);
        }
    }

    private void InsertSorted(Marker marker)
    {
        var index = _markers.Count;
        for (var i = 0; i < _markers.Count; i++)
        {
            if (_markers[i].CreatedAt > marker.CreatedAt)
            {
                index = i;
                break;
            }
        }
        _markers.Insert(index, marker);
    }

    private void ReplaceSorted(Marker marker)
    {
        for (var i = 0; i < _markers.Count; i++)
        {
            if (!string.Equals(_markers[i].Id, marker.Id, StringComparison.Ordinal)) continue;
            _markers[i] = marker;
            return;
        }
        InsertSorted(marker);
    }

    private void RefreshMapState()
    {
        var map = _session?.Map;
        if (map is null) return;

        IsGridVisible = map.GridVisible;
        Viewport.SetImage(map.ImageWidth, map.ImageHeight);
        MapTitle = map.ImageWidth > 0
            ? $"地图 {map.ImageWidth}×{map.ImageHeight} · 网格 {map.GridCols}×{map.GridRows}"
            : "（未加载地图）";
        OnPropertyChanged(nameof(Map));
    }

    private void DecodeBackground()
    {
        var bytes = _session?.ImageBytes;
        if (bytes is null || bytes.Length == 0)
        {
            SetBackground(null);
            return;
        }

        try
        {
            var image = SKImage.FromEncodedData(bytes);
            if (image is null)
            {
                SetBackground(null);
                return;
            }
            SetBackground(image);
        }
        catch (Exception ex)
        {
            SetStatus($"地图图片解码失败：{ex.Message}");
            SetBackground(null);
        }
    }

    private void SetBackground(SKImage? image)
    {
        var old = _background;
        _background = image;
        old?.Dispose();
        OnPropertyChanged(nameof(Background));
    }

    private void RefreshPlayers()
    {
        Players.Clear();
        var session = _session;
        if (session is null)
        {
            PlayerSummary = "—";
            return;
        }

        foreach (var player in session.Players) Players.Add(player);
        if (Players.Count == 0)
        {
            Players.Add(new PlayerInfo { PlayerId = session.PlayerId, PlayerName = session.PlayerName, IsHost = session.IsHost });
        }
        PlayerSummary = string.Join("、", Players.Select(p => p.ToString()));
        UpdateConnectionText();
    }

    private void UpdateConnectionText()
    {
        var session = _session;
        if (session is null)
        {
            ConnectionText = "未连接";
            return;
        }

        var online = Players.Count == 0 ? 1 : Players.Count;
        ConnectionText = session.Mode switch
        {
            SessionMode.Host => $"房主 · TCP {session.Port} · 在线 {online}",
            SessionMode.Client => $"客户端 · {session.Config.HostIp}:{session.Config.Port} · 在线 {online} · {DescribeState(session.ConnectionState)}",
            _ => $"本地模式 · 在线 1",
        };
    }

    private static string DescribeState(ConnectionState state) => state switch
    {
        ConnectionState.Connected => "已连接",
        ConnectionState.Connecting => "连接中",
        ConnectionState.Reconnecting => "重连中",
        ConnectionState.Faulted => "已断开",
        ConnectionState.Stopped => "已停止",
        _ => "空闲",
    };

    private static async Task<string> FetchHostAddressTextAsync()
    {
        try
        {
            var addresses = await NetworkAddresses.GetLanAddressesAsync().ConfigureAwait(false);
            return addresses.Count > 0
                ? string.Join("  /  ", addresses)
                : "未发现可用虚拟网卡地址";
        }
        catch
        {
            return "获取本机地址失败";
        }
    }

    [ObservableProperty]
    private string _hostAddressText = "";

    // ── 选中与属性面板 ──────────────────────────────────────────────────

    public Marker? SelectedMarker
    {
        get
        {
            if (string.IsNullOrEmpty(SelectedMarkerId)) return null;
            foreach (var marker in _markers)
            {
                if (string.Equals(marker.Id, SelectedMarkerId, StringComparison.Ordinal)) return marker;
            }
            return null;
        }
    }

    public void SelectMarker(string? id)
    {
        SelectedMarkerId = id;
        SyncSelection();
        InvalidateRender();
    }

    private void SyncSelection()
    {
        var marker = SelectedMarker;
        HasSelection = marker is not null;
        if (marker is null)
        {
            SelectionTypeText = "";
            CanEditSelection = false;
            return;
        }

        _suppressSelectionSync = true;
        try
        {
            SelectionTypeText = DescribeMarker(marker);
            SelectionColorText = ColorHex.Normalize(marker.Color) ?? marker.Color;
            SelectionSize = Math.Round(marker.Size, 4);
            SelectionRotation = Math.Round(marker.Rotation, 1);
            SelectionText = marker.Text ?? string.Empty;
            CanEditSelection = _session?.CanModify(marker) ?? false;
        }
        finally
        {
            _suppressSelectionSync = false;
        }
    }

    public static string DescribeMarker(Marker marker)
    {
        if (marker.Type == MarkerType.Point)
        {
            if (!string.IsNullOrEmpty(marker.IconId) && IconLibrary.Labels.TryGetValue(marker.IconId, out var label))
            {
                return $"图标点 · {label}";
            }
            return "圆点";
        }

        return marker.Type switch
        {
            MarkerType.Text => "文本",
            MarkerType.Arrow => "箭头",
            MarkerType.Line => "直线",
            MarkerType.Circle => "圆圈",
            MarkerType.Rectangle => "方框",
            _ => "未知类型",
        };
    }

    partial void OnSelectionColorTextChanged(string value)
    {
        if (_suppressSelectionSync) return;
        SetSelectionColor(value);
    }

    partial void OnSelectionSizeChanged(double value)
    {
        if (_suppressSelectionSync) return;
        CommitSelection(m => m.Size = (float)value);
    }

    partial void OnSelectionRotationChanged(double value)
    {
        if (_suppressSelectionSync) return;
        CommitSelection(m => m.Rotation = (float)value);
    }

    partial void OnSelectionTextChanged(string value)
    {
        if (_suppressSelectionSync) return;
        CommitSelection(m => m.Text = value);
    }

    private void SetSelectionColor(string color)
    {
        var normalized = ColorHex.Normalize(color);
        if (normalized is null) return;
        CommitSelection(m => m.Color = normalized);
    }

    /// <summary>把属性面板的修改提交给会话（房主/本地立即生效，客户端等房主广播）。</summary>
    private void CommitSelection(Action<Marker> mutate)
    {
        var marker = SelectedMarker;
        var session = _session;
        if (marker is null || session is null || !session.CanModify(marker))
        {
            if (marker is not null) SetStatus("无权修改他人创建的标记");
            return;
        }

        var clone = marker.Clone();
        mutate(clone);
        _ = CommitMarkerAsync(clone);
    }

    private async Task CommitMarkerAsync(Marker marker)
    {
        var session = _session;
        if (session is null) return;
        var result = await session.UpdateMarkerAsync(marker).ConfigureAwait(false);
        if (result.Outcome is MutationOutcome.PermissionDenied or MutationOutcome.Invalid or MutationOutcome.NotFound)
        {
            Post(() => SetStatus($"修改未生效：{DescribeMutation(result.Outcome)}"));
        }
    }

    private static string DescribeMutation(MutationOutcome outcome) => outcome switch
    {
        MutationOutcome.PermissionDenied => "无权限",
        MutationOutcome.Invalid => "内容不合法",
        MutationOutcome.NotFound => "标记不存在",
        MutationOutcome.Ignored => "已被忽略",
        MutationOutcome.Sent => "已发送，等待房主确认",
        _ => outcome.ToString(),
    };

    /// <summary>属性面板的旋转微调按钮。</summary>
    public void NudgeRotation(float delta)
    {
        var marker = SelectedMarker;
        if (marker is null) return;
        var rotation = MarkerValidator.NormalizeRotation(marker.Rotation + delta);
        _suppressSelectionSync = true;
        try { SelectionRotation = Math.Round(rotation, 1); }
        finally { _suppressSelectionSync = false; }
        CommitSelection(m => m.Rotation = rotation);
    }

    // ── 标记操作 ────────────────────────────────────────────────────────

    /// <summary>按当前工具创建标记原型（尚未提交）。</summary>
    public Marker CreatePrototype(ToolKind tool, MapPoint normalized, string? text = null)
    {
        var type = ToolKinds.MarkerTypeOf(tool);
        var marker = new Marker
        {
            Id = Guid.NewGuid().ToString("D"),
            Type = type,
            X = MarkerValidator.Clamp01(normalized.X),
            Y = MarkerValidator.Clamp01(normalized.Y),
            Size = CurrentSize > 0 ? CurrentSize : MarkerDefaults.DefaultSizeFor(type),
            Color = CurrentColor,
            Rotation = 0f,
            IconId = ToolKinds.IconIdOf(tool),
            Text = text,
            OwnerId = _session?.PlayerId ?? string.Empty,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        switch (type)
        {
            case MarkerType.Line:
            case MarkerType.Rectangle:
                marker.EndX = marker.X;
                marker.EndY = marker.Y;
                break;
            case MarkerType.Arrow:
                marker.Size = CurrentSize > 0 && CurrentSize > 0.01f ? CurrentSize : MarkerDefaults.ArrowSize;
                marker.Rotation = 0f;
                break;
        }

        return marker;
    }

    public async Task AddMarkerAsync(Marker marker)
    {
        var session = _session;
        if (session is null) return;
        var result = await session.AddMarkerAsync(marker).ConfigureAwait(false);
        if (result.Outcome is not (MutationOutcome.Applied or MutationOutcome.Ignored or MutationOutcome.Sent))
        {
            Post(() => SetStatus($"新增标记失败：{DescribeMutation(result.Outcome)}"));
            return;
        }
        Post(() => SelectedMarkerId = marker.Id);
    }

    public async Task UpdateMarkerAsync(Marker marker)
    {
        var session = _session;
        if (session is null) return;
        var result = await session.UpdateMarkerAsync(marker).ConfigureAwait(false);
        if (result.Outcome is not (MutationOutcome.Applied or MutationOutcome.Ignored or MutationOutcome.Sent))
        {
            Post(() => SetStatus($"修改未生效：{DescribeMutation(result.Outcome)}"));
        }
    }

    public async Task<bool> DeleteMarkerAsync(string id)
    {
        var session = _session;
        if (session is null) return false;
        var marker = session.GetMarker(id);
        if (marker is not null && !session.CanModify(marker))
        {
            SetStatus("无权删除他人创建的标记");
            return false;
        }

        var result = await session.DeleteMarkerAsync(id).ConfigureAwait(false);
        if (result.Outcome is MutationOutcome.PermissionDenied or MutationOutcome.Invalid)
        {
            Post(() => SetStatus($"删除失败：{DescribeMutation(result.Outcome)}"));
            return false;
        }
        return true;
    }

    public Task DeleteSelectedAsync()
    {
        var id = SelectedMarkerId;
        return id is null ? Task.CompletedTask : DeleteMarkerAsync(id);
    }

    /// <summary>
    /// 中键快速删除：删除屏幕坐标下最上层的标记（需要权限）。
    /// 返回是否真的删除了标记。
    /// </summary>
    public async Task<bool> DeleteMarkerAtAsync(float screenX, float screenY)
    {
        var id = HitTestMarker(screenX, screenY);
        if (id is null)
        {
            Post(() => SetStatus("中键：此处没有标记"));
            return false;
        }

        var marker = FindMarker(id);
        var session = _session;
        if (marker is null || session is null) return false;

        if (!session.CanModify(marker))
        {
            Post(() => SetStatus($"标记由「{PlayerDisplayName(marker.OwnerId)}」创建，无权删除"));
            return false;
        }

        Post(() => SelectMarker(id));
        var deleted = await DeleteMarkerAsync(id).ConfigureAwait(false);
        if (deleted)
        {
            Post(() => SetStatus($"已删除{DescribeMarker(marker)}标记"));
        }
        return deleted;
    }

    // ── 视图 ────────────────────────────────────────────────────────────

    public void ResetView()
    {
        Viewport.ResetView();
        SetStatus("视图已重置");
    }

    public void ToggleGrid()
    {
        _session?.ToggleGrid();
        if (_session is null) IsGridVisible = !IsGridVisible;
        SetStatus(IsGridVisible ? "已显示网格" : "已隐藏网格");
    }

    public void SetGridSize(int cols, int rows)
    {
        _session?.SetGrid(cols, rows);
        SetStatus($"网格已设为 {cols}×{rows}（仅本机视图，不参与存储）");
    }

    public void SetStatus(string message)
    {
        StatusText = message;
    }

    /// <summary>更新鼠标相关的状态栏信息。</summary>
    public void UpdateHover(float screenX, float screenY)
    {
        var map = _session?.Map;
        var normalized = Viewport.ScreenToNormalized(screenX, screenY);
        var inside = normalized.X is >= 0f and <= 1f && normalized.Y is >= 0f and <= 1f;
        if (!inside || map is null)
        {
            HoverGridLabel = null;
            GridLabel = "—";
            CoordinateText = "—";
            return;
        }

        HoverGridLabel = GridReference.GetLabel(normalized.X, normalized.Y, map.GridCols, map.GridRows);
        GridLabel = HoverGridLabel ?? "—";
        var px = (int)MathF.Round(normalized.X * map.ImageWidth);
        var py = (int)MathF.Round(normalized.Y * map.ImageHeight);
        CoordinateText = $"X {normalized.X:F3}  Y {normalized.Y:F3}  ({px}, {py}px)";
    }

    public void ClearHover()
    {
        HoverGridLabel = null;
        GridLabel = "—";
        CoordinateText = "—";
    }

    /// <summary>命中检测：返回最上层（CreatedAt 最大）被命中的标记 Id。</summary>
    public string? HitTestMarker(float screenX, float screenY)
    {
        var point = new MapPoint(screenX, screenY);
        for (var i = _markers.Count - 1; i >= 0; i--)
        {
            if (MarkerGeometry.HitTest(_markers[i], point, Viewport)) return _markers[i].Id;
        }
        return null;
    }

    /// <summary>命中检测：选中标记的控制点。</summary>
    public HandleInfo? HitTestHandle(float screenX, float screenY)
    {
        var marker = SelectedMarker;
        if (marker is null) return null;
        return MarkerGeometry.HitTestHandle(marker, new MapPoint(screenX, screenY), Viewport);
    }

    public Marker? FindMarker(string id)
    {
        foreach (var marker in _markers)
        {
            if (string.Equals(marker.Id, id, StringComparison.Ordinal)) return marker;
        }
        return null;
    }

    /// <summary>保存当前设置为「上次使用」。</summary>
    public void PersistSettings()
    {
        _settings.DefaultColor = ColorHex.Normalize(CurrentColor) ?? MarkerDefaults.DefaultColor;
        _settings.Save();
    }

    public string PlayerName => _session?.PlayerName ?? _settings.PlayerName;

    /// <summary>把玩家 Id 映射成显示名（用于「他人标记」提示）。</summary>
    public string PlayerDisplayName(string playerId)
    {
        foreach (var player in Players)
        {
            if (string.Equals(player.PlayerId, playerId, StringComparison.Ordinal)) return player.PlayerName;
        }
        if (string.Equals(playerId, _session?.PlayerId, StringComparison.Ordinal)) return _session?.PlayerName ?? "我";
        return string.IsNullOrEmpty(playerId) ? "未知玩家" : playerId[..Math.Min(6, playerId.Length)];
    }
}
