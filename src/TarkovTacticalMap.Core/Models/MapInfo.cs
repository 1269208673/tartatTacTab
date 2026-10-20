using MessagePack;

namespace TarkovTacticalMap.Core.Models;

/// <summary>
/// 地图元数据。MapId / ImageHash 由房主创建房间时生成。
/// </summary>
[MessagePackObject]
public sealed class MapInfo
{
    public const int DefaultGridCols = 8;
    public const int DefaultGridRows = 6;
    public const int MinGrid = 1;
    public const int MaxGrid = 26;

    /// <summary>地图 GUID，房主创建时生成。</summary>
    [Key(0)]
    public string MapId { get; set; } = string.Empty;

    /// <summary>原图 SHA-256，小写 hex。</summary>
    [Key(1)]
    public string ImageHash { get; set; } = string.Empty;

    /// <summary>原图像素宽。</summary>
    [Key(2)]
    public int ImageWidth { get; set; }

    /// <summary>原图像素高。</summary>
    [Key(3)]
    public int ImageHeight { get; set; }

    /// <summary>网格列数，默认 8。</summary>
    [Key(4)]
    public int GridCols { get; set; } = DefaultGridCols;

    /// <summary>网格行数，默认 6。</summary>
    [Key(5)]
    public int GridRows { get; set; } = DefaultGridRows;

    /// <summary>网格是否显示，默认 true。</summary>
    [Key(6)]
    public bool GridVisible { get; set; } = true;

    public MapInfo Clone() => new()
    {
        MapId = MapId,
        ImageHash = ImageHash,
        ImageWidth = ImageWidth,
        ImageHeight = ImageHeight,
        GridCols = GridCols,
        GridRows = GridRows,
        GridVisible = GridVisible,
    };

    /// <summary>把网格行列数约束到协议允许的范围。</summary>
    public void ClampGrid()
    {
        GridCols = Math.Clamp(GridCols, MinGrid, MaxGrid);
        GridRows = Math.Clamp(GridRows, MinGrid, MaxGrid);
    }

    /// <summary>创建一个空白地图（默认空白网格，1:1 宽高）。</summary>
    public static MapInfo CreateBlank(string imageHash, int width, int height) => new()
    {
        MapId = Guid.NewGuid().ToString("D"),
        ImageHash = imageHash,
        ImageWidth = width,
        ImageHeight = height,
        GridCols = DefaultGridCols,
        GridRows = DefaultGridRows,
        GridVisible = true,
    };
}
