using TarkovTacticalMap.Core.Protocol;
using TarkovTacticalMap.Core.Utils;
using TarkovTacticalMap.Network;
using Xunit;

namespace TarkovTacticalMap.Tests;

/// <summary>地图图片分块传输与哈希工具测试（对应文档 5.4）。</summary>
public class ImageTransferTests
{
    [Fact]
    public void ChunkCount_RoundsUp()
    {
        Assert.Equal(0, ImageTransfer.GetChunkCount(0));
        Assert.Equal(1, ImageTransfer.GetChunkCount(1));
        Assert.Equal(1, ImageTransfer.GetChunkCount(ImageTransfer.ChunkSize));
        Assert.Equal(2, ImageTransfer.GetChunkCount(ImageTransfer.ChunkSize + 1));
        Assert.Equal(5, ImageTransfer.GetChunkCount(ImageTransfer.ChunkSize * 5));
    }

    [Fact]
    public void CreateChunks_SplitsWithCorrectIndexesAndSizes()
    {
        var data = new byte[ImageTransfer.ChunkSize * 2 + 123];
        new Random(42).NextBytes(data);
        var hash = HashUtil.Sha256Hex(data);

        var chunks = ImageTransfer.CreateChunks(hash, data);

        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, c => Assert.Equal(3, c.Total));
        Assert.Equal(ImageTransfer.ChunkSize, chunks[0].Data.Length);
        Assert.Equal(ImageTransfer.ChunkSize, chunks[1].Data.Length);
        Assert.Equal(123, chunks[2].Data.Length);
        Assert.Equal(new[] { 0, 1, 2 }, chunks.Select(c => c.Index).ToArray());
    }

    [Fact]
    public void Assembler_ReassemblesAndVerifiesHash()
    {
        var data = new byte[ImageTransfer.ChunkSize * 3 + 777];
        new Random(7).NextBytes(data);
        var hash = HashUtil.Sha256Hex(data);
        var chunks = ImageTransfer.CreateChunks(hash, data);

        // 乱序加入，验证按 Index 拼装
        var assembler = new ImageTransfer.MapAssembler(hash);
        foreach (var chunk in chunks.Reverse())
        {
            assembler.Add(chunk);
        }

        Assert.True(assembler.IsComplete);
        var assembled = assembler.TryAssemble();
        Assert.NotNull(assembled);
        Assert.Equal(data, assembled!);
    }

    [Fact]
    public void Assembler_RejectsWrongHash()
    {
        var data = new byte[1000];
        new Random(3).NextBytes(data);
        var hash = HashUtil.Sha256Hex(data);
        var chunks = ImageTransfer.CreateChunks(hash, data);
        chunks[0].Data[0] ^= 0xFF; // 破坏数据

        var assembler = new ImageTransfer.MapAssembler(hash);
        foreach (var chunk in chunks) assembler.Add(chunk);

        Assert.True(assembler.IsComplete);
        Assert.Null(assembler.TryAssemble());
    }

    [Fact]
    public void Assembler_RejectsChunkFromOtherImage()
    {
        var assembler = new ImageTransfer.MapAssembler("hash-a");
        var chunk = new MapChunkMessage { ImageHash = "hash-b", Index = 0, Total = 1, Data = new byte[1] };
        Assert.Throws<InvalidOperationException>(() => assembler.Add(chunk));
    }

    [Fact]
    public void Assembler_RejectsOutOfRangeIndex()
    {
        var assembler = new ImageTransfer.MapAssembler("hash");
        var chunk = new MapChunkMessage { ImageHash = "hash", Index = 5, Total = 2, Data = new byte[1] };
        Assert.Throws<InvalidOperationException>(() => assembler.Add(chunk));
    }

    [Fact]
    public void Assembler_IsIncompleteUntilAllChunksArrive()
    {
        var data = new byte[ImageTransfer.ChunkSize * 2];
        var hash = HashUtil.Sha256Hex(data);
        var chunks = ImageTransfer.CreateChunks(hash, data);

        var assembler = new ImageTransfer.MapAssembler(hash);
        assembler.Add(chunks[0]);
        Assert.False(assembler.IsComplete);
        Assert.Null(assembler.TryAssemble());
        assembler.Add(chunks[1]);
        Assert.True(assembler.IsComplete);
        Assert.NotNull(assembler.TryAssemble());
    }

    [Fact]
    public void Sha256Hex_IsLowercaseHexOf64Chars()
    {
        var hash = HashUtil.Sha256Hex("hello");
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash.ToLowerInvariant(), hash);
        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", hash);
    }

    [Fact]
    public void VerifySha256_DetectsMismatch()
    {
        var data = new byte[] { 1, 2, 3 };
        var hash = HashUtil.Sha256Hex(data);
        Assert.True(HashUtil.VerifySha256(data, hash));
        Assert.True(HashUtil.VerifySha256(data, hash.ToUpperInvariant()));
        Assert.False(HashUtil.VerifySha256(new byte[] { 1, 2, 4 }, hash));
    }

    [Fact]
    public void RoomPassword_HashesDeterministically()
    {
        Assert.Equal(RoomPassword.Hash("abc"), RoomPassword.Hash("abc"));
        Assert.NotEqual(RoomPassword.Hash("abc"), RoomPassword.Hash("abd"));
        Assert.True(RoomPassword.Matches(RoomPassword.Hash("abc"), RoomPassword.Hash("abc").ToUpperInvariant()));
        Assert.False(RoomPassword.Matches(RoomPassword.Hash("abc"), RoomPassword.Hash("x")));
    }

    [Theory]
    [InlineData("#FF0000", true)]
    [InlineData("FF0000", true)]
    [InlineData("#80FF0000", true)]
    [InlineData("#F00", false)]
    [InlineData("red", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ColorHex_ValidatesFormat(string? color, bool expected)
        => Assert.Equal(expected, ColorHex.IsValid(color));

    [Fact]
    public void ColorHex_NormalizesToArgb()
    {
        Assert.Equal("#FFFF0000", ColorHex.Normalize("#FF0000"));
        Assert.Equal("#80FF0000", ColorHex.Normalize("#80ff0000"));
        Assert.Equal(0xFFFF0000u, ColorHex.ToArgb("#FF0000"));
        Assert.Equal(0x80FF0000u, ColorHex.ToArgb("#80FF0000"));
        Assert.Equal(0xFFFFE066u, ColorHex.ToArgb("bogus")); // 非法输入回退默认色 #FFE066
    }

    [Fact]
    public void ColorHex_SplitsComponents()
    {
        var (a, r, g, b) = ColorHex.ToComponents("#80336699");
        Assert.Equal(0x80, a);
        Assert.Equal(0x33, r);
        Assert.Equal(0x66, g);
        Assert.Equal(0x99, b);
    }
}
