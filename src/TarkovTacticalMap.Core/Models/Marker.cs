using MessagePack;

namespace TarkovTacticalMap.Core.Models;

/// <summary>
/// 地图标记。所有坐标归一化到 0~1（相对图片宽高），Size 相对图宽。
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

    /// <summary>归一化大小 / 线宽，相对图宽。</summary>
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
    };

    public override string ToString() =>
        $"{Type} {Id[..Math.Min(8, Id.Length)]} ({X:F3},{Y:F3}) size={Size:F3} color={Color}";
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

    /// <summary>某一类型新建时的默认 Size。</summary>
    public static float DefaultSizeFor(MarkerType type) => type switch
    {
        MarkerType.Point => PointSize,
        MarkerType.Text => TextSize,
        MarkerType.Arrow => ArrowSize,
        MarkerType.Line => LineSize,
        MarkerType.Circle => CircleSize,
        MarkerType.Rectangle => RectangleSize,
        _ => PointSize,
    };
}
