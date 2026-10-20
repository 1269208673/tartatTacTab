namespace TarkovTacticalMap.Core.Protocol;

/// <summary>
/// 消息类型。数值即协议的一部分，新增类型追加在末尾，不复用旧值。
/// </summary>
public enum MessageType : int
{
    /// <summary>C→S 连接后第一条消息。</summary>
    Hello = 1,

    /// <summary>S→C 认证成功 + 全量快照。</summary>
    Welcome = 2,

    /// <summary>S→C 认证失败 / 版本不匹配，随后断开。</summary>
    Error = 3,

    /// <summary>C→S 请求地图图片。</summary>
    MapRequest = 4,

    /// <summary>S→C 地图图片分块。</summary>
    MapChunk = 5,

    /// <summary>C→S 请求新增标记。</summary>
    MarkerAdd = 10,

    /// <summary>C→S 请求全量更新标记。</summary>
    MarkerUpdate = 11,

    /// <summary>C→S 请求删除标记。</summary>
    MarkerDelete = 12,

    /// <summary>S→C 权威广播：新增。</summary>
    MarkerAdded = 20,

    /// <summary>S→C 权威广播：更新。</summary>
    MarkerUpdated = 21,

    /// <summary>S→C 权威广播：删除。</summary>
    MarkerDeleted = 22,

    /// <summary>S→C 玩家加入。</summary>
    PlayerJoined = 30,

    /// <summary>S→C 玩家离开。</summary>
    PlayerLeft = 31,

    /// <summary>双向心跳。</summary>
    Ping = 40,

    /// <summary>双向心跳响应。</summary>
    Pong = 41,
}

/// <summary>错误码。</summary>
public enum ErrorCode : int
{
    None = 0,

    /// <summary>口令错误。</summary>
    AuthFailed = 1,

    /// <summary>协议版本不匹配。</summary>
    ProtocolVersionMismatch = 2,

    /// <summary>无权限操作该标记（非房主操作他人标记）。</summary>
    PermissionDenied = 3,

    /// <summary>请求内容不合法。</summary>
    InvalidRequest = 4,

    /// <summary>帧格式错误（超长帧等），随后断开。</summary>
    ProtocolError = 5,

    /// <summary>房主没有该哈希对应的地图图片。</summary>
    MapUnavailable = 6,

    /// <summary>房主关闭房间 / 主动断开。</summary>
    ServerClosed = 7,

    /// <summary>未在超时时间内收到 HELLO。</summary>
    HandshakeTimeout = 8,
}

/// <summary>协议常量。</summary>
public static class ProtocolConstants
{
    /// <summary>
    /// 协议版本。数据模型 / 消息表变更时递增，房主据此提示客户端版本不一致。
    /// 1 = 首版（M1~M6 完整消息表）。
    /// </summary>
    public const int ProtocolVersion = 1;

    /// <summary>单帧 payload 上限：8MB，超过视为协议错误并断开。</summary>
    public const int MaxFrameSize = 8 * 1024 * 1024;

    /// <summary>地图图片分块大小：64KB。</summary>
    public const int MapChunkSize = 64 * 1024;

    /// <summary>心跳间隔：5 秒。</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    /// <summary>心跳超时：30 秒内没有任何响应视为断线。</summary>
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(30);

    /// <summary>客户端重连退避上限：30 秒。</summary>
    public static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(30);

    /// <summary>默认监听端口。</summary>
    public const int DefaultPort = 27015;
}
