using System.Buffers.Binary;
using TarkovTacticalMap.Core.Protocol;
using TarkovTacticalMap.Core.Serialization;

namespace TarkovTacticalMap.Network;

/// <summary>协议错误（帧格式非法、超长帧等）。</summary>
public sealed class ProtocolException : Exception
{
    public ProtocolException(string message, ErrorCode code = ErrorCode.ProtocolError) : base(message)
    {
        Code = code;
    }

    public ErrorCode Code { get; }
}

/// <summary>
/// 长度前缀帧编解码：
/// [4 字节大端 int32：payload 长度 N][N 字节 payload]
/// payload = MessagePack 序列化的 Envelope。
/// 单帧上限 8MB，超过视为协议错误。
/// </summary>
public static class FrameCodec
{
    /// <summary>帧头长度。</summary>
    public const int HeaderLength = 4;

    /// <summary>把 Envelope 序列化后写入流（含长度前缀）。</summary>
    public static async ValueTask WriteAsync(Stream stream, Envelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(envelope);
        var payload = MessagePackConfig.Serialize(envelope);
        await WritePayloadAsync(stream, payload, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按帧格式写入原始 payload 字节。</summary>
    public static async ValueTask WritePayloadAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (payload.Length > ProtocolConstants.MaxFrameSize)
        {
            throw new ProtocolException($"帧长度 {payload.Length} 超过上限 {ProtocolConstants.MaxFrameSize}");
        }

        var header = new byte[HeaderLength];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 读取一帧并反序列化为 Envelope。
    /// 对端正常关闭时返回 null；帧非法时抛 <see cref="ProtocolException"/>。
    /// </summary>
    public static async ValueTask<Envelope?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[HeaderLength];
        var read = await ReadAtLeastAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (read == 0) return null; // 对端正常关闭
        if (read < HeaderLength) throw new ProtocolException("帧头不完整，连接已中断");

        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 0) throw new ProtocolException($"帧长度为负：{length}");
        if (length > ProtocolConstants.MaxFrameSize)
        {
            throw new ProtocolException($"帧长度 {length} 超过上限 {ProtocolConstants.MaxFrameSize}");
        }

        if (length == 0) return new Envelope(MessageType.Ping, Array.Empty<byte>());

        var payload = new byte[length];
        var payloadRead = await ReadAtLeastAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        if (payloadRead < length) throw new ProtocolException("帧不完整，连接已中断");

        try
        {
            var envelope = MessagePackConfig.Deserialize<Envelope>(payload);
            return envelope ?? throw new ProtocolException("信封反序列化失败");
        }
        catch (ProtocolException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ProtocolException($"信封反序列化失败：{ex.Message}");
        }
    }

    /// <summary>读取到缓冲区填满为止；返回实际读取字节数（0 表示流已结束）。</summary>
    public static async ValueTask<int> ReadAtLeastAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }
}
