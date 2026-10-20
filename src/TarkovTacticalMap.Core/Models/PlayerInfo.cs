using MessagePack;

namespace TarkovTacticalMap.Core.Models;

/// <summary>在线玩家信息。</summary>
[MessagePackObject]
public sealed class PlayerInfo
{
    [Key(0)]
    public string PlayerId { get; set; } = string.Empty;

    [Key(1)]
    public string PlayerName { get; set; } = string.Empty;

    [Key(2)]
    public bool IsHost { get; set; }

    public PlayerInfo Clone() => new() { PlayerId = PlayerId, PlayerName = PlayerName, IsHost = IsHost };

    public override string ToString() => IsHost ? $"{PlayerName}（房主）" : PlayerName;
}
