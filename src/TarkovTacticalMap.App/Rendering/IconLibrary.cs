using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace TarkovTacticalMap.App.Rendering;

/// <summary>
/// 内置语义图标（kill / scout / document / key / target）。
/// 资源以 SVG 形式内嵌在程序集里（Assets/Icons/{iconId}.svg），
/// 运行时解析其中的 path 数据为 SKPath，用 SKPaint.Color 直接着色（单一实现，不混用蒙版染色）。
/// 未知 IconId 返回 false，由渲染层回退为默认圆点。
/// </summary>
public sealed partial class IconLibrary
{
    /// <summary>协议约定的五种 IconId，顺序与工具栏一致。</summary>
    public static readonly string[] IconIds = { "kill", "scout", "document", "key", "target" };

    /// <summary>中文标签（工具栏显示）。</summary>
    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["kill"] = "击杀",
        ["scout"] = "踩点",
        ["document"] = "文件",
        ["key"] = "钥匙",
        ["target"] = "目标",
    };

    /// <summary>SVG viewBox 边长（所有内置图标统一 0 0 24 24）。</summary>
    public const float ViewBoxSize = 24f;

    private readonly Dictionary<string, SKPath> _paths = new(StringComparer.OrdinalIgnoreCase);

    public static IconLibrary Default { get; } = new();

    public IconLibrary() : this(typeof(IconLibrary).Assembly)
    {
    }

    public IconLibrary(Assembly assembly)
    {
        foreach (var iconId in IconIds)
        {
            var path = LoadIcon(assembly, iconId);
            if (path is not null) _paths[iconId] = path;
        }
    }

    /// <summary>已成功加载的图标集合。</summary>
    public IReadOnlyCollection<string> Loaded => _paths.Keys;

    /// <summary>是否存在该图标（未知 IconId 返回 false）。</summary>
    public bool Contains(string? iconId)
        => !string.IsNullOrEmpty(iconId) && _paths.ContainsKey(iconId);

    [GeneratedRegex("d\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex PathDataRegex();

    private static SKPath? LoadIcon(Assembly assembly, string iconId)
    {
        try
        {
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith($".{iconId}.svg", StringComparison.OrdinalIgnoreCase));
            if (resourceName is null) return null;

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null) return null;
            using var reader = new StreamReader(stream);
            var svg = reader.ReadToEnd();

            var combined = new SKPath { FillType = SKPathFillType.EvenOdd };
            var any = false;
            foreach (Match match in PathDataRegex().Matches(svg))
            {
                var data = match.Groups[1].Value;
                using var parsed = SKPath.ParseSvgPathData(data);
                if (parsed is null) continue;
                combined.AddPath(parsed);
                any = true;
            }

            if (!any)
            {
                combined.Dispose();
                return null;
            }
            return combined;
        }
        catch
        {
            return null; // 图标资源损坏不影响主流程
        }
    }

    /// <summary>
    /// 在 dest 矩形内绘制图标，用 color 着色，围绕矩形中心旋转 rotationDegrees。
    /// 返回 false 表示图标不存在（调用方应回退）。
    /// </summary>
    public bool TryDraw(SKCanvas canvas, string? iconId, SKRect dest, SKColor color, float rotationDegrees = 0f)
    {
        if (string.IsNullOrEmpty(iconId) || !_paths.TryGetValue(iconId, out var path)) return false;
        if (dest.Width <= 0.5f || dest.Height <= 0.5f) return false;

        var state = canvas.Save();
        try
        {
            canvas.Translate(dest.MidX, dest.MidY);
            if (MathF.Abs(rotationDegrees) > 0.01f) canvas.RotateDegrees(rotationDegrees);
            canvas.Scale(dest.Width / ViewBoxSize, dest.Height / ViewBoxSize);
            canvas.Translate(-ViewBoxSize / 2f, -ViewBoxSize / 2f);

            using var paint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                Color = color,
            };
            canvas.DrawPath(path, paint);
            return true;
        }
        finally
        {
            canvas.RestoreToCount(state);
        }
    }
}
