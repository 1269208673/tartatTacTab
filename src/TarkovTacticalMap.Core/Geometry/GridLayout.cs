using TarkovTacticalMap.Core.Models;

namespace TarkovTacticalMap.Core.Geometry;

/// <summary>
/// 网格布局（只有一级）：每边最多 10 格。
/// 列标签 A~J、行标签 0~9；非正方形图片会按宽高比减少较短那边，使格子接近正方形。
/// 网格只是视觉参考与定位辅助，不参与标记存储。
/// </summary>
public static class GridLayout
{
    /// <summary>网格列数/行数（已按图片宽高比计算，格子接近正方形）。</summary>
    public static (int Cols, int Rows) GetSize(MapInfo map)
        => (Math.Clamp(map.GridCols, MapInfo.MinGrid, MapInfo.MaxGrid),
            Math.Clamp(map.GridRows, MapInfo.MinGrid, MapInfo.MaxGrid));

    /// <summary>某个格子的归一化矩形。</summary>
    public static MapRect GetCellRect(int cols, int rows, int col, int row)
    {
        cols = Math.Max(cols, 1);
        rows = Math.Max(rows, 1);
        var width = 1f / cols;
        var height = 1f / rows;
        return new MapRect(
            Math.Clamp(col, 0, cols - 1) * width,
            Math.Clamp(row, 0, rows - 1) * height,
            width,
            height);
    }

    /// <summary>包含给定归一化坐标的格子矩形。</summary>
    public static MapRect GetCellRectAt(int cols, int rows, float nx, float ny)
    {
        var (col, row) = GridReference.GetCell(nx, ny, cols, rows);
        return GetCellRect(cols, rows, col, row);
    }

    /// <summary>归一化坐标所在的网格引用，如 "C3"（行从 0 开始）。图外返回 null。</summary>
    public static string? GetReference(float nx, float ny, int cols, int rows)
        => GridReference.GetLabel(nx, ny, cols, rows);
}
