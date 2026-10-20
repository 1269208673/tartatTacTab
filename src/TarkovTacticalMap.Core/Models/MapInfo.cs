using MessagePack;

namespace TarkovTacticalMap.Core.Models;

/// <summary>
/// 地图元数据。MapId / ImageHash 由房主创建房间时生成。
/// 网格是视觉参考：一级网格保持「格子接近正方形」，行列数相差按图片宽高比自动计算。
/// </summary>
[MessagePackObject]
public sealed class MapInfo
{
    public const int DefaultGridCols = 8;
    public const int DefaultGridRows = 6;

    /// <summary>
    /// 每一级网格每边的最大格数：列标签 A~J（10 列）、行标签 0~9（10 行）。
    /// 非正方形图片会按宽高比减少行或列，保证格子接近正方形。
    /// </summary>
    public const int MaxGrid = 10;

    public const int MinGrid = 1;

    /// <summary>默认网格密度：每边最多格数（尽可能细，同时保持正方形格子）。</summary>
    public const int DefaultGridDensity = MaxGrid;

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

    /// <summary>网格列数。</summary>
    [Key(4)]
    public int GridCols { get; set; } = DefaultGridCols;

    /// <summary>网格行数。</summary>
    [Key(5)]
    public int GridRows { get; set; } = DefaultGridRows;

    /// <summary>网格是否显示。</summary>
    [Key(6)]
    public bool GridVisible { get; set; } = true;

    /// <summary>网格密度 = 行列数中的较大者（用户只调密度，行列数按比例推导）。</summary>
    [IgnoreMember]
    public int GridDensity => Math.Max(GridCols, GridRows);

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

    /// <summary>
    /// 按图片宽高比计算一级网格行列数：
    /// 每边最多 <see cref="MaxGrid"/> 格（列 A~J、行 0~9）；非正方形图片会减少较短那边，
    /// 使格子尽量接近正方形（误差 ≤ 6%），格子总数尽量多。
    /// </summary>
    public static (int Cols, int Rows) ComputeAspectGrid(int imageWidth, int imageHeight, int density)
    {
        density = Math.Clamp(density, 2, MaxGrid);
        if (imageWidth <= 0 || imageHeight <= 0) return (density, density);

        var bestCols = density;
        var bestRows = Math.Clamp((int)MathF.Round(density * imageHeight / (float)imageWidth), 1, density);
        var bestCells = -1;
        var bestError = float.MaxValue;

        for (var cols = density; cols >= 1; cols--)
        {
            var rows = Math.Clamp((int)MathF.Round(cols * imageHeight / (float)imageWidth), 1, density);
            if (rows > density) continue;

            var cellWidth = imageWidth / (float)cols;
            var cellHeight = imageHeight / (float)rows;
            var error = MathF.Abs(cellWidth - cellHeight) / MathF.Max(cellWidth, cellHeight);
            if (error > 0.06f) continue; // 格子明显不是正方形，不采用

            var cells = cols * rows;
            if (cells > bestCells || (cells == bestCells && error < bestError))
            {
                bestCols = cols;
                bestRows = rows;
                bestCells = cells;
                bestError = error;
            }
        }

        return (Math.Clamp(bestCols, MinGrid, MaxGrid), Math.Clamp(bestRows, MinGrid, MaxGrid));
    }

    /// <summary>按密度重设一级网格（保持宽高比）。</summary>
    public void ApplyGridDensity(int density)
    {
        var (cols, rows) = ComputeAspectGrid(ImageWidth, ImageHeight, density);
        GridCols = cols;
        GridRows = rows;
    }

    /// <summary>创建一个空白地图（默认空白网格，1:1 宽高）。</summary>
    public static MapInfo CreateBlank(string imageHash, int width, int height) => new()
    {
        MapId = Guid.NewGuid().ToString("D"),
        ImageHash = imageHash,
        ImageWidth = width,
        ImageHeight = height,
        GridCols = ComputeAspectGrid(width, height, DefaultGridDensity).Cols,
        GridRows = ComputeAspectGrid(width, height, DefaultGridDensity).Rows,
        GridVisible = true,
    };
}
