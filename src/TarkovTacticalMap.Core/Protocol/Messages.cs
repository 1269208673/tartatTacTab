using MessagePack;
using TarkovTacticalMap.Core.Models;

namespace TarkovTacticalMap.Core.Protocol;

/// <summary>C→S 连接后第一条消息。</summary>
[MessagePackObject]
public sealed class HelloMessage
{
    [Key(0)]
    public int ProtocolVersion { get; set; } = ProtocolConstants.ProtocolVersion;

    /// <summary>房间口令的 SHA-256 小写 hex。</summary>
    [Key(1)]
    public string RoomPasswordHash { get; set; } = string.Empty;

    [Key(2)]
    public string PlayerName { get; set; } = string.Empty;
}

/// <summary>S→C 认证成功，含全量快照。</summary>
[MessagePackObject]
public sealed class WelcomeMessage
{
    [Key(0)]
    public string PlayerId { get; set; } = string.Empty;

    [Key(1)]
    public MapInfo MapInfo { get; set; } = new();

    [Key(2)]
    public Marker[] Markers { get; set; } = Array.Empty<Marker>();

    [Key(3)]
    public PlayerInfo[] Players { get; set; } = Array.Empty<PlayerInfo>();
}

/// <summary>S→C 认证失败 / 版本不匹配。</summary>
[MessagePackObject]
public sealed class ErrorMessage
{
    [Key(0)]
    public ErrorCode Code { get; set; }

    [Key(1)]
    public string Message { get; set; } = string.Empty;

    /// <summary>被拒绝的请求类型（可选，便于定位问题）。</summary>
    [Key(2)]
    public MessageType? RelatedType { get; set; }
}

/// <summary>C→S 请求地图图片。</summary>
[MessagePackObject]
public sealed class MapRequestMessage
{
    [Key(0)]
    public string ImageHash { get; set; } = string.Empty;
}

/// <summary>S→C 地图图片分块。</summary>
[MessagePackObject]
public sealed class MapChunkMessage
{
    [Key(0)]
    public string ImageHash { get; set; } = string.Empty;

    [Key(1)]
    public int Index { get; set; }

    [Key(2)]
    public int Total { get; set; }

    [Key(3)]
    public byte[] Data { get; set; } = Array.Empty<byte>();
}

/// <summary>C→S 新增标记 / S→C 广播新增。</summary>
[MessagePackObject]
public sealed class MarkerMessage
{
    [Key(0)]
    public Marker Marker { get; set; } = new();
}

/// <summary>C→S 全量更新标记。</summary>
[MessagePackObject]
public sealed class MarkerUpdateMessage
{
    [Key(0)]
    public string Id { get; set; } = string.Empty;

    [Key(1)]
    public Marker Marker { get; set; } = new();
}

/// <summary>C→S 删除标记 / S→C 广播删除。</summary>
[MessagePackObject]
public sealed class MarkerIdMessage
{
    [Key(0)]
    public string Id { get; set; } = string.Empty;
}

/// <summary>S→C 玩家加入。</summary>
[MessagePackObject]
public sealed class PlayerMessage
{
    [Key(0)]
    public PlayerInfo Player { get; set; } = new();
}

/// <summary>S→C 玩家离开。</summary>
[MessagePackObject]
public sealed class PlayerLeftMessage
{
    [Key(0)]
    public string PlayerId { get; set; } = string.Empty;
}

/// <summary>心跳（体为空对象）。</summary>
[MessagePackObject]
public sealed class PingMessage
{
}

/// <summary>心跳响应（体为空对象）。</summary>
[MessagePackObject]
public sealed class PongMessage
{
}
