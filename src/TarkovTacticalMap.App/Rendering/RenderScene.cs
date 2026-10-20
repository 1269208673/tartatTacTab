using TarkovTacticalMap.Core.Geometry;
using TarkovTacticalMap.Core.Models;

namespace TarkovTacticalMap.App.Rendering;

/// <summary>左侧工具栏的工具种类。</summary>
public enum ToolKind
{
    Select = 0,
    Kill = 1,
    Scout = 2,
    Document = 3,
    Key = 4,
    Target = 5,
    Arrow = 10,
    Circle = 11,
    Rectangle = 12,
    Line = 13,
    Text = 14,
}

/// <summary>工具 → 图标 Id（仅 Point 类工具）。</summary>
public static class ToolKinds
{
    public static bool IsPointTool(ToolKind tool)
        => tool is ToolKind.Kill or ToolKind.Scout or ToolKind.Document or ToolKind.Key or ToolKind.Target;

    public static string? IconIdOf(ToolKind tool) => tool switch
    {
        ToolKind.Kill => "kill",
        ToolKind.Scout => "scout",
        ToolKind.Document => "document",
        ToolKind.Key => "key",
        ToolKind.Target => "target",
        _ => null,
    };

    public static MarkerType MarkerTypeOf(ToolKind tool) => tool switch
    {
        ToolKind.Arrow => MarkerType.Arrow,
        ToolKind.Circle => MarkerType.Circle,
        ToolKind.Rectangle => MarkerType.Rectangle,
        ToolKind.Line => MarkerType.Line,
        ToolKind.Text => MarkerType.Text,
        _ => MarkerType.Point,
    };

    public static string LabelOf(ToolKind tool) => tool switch
    {
        ToolKind.Select => "选择",
        ToolKind.Kill => "击杀",
        ToolKind.Scout => "踩点",
        ToolKind.Document => "文件",
        ToolKind.Key => "钥匙",
        ToolKind.Target => "目标",
        ToolKind.Arrow => "箭头",
        ToolKind.Circle => "圆圈",
        ToolKind.Rectangle => "方框",
        ToolKind.Line => "直线",
        ToolKind.Text => "文本",
        _ => tool.ToString(),
    };
}

/// <summary>渲染场景：一次绘制所需的全部状态。渲染层只读，不反改状态。</summary>
public sealed class RenderScene
{
    public ViewportState Viewport { get; set; } = new();

    public MapInfo? Map { get; set; }

    /// <summary>已按 CreatedAt 排序的标记（新标记在上层）。</summary>
    public IReadOnlyList<Marker> Markers { get; set; } = Array.Empty<Marker>();

    /// <summary>当前选中标记 Id。</summary>
    public string? SelectedMarkerId { get; set; }

    /// <summary>临时绘制层：正在拖拽创建 / 拖拽编辑中的标记（覆盖同 Id 的正式标记）。</summary>
    public Marker? Draft { get; set; }

    /// <summary>底图（原图尺寸，绘制时缩放到图片显示矩形）。</summary>
    public SkiaSharp.SKImage? Background { get; set; }

    public IconLibrary Icons { get; set; } = IconLibrary.Default;

    /// <summary>鼠标当前所在网格标签（如 "C3"），用于高亮。</summary>
    public string? HoverGridLabel { get; set; }

    /// <summary>背景色。</summary>
    public SkiaSharp.SKColor BackdropColor { get; set; } = new(0xFF1B1D22);
}
