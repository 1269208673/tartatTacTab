using SkiaSharp;

namespace TarkovTacticalMap.App.Rendering;

/// <summary>生成一张空白网格底图（PNG 字节），用于「不选图片直接创建房间」的场景。</summary>
public static class BlankMapFactory
{
    public const int DefaultWidth = 1600;
    public const int DefaultHeight = 1200;

    /// <summary>生成空白地图 PNG。</summary>
    public static byte[] CreatePng(int width = DefaultWidth, int height = DefaultHeight, string hint = "空白地图 · 未上传图片")
    {
        width = Math.Clamp(width, 64, 8192);
        height = Math.Clamp(height, 64, 8192);

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(0xFF23262D));

        // 大格辅助纹理
        using (var texture = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, Color = new SKColor(0x14FFFFFF) })
        {
            const int step = 100;
            for (var x = step; x < width; x += step) canvas.DrawLine(x, 0, x, height, texture);
            for (var y = step; y < height; y += step) canvas.DrawLine(0, y, width, y, texture);
        }

        // 中心十字
        using (var cross = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f, Color = new SKColor(0x33FFFFFF) })
        {
            canvas.DrawLine(width / 2f, height / 2f - 40, width / 2f, height / 2f + 40, cross);
            canvas.DrawLine(width / 2f - 40, height / 2f, width / 2f + 40, height / 2f, cross);
        }

        // 提示文字
        using (var typeface = SKTypeface.FromFamilyName("Microsoft YaHei") ?? SKTypeface.Default)
        using (var paint = new SKPaint
        {
            IsAntialias = true,
            Typeface = typeface,
            TextSize = Math.Clamp(width * 0.022f, 14f, 64f),
            TextAlign = SKTextAlign.Center,
            Color = new SKColor(0x66FFFFFF),
        })
        {
            canvas.DrawText(hint, width / 2f, height / 2f + paint.TextSize * 2.6f, paint);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
