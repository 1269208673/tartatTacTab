using TarkovTacticalMap.Core.Models;
using TarkovTacticalMap.Core.Protocol;

namespace TarkovTacticalMap.Network;

/// <summary>连接状态。</summary>
public enum ConnectionState
{
    /// <summary>未启动。</summary>
    Idle = 0,

    /// <summary>首次连接中。</summary>
    Connecting = 1,

    /// <summary>已连接（WELCOME 已完成）。</summary>
    Connected = 2,

    /// <summary>断线重连中。</summary>
    Reconnecting = 3,

    /// <summary>致命错误（口令错误 / 版本不匹配），不会自动重连。</summary>
    Faulted = 4,

    /// <summary>已主动停止。</summary>
    Stopped = 5,
}

/// <summary>标记事件参数。</summary>
public sealed class MarkerEventArgs : EventArgs
{
    public MarkerEventArgs(Marker marker) => Marker = marker;

    public Marker Marker { get; }
}

/// <summary>标记删除事件参数。</summary>
public sealed class MarkerDeletedEventArgs : EventArgs
{
    public MarkerDeletedEventArgs(string id) => Id = id;

    public string Id { get; }
}

/// <summary>玩家事件参数。</summary>
public sealed class PlayerEventArgs : EventArgs
{
    public PlayerEventArgs(PlayerInfo player) => Player = player;

    public PlayerInfo Player { get; }
}

/// <summary>玩家离开事件参数。</summary>
public sealed class PlayerLeftEventArgs : EventArgs
{
    public PlayerLeftEventArgs(string playerId) => PlayerId = playerId;

    public string PlayerId { get; }
}

/// <summary>连接/断开事件参数。</summary>
public sealed class ConnectionEventArgs : EventArgs
{
    public ConnectionEventArgs(string? reason, bool willRetry, ErrorCode code = ErrorCode.None)
    {
        Reason = reason;
        WillRetry = willRetry;
        Code = code;
    }

    public string? Reason { get; }

    /// <summary>是否会自动重连。</summary>
    public bool WillRetry { get; }

    public ErrorCode Code { get; }
}
