using TarkovTacticalMap.Core.Models;
using TarkovTacticalMap.Core.Protocol;
using TarkovTacticalMap.Core.Serialization;
using TarkovTacticalMap.Network;
using Xunit;

namespace TarkovTacticalMap.Tests;

/// <summary>帧格式与消息序列化测试（对应文档 5.1 / 9）。</summary>
public class ProtocolFrameTests
{
    [Fact]
    public async Task Frame_RoundTrip_KeepsEnvelopeAndBody()
    {
        using var stream = new MemoryStream();
        var body = new HelloMessage { ProtocolVersion = ProtocolConstants.ProtocolVersion, RoomPasswordHash = "abc", PlayerName = "队友A" };

        await FrameCodec.WriteAsync(stream, new Envelope(MessageType.Hello, MessagePackConfig.Serialize(body)));
        stream.Position = 0;

        var envelope = await FrameCodec.ReadAsync(stream);
        Assert.NotNull(envelope);
        Assert.Equal(MessageType.Hello, envelope!.Type);

        var decoded = MessagePackConfig.DeserializeBody<HelloMessage>(envelope);
        Assert.Equal(ProtocolConstants.ProtocolVersion, decoded.ProtocolVersion);
        Assert.Equal("abc", decoded.RoomPasswordHash);
        Assert.Equal("队友A", decoded.PlayerName);
    }

    [Fact]
    public async Task Frame_HeaderIsBigEndianInt32Length()
    {
        using var stream = new MemoryStream();
        await FrameCodec.WriteAsync(stream, new Envelope(MessageType.Ping, MessagePackConfig.Serialize(new PingMessage())));

        var bytes = stream.ToArray();
        var declared = (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
        Assert.Equal(bytes.Length - FrameCodec.HeaderLength, declared);
        Assert.Equal(declared, bytes.Length - 4);
    }

    [Fact]
    public async Task Read_ReturnsNull_OnCleanEof()
    {
        using var stream = new MemoryStream();
        var envelope = await FrameCodec.ReadAsync(stream);
        Assert.Null(envelope);
    }

    [Fact]
    public async Task Read_ThrowsProtocolException_WhenHeaderTruncated()
    {
        using var stream = new MemoryStream(new byte[] { 0, 0 });
        await Assert.ThrowsAsync<ProtocolException>(async () => await FrameCodec.ReadAsync(stream));
    }

    [Fact]
    public async Task Read_ThrowsProtocolException_WhenPayloadTruncated()
    {
        using var stream = new MemoryStream(new byte[] { 0, 0, 0, 16, 1, 2, 3 });
        await Assert.ThrowsAsync<ProtocolException>(async () => await FrameCodec.ReadAsync(stream));
    }

    [Fact]
    public async Task Read_ThrowsProtocolException_WhenFrameTooLarge()
    {
        var header = new byte[4];
        var length = ProtocolConstants.MaxFrameSize + 1;
        header[0] = (byte)(length >> 24);
        header[1] = (byte)(length >> 16);
        header[2] = (byte)(length >> 8);
        header[3] = (byte)length;

        using var stream = new MemoryStream(header);
        var ex = await Assert.ThrowsAsync<ProtocolException>(async () => await FrameCodec.ReadAsync(stream));
        Assert.Equal(ErrorCode.ProtocolError, ex.Code);
    }

    [Fact]
    public async Task Write_RejectsPayloadOverLimit()
    {
        using var stream = new MemoryStream();
        var payload = new byte[ProtocolConstants.MaxFrameSize + 10];
        await Assert.ThrowsAsync<ProtocolException>(async () => await FrameCodec.WritePayloadAsync(stream, payload));
    }

    [Fact]
    public async Task MultipleFrames_AreReadInOrder()
    {
        using var stream = new MemoryStream();
        for (var i = 0; i < 5; i++)
        {
            await FrameCodec.WriteAsync(stream,
                new Envelope(MessageType.MarkerDeleted, MessagePackConfig.Serialize(new MarkerIdMessage { Id = $"id-{i}" })));
        }
        stream.Position = 0;

        for (var i = 0; i < 5; i++)
        {
            var envelope = await FrameCodec.ReadAsync(stream);
            Assert.NotNull(envelope);
            var body = MessagePackConfig.DeserializeBody<MarkerIdMessage>(envelope!);
            Assert.Equal($"id-{i}", body.Id);
        }
        Assert.Null(await FrameCodec.ReadAsync(stream));
    }

    [Fact]
    public void Marker_Serialization_RoundTripsAllFields()
    {
        var marker = new Marker
        {
            Id = "m1",
            Type = MarkerType.Arrow,
            X = 0.25f,
            Y = 0.75f,
            Size = 0.12f,
            Color = "#80FF0000",
            Rotation = 45.5f,
            Text = "文本",
            EndX = 0.5f,
            EndY = 0.6f,
            IconId = "kill",
            OwnerId = "player-1",
            CreatedAt = 1700000000000,
        };

        var bytes = MessagePackConfig.Serialize(marker);
        var back = MessagePackConfig.Deserialize<Marker>(bytes);

        Assert.Equal(marker.Id, back.Id);
        Assert.Equal(marker.Type, back.Type);
        Assert.Equal(marker.X, back.X, 4);
        Assert.Equal(marker.Y, back.Y, 4);
        Assert.Equal(marker.Size, back.Size, 4);
        Assert.Equal(marker.Color, back.Color);
        Assert.Equal(marker.Rotation, back.Rotation, 4);
        Assert.Equal(marker.Text, back.Text);
        Assert.Equal(marker.EndX, back.EndX, 4);
        Assert.Equal(marker.EndY, back.EndY, 4);
        Assert.Equal(marker.IconId, back.IconId);
        Assert.Equal(marker.OwnerId, back.OwnerId);
        Assert.Equal(marker.CreatedAt, back.CreatedAt);
    }

    [Fact]
    public void Welcome_Serialization_RoundTripsNestedCollections()
    {
        var welcome = new WelcomeMessage
        {
            PlayerId = "p1",
            MapInfo = MapInfo.CreateBlank("hash", 100, 200),
            Markers = new[] { new Marker { Id = "a", Type = MarkerType.Circle, Size = 0.1f } },
            Players = new[] { new PlayerInfo { PlayerId = "p1", PlayerName = "队友", IsHost = false } },
        };

        var back = MessagePackConfig.Deserialize<WelcomeMessage>(MessagePackConfig.Serialize(welcome));
        Assert.Equal("p1", back.PlayerId);
        Assert.Equal(100, back.MapInfo.ImageWidth);
        Assert.Single(back.Markers);
        Assert.Single(back.Players);
        Assert.False(back.Players[0].IsHost);
    }

    [Fact]
    public void MapChunk_Serialization_KeepsBinaryData()
    {
        var data = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var chunk = new MapChunkMessage { ImageHash = "h", Index = 3, Total = 9, Data = data };
        var back = MessagePackConfig.Deserialize<MapChunkMessage>(MessagePackConfig.Serialize(chunk));

        Assert.Equal("h", back.ImageHash);
        Assert.Equal(3, back.Index);
        Assert.Equal(9, back.Total);
        Assert.Equal(data, back.Data);
    }
}
