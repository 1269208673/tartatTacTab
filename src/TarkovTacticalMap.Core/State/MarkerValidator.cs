using TarkovTacticalMap.Core.Models;
using TarkovTacticalMap.Core.Utils;

namespace TarkovTacticalMap.Core.State;

/// <summary>标记校验结果。</summary>
public enum ValidateStatus
{
    Ok = 0,
    InvalidId = 1,
    InvalidType = 2,
    InvalidColor = 3,
}

/// <summary>
/// 标记合法性校验与规范化：坐标 clamp 到 0~1、Size 限幅、Rotation 归一化、颜色规范化、
/// 文本长度截断。所有进出网络/状态表的标记都应先经过这里。
/// </summary>
public static class MarkerValidator
{
    /// <summary>Size 上限（归一化，相对图宽）。</summary>
    public const float MaxSize = 5f;

    /// <summary>文本最大长度。</summary>
    public const int MaxTextLength = 500;

    /// <summary>在副本上做规范化，不修改入参。</summary>
    public static Marker Normalize(Marker source)
    {
        var m = source.Clone();

        m.X = Clamp01(m.X);
        m.Y = Clamp01(m.Y);
        m.EndX = Clamp01(m.EndX);
        m.EndY = Clamp01(m.EndY);
        m.Size = Math.Clamp(m.Size, 0f, MaxSize);
        m.Rotation = NormalizeRotation(m.Rotation);

        var color = ColorHex.Normalize(m.Color);
        m.Color = color ?? ColorHex.Default;

        if (m.Text is { Length: > MaxTextLength })
        {
            m.Text = m.Text[..MaxTextLength];
        }

        m.IconId = string.IsNullOrWhiteSpace(m.IconId) ? null : m.IconId.Trim();
        m.OwnerId ??= string.Empty;
        return m;
    }

    /// <summary>校验标记是否可被接受（Id 与非空 OwnerId 必须存在）。</summary>
    public static (ValidateStatus Status, Marker? Marker) Validate(Marker? source)
    {
        if (source is null) return (ValidateStatus.InvalidType, null);
        if (string.IsNullOrWhiteSpace(source.Id)) return (ValidateStatus.InvalidId, null);

        if (!Enum.IsDefined(typeof(MarkerType), source.Type))
        {
            // 未知 MarkerType：保留原值（向前兼容，渲染层回退），但要求至少不是负数
            if ((int)source.Type < 0) return (ValidateStatus.InvalidType, null);
        }

        var normalized = Normalize(source);
        return (ValidateStatus.Ok, normalized);
    }

    public static float Clamp01(float v) => float.IsNaN(v) ? 0f : Math.Clamp(v, 0f, 1f);

    /// <summary>把角度归一化到 [0, 360)。</summary>
    public static float NormalizeRotation(float degrees)
    {
        if (float.IsNaN(degrees) || float.IsInfinity(degrees)) return 0f;
        var r = degrees % 360f;
        if (r < 0f) r += 360f;
        return r;
    }
}
