using System.Security.Cryptography;
using System.Text;

namespace TarkovTacticalMap.Core.Utils;

/// <summary>SHA-256 / 哈希工具。地图图片与房间口令都用小写 hex 表示。</summary>
public static class HashUtil
{
    /// <summary>计算字节数组的 SHA-256，返回小写 hex。</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> data)
        => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>计算字节数组的 SHA-256，返回原始 32 字节。</summary>
    public static byte[] Sha256Bytes(ReadOnlySpan<byte> data) => SHA256.HashData(data);

    /// <summary>计算字符串的 SHA-256，返回小写 hex。</summary>
    public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text ?? string.Empty));

    /// <summary>异步计算文件（或流）的 SHA-256，返回小写 hex。</summary>
    public static async Task<string> Sha256HexAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>异步计算文件的 SHA-256，返回小写 hex。</summary>
    public static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await Sha256HexAsync(fs, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>校验字节数据是否与给定小写 hex 哈希一致。</summary>
    public static bool VerifySha256(ReadOnlySpan<byte> data, string expectedHex)
    {
        if (string.IsNullOrEmpty(expectedHex)) return false;
        var actual = Sha256Hex(data);
        return string.Equals(actual, expectedHex.Trim().ToLowerInvariant(), StringComparison.Ordinal);
    }
}
