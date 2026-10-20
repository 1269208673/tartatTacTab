using System.IO;
using SkiaSharp;

namespace TarkovTacticalMap.App.Services;

/// <summary>地图图片文件读取（含像素尺寸探测），不阻塞 UI 线程的耗时部分放在 Task.Run。</summary>
public static class ImageFile
{
    /// <summary>支持的图片扩展名（SkiaSharp 可解码的常见格式）。</summary>
    public static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".gif" };

    public static string Filter =>
        "图片文件|" + string.Join(";", Extensions.Select(e => "*" + e)) + "|所有文件|*.*";

    /// <summary>读取图片字节并探测尺寸；失败返回 null。</summary>
    public static async Task<(byte[] Bytes, int Width, int Height)?> LoadAsync(string path)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            var size = await Task.Run(() => DetectSize(bytes)).ConfigureAwait(false);
            if (size is null) return null;
            return (bytes, size.Value.Width, size.Value.Height);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>探测图片像素尺寸。</summary>
    public static (int Width, int Height)? DetectSize(byte[] bytes)
    {
        try
        {
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            if (codec is null) return null;
            return (codec.Info.Width, codec.Info.Height);
        }
        catch
        {
            return null;
        }
    }
}
