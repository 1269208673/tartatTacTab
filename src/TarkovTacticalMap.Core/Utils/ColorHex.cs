using System.Globalization;
using System.Text.RegularExpressions;

namespace TarkovTacticalMap.Core.Utils;

/// <summary>
/// 颜色字符串工具。协议里 Color 为 "#RRGGBB" 或 "#AARRGGBB"。
/// 这里只做字符串 ↔ ARGB uint 的转换，不依赖任何 UI 类型。
/// </summary>
public static partial class ColorHex
{
    /// <summary>默认颜色（黄色，便于在地图上辨识）。</summary>
    public const string Default = "#FFE066";

    [GeneratedRegex("^#?(?:[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")]
    private static partial Regex ColorRegex();

    /// <summary>是否为合法的 "#RRGGBB" / "#AARRGGBB"（允许省略 #）。</summary>
    public static bool IsValid(string? color) => !string.IsNullOrWhiteSpace(color) && ColorRegex().IsMatch(color.Trim());

    /// <summary>把颜色字符串规范化为 "#AARRGGBB" 大写形式。非法输入返回 null。</summary>
    public static string? Normalize(string? color)
    {
        if (!IsValid(color)) return null;
        var hex = color!.Trim().TrimStart('#');
        if (hex.Length == 6) hex = "FF" + hex;
        return "#" + hex.ToUpperInvariant();
    }

    /// <summary>解析为 ARGB uint（0xAARRGGBB）。非法输入返回默认色。</summary>
    public static uint ToArgb(string? color)
    {
        var n = Normalize(color) ?? Normalize(Default)!;
        var hex = n.TrimStart('#');
        return uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    /// <summary>解析为 (A, R, G, B) 分量。</summary>
    public static (byte A, byte R, byte G, byte B) ToComponents(string? color)
    {
        var argb = ToArgb(color);
        return ((byte)((argb >> 24) & 0xFF), (byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));
    }

    /// <summary>由 ARGB 分量生成 "#AARRGGBB"。</summary>
    public static string FromComponents(byte a, byte r, byte g, byte b) => $"#{a:X2}{r:X2}{g:X2}{b:X2}";

    /// <summary>把颜色替换为指定透明度（0~255）后的 "#RRGGBB" / "#AARRGGBB"。</summary>
    public static string WithAlpha(string? color, byte alpha)
    {
        var (_, r, g, b) = ToComponents(color);
        return alpha == 255 ? $"#{r:X2}{g:X2}{b:X2}" : FromComponents(alpha, r, g, b);
    }
}
