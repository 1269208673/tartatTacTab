using MessagePack;

namespace TarkovTacticalMap.Core.Protocol;

/// <summary>
/// 顶层信封：[4 字节大端 int32 长度][MessagePack(Envelope)]。
/// 用一层信封便于统一分发与版本演进。
/// </summary>
[MessagePackObject]
public sealed class Envelope
{
    [Key(0)]
    public MessageType Type { get; set; }

    /// <summary>具体消息体的 MessagePack 字节。</summary>
    [Key(1)]
    public byte[] Data { get; set; } = Array.Empty<byte>();

    public Envelope() { }

    public Envelope(MessageType type, byte[] data)
    {
        Type = type;
        Data = data;
    }
}
