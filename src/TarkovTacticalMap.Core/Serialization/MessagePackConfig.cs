using MessagePack;
using MessagePack.Resolvers;
using TarkovTacticalMap.Core.Protocol;

namespace TarkovTacticalMap.Core.Serialization;

/// <summary>
/// 统一的 MessagePack 配置。模型类使用 [MessagePackObject] + [Key(n)]，
/// 同时以 ContractlessStandardResolver 兜底，保证跨版本兼容。
/// </summary>
public static class MessagePackConfig
{
    /// <summary>全局序列化选项。</summary>
    public static readonly MessagePackSerializerOptions Options =
        MessagePackSerializerOptions.Standard
            .WithResolver(ContractlessStandardResolver.Instance)
            .WithSecurity(MessagePackSecurity.UntrustedData);

    public static byte[] Serialize<T>(T value) => MessagePackSerializer.Serialize(value, Options);

    public static T Deserialize<T>(byte[] data) => MessagePackSerializer.Deserialize<T>(data, Options);

    /// <summary>序列化 Envelope（含消息体）。</summary>
    public static byte[] SerializeEnvelope<TBody>(MessageType type, TBody body)
        => Serialize(new Envelope(type, Serialize(body)));

    /// <summary>取出 Envelope 并反序列化消息体。</summary>
    public static TBody DeserializeBody<TBody>(Envelope envelope) => Deserialize<TBody>(envelope.Data);
}
