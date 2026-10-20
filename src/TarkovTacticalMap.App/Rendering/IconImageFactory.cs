using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using TarkovTacticalMap.Core.Utils;

namespace TarkovTacticalMap.App.Rendering;

/// <summary>把内置图标渲染成 WPF ImageSource（工具栏按钮用）。</summary>
public static class IconImageFactory
{
    private static readonly Dictionary<(string, uint, int), BitmapSource> Cache = new();
    private static readonly object Gate = new();

    /// <summary>渲染图标为冻结的 BitmapSource；未知图标返回 null。</summary>
    public static BitmapSource? Get(string? iconId, string? color = null, int sizePixels = 24)
    {
        if (string.IsNullOrEmpty(iconId)) return null;
        var argb = ColorHex.ToArgb(color ?? "#FFFFFFFF");
        var key = (iconId, argb, sizePixels);

        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;

            try
            {
                using var bitmap = new SKBitmap(sizePixels, sizePixels, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var canvas = new SKCanvas(bitmap);
                canvas.Clear(SKColors.Transparent);
                var dest = new SKRect(0, 0, sizePixels, sizePixels);
                if (!IconLibrary.Default.TryDraw(canvas, iconId, dest, new SKColor(argb)))
                {
                    return null;
                }

                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                using var stream = new MemoryStream(data.ToArray());
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = stream;
                bmp.EndInit();
                bmp.Freeze();
                Cache[key] = bmp;
                return bmp;
            }
            catch
            {
                return null;
            }
        }
    }
}

/// <summary>IconId → ImageSource 的 XAML 转换器。</summary>
public sealed class IconImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var color = parameter as string;
        return IconImageFactory.Get(value as string, color ?? "#FFE6E8EB", 24);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>颜色字符串 → WPF 画刷的转换器（色板按钮用）。</summary>
public sealed class ColorToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            var (a, r, g, b) = ColorHex.ToComponents(value as string);
            return new SolidColorBrush(Color.FromArgb(a, r, g, b));
        }
        catch
        {
            return Brushes.White;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
