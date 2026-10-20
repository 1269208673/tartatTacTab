using MessagePack;

namespace TarkovTacticalMap.Core.Models;

/// <summary>
/// 地图标记。所有坐标归一化到 0~1（相对图片宽高），Size / Thickness 相对图宽。
/// 字段编号一经发布不得复用，新增字段追加 [Key(n)]。
/// </summary>
[MessagePackObject]
public sealed class Marker
{
    /// <summary>GUID，创建端生成。</summary>
    [Key(0)]
    public string Id { get; set; } = string.Empty;

    [Key(1)]
    public MarkerType Type { get; set; }

    /// <summary>0~1，相对图片宽度。</summary>
    [Key(2)]
    public float X { get; set; }

    /// <summary>0~1，相对图片高度。</summary>
    [Key(3)]
    public float Y { get; set; }

    /// <summary>
    /// 归一化大小 / 几何尺寸，相对图宽。
    /// Point=图标宽度，Text=字号，Arrow=箭头长度，Line/Rectangle=线宽（Thickness 为 0 时的回退值），
    /// Circle=半径，Stroke=线宽（回退值）。
    /// </summary>
    [Key(4)]
    public float Size { get; set; }

    /// <summary>"#RRGGBB" 或 "#AARRGGBB"。</summary>
    [Key(5)]
    public string Color { get; set; } = MarkerDefaults.DefaultColor;

    /// <summary>0~360 度，顺时针。</summary>
    [Key(6)]
    public float Rotation { get; set; }

    /// <summary>仅 Type=Text 使用。</summary>
    [Key(7)]
    public string? Text { get; set; }

    /// <summary>仅 Type=Line/Rectangle 使用。</summary>
    [Key(8)]
    public float EndX { get; set; }

    /// <summary>仅 Type=Line/Rectangle 使用。</summary>
    [Key(9)]
    public float EndY { get; set; }

    /// <summary>仅 Type=Point 使用，null 表示默认圆点。</summary>
    [Key(10)]
    public string? IconId { get; set; }

    /// <summary>创建者玩家 ID。</summary>
    [Key(11)]
    public string OwnerId { get; set; } = string.Empty;

    /// <summary>Unix 毫秒，用于绘制排序。</summary>
    [Key(12)]
    public long CreatedAt { get; set; }

    /// <summary>
    /// 描边 / 线宽（归一化，相对图宽）。0 表示沿用旧行为（按类型从 Size 推导），
    /// 使箭头、圆圈、直线、方框的粗细可以与几何尺寸分开调整。
    /// </summary>
    [Key(13)]
    public float Thickness { get; set; }

    /// <summary>仅 Type=Stroke 使用：自由笔画的折线点（归一化坐标，按绘制顺序）。</summary>
    [Key(14)]
    public MarkerPoint[]? Points { get; set; }

    public Marker Clone() => new()
    {
        Id = Id,
        Type = Type,
        X = X,
        Y = Y,
        Size = Size,
        Color = Color,
        Rotation = Rotation,
        Text = Text,
        EndX = EndX,
        EndY = EndY,
        IconId = IconId,
        OwnerId = OwnerId,
        CreatedAt = CreatedAt,
        Thickness = Thickness,
        Points = Points is null ? null : (MarkerPoint[])Points.Clone(),
    };

    /// <summary>该类型是否使用 Thickness（独立的粗细）。</summary>
    [IgnoreMember]
    public bool UsesThickness => Type is MarkerType.Arrow or MarkerType.Circle
        or MarkerType.Line or MarkerType.Rectangle or MarkerType.Stroke;

    /// <summary>笔画的点数（非笔画为 0）。</summary>
    [IgnoreMember]
    public int PointCount => Points?.Length ?? 0;

    public override string ToString() =>
        $"{Type} {Id[..Math.Min(8, Id.Length)]} ({X:F3},{Y:F3}) size={Size:F3} thick={Thickness:F4} color={Color}";
}

/// <summary>归一化坐标点（用于自由笔画折线）。</summary>
[MessagePackObject]
public struct MarkerPoint
{
    [Key(0)]
    public float X { get; set; }

    [Key(1)]
    public float Y { get; set; }

    public MarkerPoint()
    {
    }

    public MarkerPoint(float x, float y)
    {
        X = x;
        Y = y;
    }

    public override string ToString() => $"({X:F3},{Y:F3})";
}

/// <summary>标记相关的默认值，供 Core / App 共同使用。</summary>
public static class MarkerDefaults
{
    public const string DefaultColor = "#FFE066";

    /// <summary>图标点标记默认大小（相对图宽）。</summary>
    public const float PointSize = 0.02f;

    public const float TextSize = 0.03f;
    public const float ArrowSize = 0.12f;
    public const float LineSize = 0.004f;
    public const float CircleSize = 0.06f;
    public const float RectangleSize = 0.004f;

    /// <summary>默认描边粗细（相对图宽）。</summary>
    public const float DefaultThickness = 0.005f;

    public const float ArrowThickness = 0.005f;
    public const float CircleThickness = 0.004f;
    public const float StrokeThickness = 0.005f;

    /// <summary>某一类型新建时的默认 Size。</summary>
    public static float DefaultSizeFor(MarkerType type) => type switch
    {
        MarkerType.Point => PointSize,
        MarkerType.Text => TextSize,
        MarkerType.Arrow => ArrowSize,
        MarkerType.Line => LineSize,
        MarkerType.Circle => CircleSize,
        MarkerType.Rectangle => RectangleSize,
        MarkerType.Stroke => StrokeThickness,
        _ => PointSize,
    };

    /// <summary>某一类型新建时的默认 Thickness（不使用粗细的类型返回 0）。</summary>
    public static float DefaultThicknessFor(MarkerType type) => type switch
    {
        MarkerType.Arrow => ArrowThickness,
        MarkerType.Circle => CircleThickness,
        MarkerType.Line => DefaultThickness,
        MarkerType.Rectangle => DefaultThickness,
        MarkerType.Stroke => StrokeThickness,
        _ => 0f,
    };
}
