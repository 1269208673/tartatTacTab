namespace TarkovTacticalMap.Core.Utils;

/// <summary>房间口令处理：协议只传 SHA-256 小写 hex，不传明文。</summary>
public static class RoomPassword
{
    /// <summary>空口令的哈希（允许无口令房间）。</summary>
    public static readonly string EmptyHash = HashUtil.Sha256Hex(string.Empty);

    /// <summary>把明文口令转为 SHA-256 小写 hex。</summary>
    public static string Hash(string? password) => HashUtil.Sha256Hex(password ?? string.Empty);

    /// <summary>比较两个口令哈希是否一致（大小写不敏感）。</summary>
    public static bool Matches(string? hash, string? passwordHash)
        => string.Equals(hash ?? string.Empty, passwordHash ?? string.Empty, StringComparison.OrdinalIgnoreCase);
}
