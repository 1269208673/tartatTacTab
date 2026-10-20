using TarkovTacticalMap.Core.Protocol;
using TarkovTacticalMap.Core.Utils;

namespace TarkovTacticalMap.Network;

/// <summary>把图片切成 64KB 分块，并在接收端拼装 + 校验 SHA-256。</summary>
public static class ImageTransfer
{
    /// <summary>分块大小（与协议常量一致）。</summary>
    public const int ChunkSize = ProtocolConstants.MapChunkSize;

    /// <summary>按字节数计算分块总数。</summary>
    public static int GetChunkCount(int totalBytes)
    {
        if (totalBytes <= 0) return 0;
        return (totalBytes + ChunkSize - 1) / ChunkSize;
    }

    /// <summary>把图片字节切成 MAP_CHUNK 消息序列。</summary>
    public static IReadOnlyList<MapChunkMessage> CreateChunks(string imageHash, byte[] imageBytes)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        if (imageBytes.Length == 0) return Array.Empty<MapChunkMessage>();

        var total = GetChunkCount(imageBytes.Length);
        var chunks = new List<MapChunkMessage>(total);
        for (var i = 0; i < total; i++)
        {
            var offset = i * ChunkSize;
            var length = Math.Min(ChunkSize, imageBytes.Length - offset);
            var data = new byte[length];
            Buffer.BlockCopy(imageBytes, offset, data, 0, length);
            chunks.Add(new MapChunkMessage
            {
                ImageHash = imageHash,
                Index = i,
                Total = total,
                Data = data,
            });
        }
        return chunks;
    }

    /// <summary>接收端拼装器：按 Index 收齐后校验 SHA-256。允许先创建、收到首块后确定总块数。</summary>
    public sealed class MapAssembler
    {
        private byte[]?[] _chunks;
        private readonly object _gate = new();
        private int _received;

        public MapAssembler(string imageHash, int totalChunks = 0)
        {
            if (totalChunks < 0) throw new ArgumentOutOfRangeException(nameof(totalChunks));
            ImageHash = imageHash ?? string.Empty;
            TotalChunks = totalChunks;
            _chunks = totalChunks == 0 ? Array.Empty<byte[]?>() : new byte[]?[totalChunks];
        }

        public string ImageHash { get; }

        public int TotalChunks { get; private set; }

        public int ReceivedChunks
        {
            get { lock (_gate) return _received; }
        }

        public bool IsComplete
        {
            get { lock (_gate) return TotalChunks > 0 && _received == TotalChunks; }
        }

        /// <summary>加入一块；返回是否已收齐。</summary>
        public bool Add(MapChunkMessage chunk)
        {
            ArgumentNullException.ThrowIfNull(chunk);
            if (!string.Equals(chunk.ImageHash, ImageHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("分块的 ImageHash 与拼装器不一致");
            }

            lock (_gate)
            {
                if (TotalChunks == 0 && chunk.Total > 0)
                {
                    // 首块到达后确定总块数（客户端事先不知道总块数）
                    TotalChunks = chunk.Total;
                    _chunks = new byte[]?[TotalChunks];
                }
                else if (chunk.Total != TotalChunks)
                {
                    throw new InvalidOperationException("分块总数与拼装器不一致");
                }

                if (chunk.Index < 0 || chunk.Index >= TotalChunks)
                {
                    throw new InvalidOperationException($"分块序号越界：{chunk.Index}/{TotalChunks}");
                }

                if (_chunks[chunk.Index] is null)
                {
                    _chunks[chunk.Index] = chunk.Data ?? Array.Empty<byte>();
                    _received++;
                }
                return _received == TotalChunks && TotalChunks > 0;
            }
        }

        /// <summary>拼装并校验；校验失败返回 null。</summary>
        public byte[]? TryAssemble()
        {
            lock (_gate)
            {
                if (TotalChunks <= 0 || _received != TotalChunks) return null;

                var totalLength = 0;
                foreach (var c in _chunks) totalLength += c?.Length ?? 0;

                var result = new byte[totalLength];
                var offset = 0;
                foreach (var c in _chunks)
                {
                    if (c is null) return null;
                    Buffer.BlockCopy(c, 0, result, offset, c.Length);
                    offset += c.Length;
                }

                return HashUtil.VerifySha256(result, ImageHash) ? result : null;
            }
        }
    }
}
