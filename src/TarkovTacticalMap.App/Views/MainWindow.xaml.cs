using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using TarkovTacticalMap.App.Rendering;
using TarkovTacticalMap.App.Services;
using TarkovTacticalMap.App.ViewModels;
using TarkovTacticalMap.Core.Geometry;
using TarkovTacticalMap.Core.Models;
using TarkovTacticalMap.Core.State;
using TarkovTacticalMap.Core.Utils;

namespace TarkovTacticalMap.App.Views;

/// <summary>主窗口：菜单 + 左侧工具栏 + SkiaSharp 画布 + 右侧属性面板 + 状态栏。</summary>
public partial class MainWindow : Window
{
    private enum DragMode
    {
        None,
        Pan,
        MoveMarker,
        Handle,
        Create,
    }

    private readonly MainViewModel _vm;
    private readonly AppSettings _settings;
    private readonly MapRenderer _renderer = new();

    private TacticalSession? _session;
    private float _scaleX = 1f;
    private float _scaleY = 1f;
    private Point _lastDip;
    private DragMode _drag = DragMode.None;
    private Marker? _dragOrigin;
    private HandleKind _dragHandle = HandleKind.None;
    private string? _dragMarkerId;
    private MapPoint _dragStartNorm;
    private MapPoint _dragStartSurface;
    private bool _spaceDown;
    private MouseButton _dragButton;
    private bool _cleanupStarted;

    public MainWindow(MainViewModel viewModel, TacticalSession session, AppSettings settings)
    {
        InitializeComponent();
        _vm = viewModel;
        _session = session;
        _settings = settings;
        DataContext = viewModel;
        _vm.RenderInvalidated += (_, _) => InvalidateCanvas();
        Loaded += OnWindowLoaded;
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        var session = _session;
        if (session is null) return;
        await _vm.AttachSessionAsync(session);
        CanvasView.Focus();
        InvalidateCanvas();
    }

    // ── 绘制 ────────────────────────────────────────────────────────────

    private void OnPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var actualWidth = CanvasView.ActualWidth;
        var actualHeight = CanvasView.ActualHeight;
        if (actualWidth > 0 && actualHeight > 0)
        {
            _scaleX = (float)(e.Info.Width / actualWidth);
            _scaleY = (float)(e.Info.Height / actualHeight);
        }

        _vm.Viewport.SetCanvas(e.Info.Width, e.Info.Height);

        var scene = new RenderScene
        {
            Viewport = _vm.Viewport,
            Map = _vm.Map,
            Markers = _vm.Markers,
            SelectedMarkerId = _vm.SelectedMarkerId,
            Draft = _vm.Draft,
            Background = _vm.Background,
            Icons = _vm.Icons,
            HoverGridLabel = _vm.HoverGridLabel,
        };

        try
        {
            _renderer.Draw(e.Surface.Canvas, scene);
        }
        catch
        {
            e.Surface.Canvas.Clear(new SKColor(0xFF1B1D22));
        }
    }

    private void InvalidateCanvas() => CanvasView.InvalidateVisual();

    private MapPoint ToSurface(Point dip) => new((float)(dip.X * _scaleX), (float)(dip.Y * _scaleY));

    private MapPoint ToNormalizedClamped(MapPoint surface)
    {
        var n = _vm.Viewport.ScreenToNormalized(surface.X, surface.Y);
        return new MapPoint(MarkerValidator.Clamp01(n.X), MarkerValidator.Clamp01(n.Y));
    }

    // ── 鼠标 ────────────────────────────────────────────────────────────

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        CanvasView.Focus();
        var dip = e.GetPosition(CanvasView);
        _lastDip = dip;
        var surface = ToSurface(dip);

        // 已有拖拽进行中时，忽略其它按键按下（避免误删 / 误切换）
        if (_drag != DragMode.None) return;

        // 中键：快速删除光标下的标记（不改选中、不进入拖拽）
        if (e.ChangedButton == MouseButton.Middle)
        {
            _ = _vm.DeleteMarkerAtAsync(surface.X, surface.Y);
            InvalidateCanvas();
            e.Handled = true;
            return;
        }

        // 右键拖拽 / 空格+左键拖拽：平移画布
        var wantsPan = e.ChangedButton == MouseButton.Right ||
                       (e.ChangedButton == MouseButton.Left && _spaceDown);
        if (wantsPan)
        {
            _drag = DragMode.Pan;
            _dragButton = e.ChangedButton;
            CanvasView.Cursor = Cursors.Hand;
            CanvasView.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Left) return;
        if (!_vm.Viewport.IsValid) return;

        _dragButton = e.ChangedButton;
        _dragStartSurface = surface;
        _dragStartNorm = ToNormalizedClamped(surface);

        // 双击文本标记：编辑文本内容
        if (e.ClickCount == 2 && _vm.ActiveTool == ToolKind.Select)
        {
            var hitId = _vm.HitTestMarker(surface.X, surface.Y);
            var target = hitId is null ? null : _vm.FindMarker(hitId);
            if (target is { Type: MarkerType.Text } && (_vm.Session?.CanModify(target) ?? false))
            {
                var text = TextInputDialog.Ask(this, "编辑文本", "修改要显示在地图上的文本：", target.Text ?? string.Empty);
                if (text is not null)
                {
                    var updated = target.Clone();
                    updated.Text = text;
                    _ = _vm.UpdateMarkerAsync(updated);
                }
                return;
            }
        }

        if (_vm.ActiveTool == ToolKind.Select)
        {
            BeginSelectDrag(surface);
        }
        else
        {
            BeginCreate(surface);
        }
        InvalidateCanvas();
    }

    private void BeginSelectDrag(MapPoint surface)
    {
        // 1) 控制点优先（在屏幕坐标系判定，半径不随缩放变化）
        var handle = _vm.HitTestHandle(surface.X, surface.Y);
        var selected = _vm.SelectedMarker;
        if (handle is { } h && selected is not null && (_vm.Session?.CanModify(selected) ?? false))
        {
            _drag = DragMode.Handle;
            _dragHandle = h.Kind;
            _dragMarkerId = selected.Id;
            _dragOrigin = selected.Clone();
            CanvasView.CaptureMouse();
            return;
        }

        // 2) 标记本体
        var hitId = _vm.HitTestMarker(surface.X, surface.Y);
        if (hitId is not null)
        {
            _vm.SelectMarker(hitId);
            var marker = _vm.FindMarker(hitId);
            if (marker is not null && (_vm.Session?.CanModify(marker) ?? false))
            {
                _drag = DragMode.MoveMarker;
                _dragMarkerId = hitId;
                _dragOrigin = marker.Clone();
                CanvasView.CaptureMouse();
            }
            else if (marker is not null)
            {
                _vm.SetStatus($"标记由「{_vm.PlayerDisplayName(marker.OwnerId)}」创建，你只能查看");
            }
            return;
        }

        // 3) 空白处：取消选中
        _vm.SelectMarker(null);
    }

    private void BeginCreate(MapPoint surface)
    {
        var tool = _vm.ActiveTool;
        var normalized = _dragStartNorm;

        if (ToolKinds.IsPointTool(tool))
        {
            var marker = _vm.CreatePrototype(tool, normalized);
            _ = _vm.AddMarkerAsync(marker);
            return;
        }

        if (tool == ToolKind.Text)
        {
            var text = TextInputDialog.Ask(this, "新建文本", "输入要显示在地图上的文本：", "");
            if (string.IsNullOrWhiteSpace(text)) return;
            var marker = _vm.CreatePrototype(tool, normalized, text);
            _ = _vm.AddMarkerAsync(marker);
            return;
        }

        var draft = _vm.CreatePrototype(tool, normalized);
        draft.EndX = normalized.X;
        draft.EndY = normalized.Y;
        if (tool == ToolKind.Arrow) draft.Size = 0f;

        _vm.Draft = draft;
        _drag = DragMode.Create;
        CanvasView.CaptureMouse();
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag == DragMode.None) return;
        // 只响应「发起拖拽的那个键」，避免另一个键松开时误结束当前操作
        if (e.ChangedButton != _dragButton) return;

        CanvasView.ReleaseMouseCapture();
        CanvasView.Cursor = Cursors.Arrow;
        var mode = _drag;
        _drag = DragMode.None;

        switch (mode)
        {
            case DragMode.Pan:
                break;

            case DragMode.MoveMarker:
            case DragMode.Handle:
            {
                var draft = _vm.Draft;
                _vm.Draft = null;
                if (draft is not null) _ = _vm.UpdateMarkerAsync(draft);
                break;
            }

            case DragMode.Create:
            {
                var draft = _vm.Draft;
                _vm.Draft = null;
                if (draft is not null && IsGeometryValid(draft)) _ = _vm.AddMarkerAsync(draft);
                break;
            }
        }

        _dragOrigin = null;
        _dragMarkerId = null;
        _dragHandle = HandleKind.None;
        InvalidateCanvas();
    }

    private static bool IsGeometryValid(Marker marker)
    {
        switch (marker.Type)
        {
            case MarkerType.Arrow:
                return marker.Size > 0.002f;
            case MarkerType.Circle:
                return marker.Size > 0.002f;
            case MarkerType.Line:
            case MarkerType.Rectangle:
                return MathF.Abs(marker.EndX - marker.X) > 0.002f || MathF.Abs(marker.EndY - marker.Y) > 0.002f;
            default:
                return true;
        }
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        var dip = e.GetPosition(CanvasView);
        var surface = ToSurface(dip);

        if (_drag == DragMode.Pan)
        {
            var dx = (float)((dip.X - _lastDip.X) * _scaleX);
            var dy = (float)((dip.Y - _lastDip.Y) * _scaleY);
            _vm.Viewport.PanBy(dx, dy);
            _lastDip = dip;
            InvalidateCanvas();
            return;
        }

        _lastDip = dip;
        _vm.UpdateHover(surface.X, surface.Y);

        switch (_drag)
        {
            case DragMode.MoveMarker:
                DragMoveMarker(surface);
                break;
            case DragMode.Handle:
                DragHandle(surface);
                break;
            case DragMode.Create:
                DragCreate(surface);
                break;
            default:
                UpdateCursor(surface);
                break;
        }

        InvalidateCanvas();
    }

    private void UpdateCursor(MapPoint surface)
    {
        if (_spaceDown)
        {
            CanvasView.Cursor = Cursors.Hand;
            return;
        }

        if (_vm.ActiveTool != ToolKind.Select)
        {
            CanvasView.Cursor = Cursors.Cross;
            return;
        }

        if (_vm.HitTestHandle(surface.X, surface.Y) is not null)
        {
            CanvasView.Cursor = Cursors.SizeNWSE;
        }
        else if (_vm.HitTestMarker(surface.X, surface.Y) is not null)
        {
            CanvasView.Cursor = Cursors.SizeAll;
        }
        else
        {
            CanvasView.Cursor = Cursors.Arrow;
        }
    }

    private void DragMoveMarker(MapPoint surface)
    {
        var origin = _dragOrigin;
        if (origin is null) return;

        var current = ToNormalizedClamped(surface);
        var dx = current.X - _dragStartNorm.X;
        var dy = current.Y - _dragStartNorm.Y;

        var draft = origin.Clone();
        draft.X = MarkerValidator.Clamp01(origin.X + dx);
        draft.Y = MarkerValidator.Clamp01(origin.Y + dy);
        if (origin.Type is MarkerType.Line or MarkerType.Rectangle)
        {
            draft.EndX = MarkerValidator.Clamp01(origin.EndX + dx);
            draft.EndY = MarkerValidator.Clamp01(origin.EndY + dy);
        }

        _vm.Draft = draft;
    }

    private void DragHandle(MapPoint surface)
    {
        var origin = _dragOrigin;
        if (origin is null) return;

        var displayWidth = _vm.Viewport.DisplayWidth;
        var draft = origin.Clone();

        switch (_dragHandle)
        {
            case HandleKind.Tip:
            {
                var start = _vm.Viewport.NormalizedToScreen(origin.X, origin.Y);
                var (rotation, size) = MarkerGeometry.ComputeArrowGeometry(start, surface, displayWidth);
                draft.Rotation = MarkerValidator.NormalizeRotation(rotation);
                draft.Size = Math.Clamp(size, 0f, MarkerValidator.MaxSize);
                break;
            }
            case HandleKind.EndPoint:
            case HandleKind.Corner:
            {
                var current = ToNormalizedClamped(surface);
                draft.EndX = current.X;
                draft.EndY = current.Y;
                break;
            }
            case HandleKind.Radius:
            {
                var center = _vm.Viewport.NormalizedToScreen(origin.X, origin.Y);
                var distance = center.DistanceTo(surface);
                draft.Size = displayWidth > 1e-4f ? Math.Clamp(distance / displayWidth, 0f, MarkerValidator.MaxSize) : origin.Size;
                break;
            }
        }

        _vm.Draft = draft;
    }

    private void DragCreate(MapPoint surface)
    {
        var draft = _vm.Draft;
        if (draft is null) return;

        var displayWidth = _vm.Viewport.DisplayWidth;
        switch (draft.Type)
        {
            case MarkerType.Arrow:
            {
                var (rotation, size) = MarkerGeometry.ComputeArrowGeometry(_dragStartSurface, surface, displayWidth);
                draft.Rotation = MarkerValidator.NormalizeRotation(rotation);
                draft.Size = Math.Clamp(size, 0f, MarkerValidator.MaxSize);
                break;
            }
            case MarkerType.Circle:
            {
                var distance = _dragStartSurface.DistanceTo(surface);
                draft.Size = displayWidth > 1e-4f ? Math.Clamp(distance / displayWidth, 0f, MarkerValidator.MaxSize) : 0f;
                break;
            }
            default:
            {
                var current = ToNormalizedClamped(surface);
                draft.EndX = current.X;
                draft.EndY = current.Y;
                break;
            }
        }
    }

    private void OnCanvasMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_vm.Viewport.IsValid) return;
        var dip = e.GetPosition(CanvasView);
        var surface = ToSurface(dip);
        var factor = e.Delta > 0 ? 1.15f : 1f / 1.15f;
        _vm.Viewport.ZoomAt(surface.X, surface.Y, _vm.Viewport.Zoom * factor);
        _vm.UpdateHover(surface.X, surface.Y);
        InvalidateCanvas();
        e.Handled = true;
    }

    private void OnCanvasMouseLeave(object sender, MouseEventArgs e)
    {
        _vm.ClearHover();
        InvalidateCanvas();
    }

    // ── 键盘 ────────────────────────────────────────────────────────────

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox or System.Windows.Controls.PasswordBox)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
                _spaceDown = true;
                e.Handled = true;
                break;

            case Key.Delete:
                _ = _vm.DeleteSelectedAsync();
                e.Handled = true;
                break;

            case Key.Escape:
                _vm.Draft = null;
                _vm.SelectMarker(null);
                _drag = DragMode.None;
                InvalidateCanvas();
                e.Handled = true;
                break;

            case Key.D0:
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    _vm.ResetView();
                    InvalidateCanvas();
                    e.Handled = true;
                }
                break;
        }
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        if (e.Key == Key.Space) _spaceDown = false;
        base.OnPreviewKeyUp(e);
    }

    // ── 工具栏 / 属性面板 ───────────────────────────────────────────────

    private void OnToolButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ToolKind kind }) _vm.SelectTool(kind);
        CanvasView.Focus();
        InvalidateCanvas();
    }

    private void OnSwatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string color }) _vm.SetColor(color);
        CanvasView.Focus();
    }

    private void OnRotateMinusClick(object sender, RoutedEventArgs e) => _vm.NudgeRotation(-15f);

    private void OnRotatePlusClick(object sender, RoutedEventArgs e) => _vm.NudgeRotation(15f);

    private async void OnDeleteSelectedClick(object sender, RoutedEventArgs e) => await _vm.DeleteSelectedAsync();

    // ── 菜单 ────────────────────────────────────────────────────────────

    private async void OnNewRoomClick(object sender, RoutedEventArgs e) => await SwitchSessionAsync(true);

    private async void OnJoinRoomClick(object sender, RoutedEventArgs e) => await SwitchSessionAsync(false);

    private async Task SwitchSessionAsync(bool create)
    {
        var startup = new StartupWindow(_settings, App.Cache) { Owner = this };
        if (startup.ShowDialog() != true || startup.ResultSession is null) return;

        var previous = _session;
        _session = startup.ResultSession;
        await _vm.AttachSessionAsync(_session);
        if (previous is not null) await previous.DisposeAsync();
        InvalidateCanvas();
    }

    private async void OnUploadImageClick(object sender, RoutedEventArgs e)
    {
        var session = _session;
        if (session is null) return;

        if (!session.CanReplaceImage)
        {
            MessageBox.Show(
                "房间里已经有队友加入了，地图图片已固定（队友已按哈希缓存同一张图）。\n如需更换地图，请通过「文件 → 新建房间」重新开房。",
                "更换地图", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择地图图片",
            Filter = ImageFile.Filter,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        var loaded = await ImageFile.LoadAsync(dialog.FileName);
        if (loaded is null)
        {
            MessageBox.Show("无法读取该图片。", "上传地图", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (session.ReplaceLocalImage(loaded.Value.Bytes, loaded.Value.Width, loaded.Value.Height))
        {
            _settings.LastImagePath = dialog.FileName;
            _settings.Save();
            _vm.SetStatus($"地图已更新为 {Path.GetFileName(dialog.FileName)}");
            InvalidateCanvas();
        }
    }

    private void OnClearCacheClick(object sender, RoutedEventArgs e)
    {
        var count = App.Cache.GetFileCount();
        var size = App.Cache.GetTotalSizeBytes() / 1024.0 / 1024.0;
        var confirm = MessageBox.Show(
            $"将删除 {count} 个缓存文件（约 {size:F1} MB）。\n下次加入房间时会重新从房主获取地图图片。\n\n确定清空缓存？",
            "清空缓存", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        var removed = App.Cache.Clear();
        _vm.SetStatus($"已清空缓存（删除 {removed} 个文件）");
    }

    private void OnPlayerListClick(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        new PlayerListDialog(_session) { Owner = this }.ShowDialog();
    }

    private async void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        var confirm = MessageBox.Show("确定断开当前房间？断线后可以重新创建或加入房间。",
            "断开连接", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        await _vm.DetachSessionAsync();
        _session = null;
        InvalidateCanvas();
    }

    private void OnToggleGridClick(object sender, RoutedEventArgs e)
    {
        _vm.ToggleGrid();
        InvalidateCanvas();
    }

    private void OnGridSettingsClick(object sender, RoutedEventArgs e)
    {
        var map = _vm.Map;
        if (map is null) return;
        var result = GridSettingsDialog.Ask(this, map.GridCols, map.GridRows);
        if (result is null) return;
        _vm.SetGridSize(result.Value.Cols, result.Value.Rows);
        InvalidateCanvas();
    }

    private void OnResetViewClick(object sender, RoutedEventArgs e)
    {
        _vm.ResetView();
        InvalidateCanvas();
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "塔塔战术平板 · Tarkov Tactical Map\n\n" +
            "小队内部使用的战术地图白板工具。\n" +
            "手动上传地图图片，放置语义图标 / 文本 / 箭头 / 圆圈 / 方框，\n" +
            "所有标记通过 TCP 在房主与队友之间可靠同步。\n\n" +
            "不读取游戏内存 / 日志 / 网络封包，不注入进程，不做自动房间发现。\n" +
            "协议版本 v" + Core.Protocol.ProtocolConstants.ProtocolVersion + "\n\n" +
            "缓存目录：" + App.Cache.Root,
            "关于", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    // ── 关闭清理 ────────────────────────────────────────────────────────

    private async void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_cleanupStarted) return;
        _cleanupStarted = true;
        e.Cancel = true;
        try
        {
            await _vm.DetachSessionAsync();
        }
        catch
        {
            // 关闭流程中的异常忽略
        }
        Close();
    }
}
