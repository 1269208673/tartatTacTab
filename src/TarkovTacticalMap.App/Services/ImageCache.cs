using System.IO;
using TarkovTacticalMap.Core.Utils;

namespace TarkovTacticalMap.App.Services;

/// <summary>
/// 地图图片本地缓存：%LocalAppData%/TarkovTacticalMap/cache/{imageHash}.img
/// 按哈希命名天然去重，不做自动清理（设置里提供「清空缓存」）。
/// </summary>
public sealed class ImageCache
{
    private readonly object _gate = new();

    public ImageCache(string? rootDirectory = null)
    {
        Root = rootDirectory ?? DefaultRoot;
        try { Directory.CreateDirectory(Root); } catch { /* 目录不可建时后续操作自会失败并返回 false */ }
    }

    /// <summary>默认缓存目录。</summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TarkovTacticalMap",
        "cache");

    public string Root { get; }

    /// <summary>清理哈希串，防止路径穿越。</summary>
    private static string SafeHash(string imageHash)
    {
        var cleaned = new string((imageHash ?? string.Empty)
            .Where(c => char.IsAsciiLetterOrDigit(c))
            .ToArray());
        return cleaned.ToLowerInvariant();
    }

    public string GetPath(string imageHash) => Path.Combine(Root, SafeHash(imageHash) + ".img");

    /// <summary>尝试读取并校验缓存（SHA-256 必须匹配）。</summary>
    public bool TryLoad(string imageHash, out byte[]? bytes)
    {
        bytes = null;
        var hash = SafeHash(imageHash);
        if (hash.Length == 0) return false;

        try
        {
            var path = GetPath(hash);
            if (!File.Exists(path)) return false;
            var data = File.ReadAllBytes(path);
            if (!HashUtil.VerifySha256(data, hash)) return false; // 缓存损坏，视为未命中
            bytes = data;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>写入缓存（返回是否成功）。</summary>
    public bool Save(string imageHash, byte[] bytes)
    {
        var hash = SafeHash(imageHash);
        if (hash.Length == 0 || bytes.Length == 0) return false;

        lock (_gate)
        {
            try
            {
                var path = GetPath(hash);
                Directory.CreateDirectory(Root);
                var temp = path + ".tmp";
                File.WriteAllBytes(temp, bytes);
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>缓存占用字节数。</summary>
    public long GetTotalSizeBytes()
    {
        try
        {
            if (!Directory.Exists(Root)) return 0;
            var total = 0L;
            foreach (var file in Directory.EnumerateFiles(Root, "*.img"))
            {
                try { total += new FileInfo(file).Length; } catch { /* ignore */ }
            }
            return total;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>缓存文件数。</summary>
    public int GetFileCount()
    {
        try
        {
            return Directory.Exists(Root) ? Directory.GetFiles(Root, "*.img").Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>清空缓存，返回删除的文件数。</summary>
    public int Clear()
    {
        lock (_gate)
        {
            var removed = 0;
            try
            {
                if (!Directory.Exists(Root)) return 0;
                foreach (var file in Directory.EnumerateFiles(Root, "*.img"))
                {
                    try
                    {
                        File.Delete(file);
                        removed++;
                    }
                    catch { /* 占用中的文件跳过 */ }
                }
                foreach (var file in Directory.EnumerateFiles(Root, "*.tmp"))
                {
                    try { File.Delete(file); } catch { /* ignore */ }
                }
            }
            catch
            {
                // 忽略目录访问错误
            }
            return removed;
        }
    }
}
