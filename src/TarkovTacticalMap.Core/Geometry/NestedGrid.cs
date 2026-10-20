using TarkovTacticalMap.Core.Models;

namespace TarkovTacticalMap.Core.Geometry;

/// <summary>某一级网格的分块方式：该级每个父格被切成 Cols × Rows 个小格。</summary>
public readonly record struct GridLevelSpec(int Cols, int Rows);

/// <summary>
/// 嵌套网格：一级网格铺满整张图（格子接近正方形、密度尽可能高），
/// 之后每一级把父格再切成 <see cref="SubDivisions"/> × <see cref="SubDivisions"/> 小格。
///
/// 显示规则（自动按缩放递归）：屏幕上的格子尺寸大于 <see cref="RevealCellPixels"/> 时才显示下一级，
/// 最多 <see cref="MaxLevels"/> 级；网格引用用冒号串起来，例如 <c>D5:A3:E1:A1</c>。
/// 网格只做视觉参考与定位，不参与标记存储。
/// </summary>
public static class NestedGrid
{
    /// <summary>最多显示几级。</summary>
    public const int MaxLevels = 4;

    /// <summary>
    /// 二级及以后每级细分的格数：每边最多 10 格（列 A~J、行 0~9），
    /// 与一级网格同一套标签规则，所以引用形如 <c>D5:A3:E1:A0</c>。
    /// </summary>
    public const int SubDivisions = MapInfo.MaxGrid;

    /// <summary>该级格子屏幕尺寸不小于该值（像素）时才显示这一级。</summary>
    public const float RevealCellPixels = 20f;

    /// <summary>一级网格的列数/行数（已按图片宽高比计算，格子接近正方形）。</summary>
    public static GridLevelSpec GetBaseSpec(MapInfo map)
        => new(Math.Clamp(map.GridCols, MapInfo.MinGrid, MapInfo.MaxGrid),
               Math.Clamp(map.GridRows, MapInfo.MinGrid, MapInfo.MaxGrid));

    /// <summary>构造完整的层级定义（第 1 级用地图网格，之后每级 8×8）。</summary>
    public static IReadOnlyList<GridLevelSpec> BuildSpecs(MapInfo map, int maxLevels = MaxLevels)
    {
        maxLevels = Math.Clamp(maxLevels, 1, MaxLevels);
        var specs = new List<GridLevelSpec>(maxLevels) { GetBaseSpec(map) };
        for (var i = 1; i < maxLevels; i++) specs.Add(new GridLevelSpec(SubDivisions, SubDivisions));
        return specs;
    }

    /// <summary>某一级网格中「一个小格」的归一化尺寸。</summary>
    public static (float CellWidth, float CellHeight) GetCellSize(IReadOnlyList<GridLevelSpec> specs, int level)
    {
        var w = 1f;
        var h = 1f;
        for (var i = 0; i < level && i < specs.Count; i++)
        {
            w /= specs[i].Cols;
            h /= specs[i].Rows;
        }
        return (w, h);
    }

    /// <summary>当前缩放下可见的最深层级（1 表示只看一级网格）。</summary>
    public static int GetVisibleDepth(IReadOnlyList<GridLevelSpec> specs, ViewportState viewport)
    {
        if (specs.Count == 0 || !viewport.IsValid) return 1;

        // 一级总是显示；第 k 级（k≥2）只有它自己的格子足够大时才出现
        var depth = 1;
        for (var level = 2; level <= specs.Count; level++)
        {
            var (cellW, cellH) = GetCellSize(specs, level);
            var screenW = cellW * viewport.DisplayWidth;
            var screenH = cellH * viewport.DisplayHeight;
            if (MathF.Min(screenW, screenH) < RevealCellPixels) break;
            depth = level;
        }
        return depth;
    }

    /// <summary>某一级某个格子的归一化矩形。</summary>
    public static MapRect GetCellRect(IReadOnlyList<GridLevelSpec> specs, int level, int col, int row)
    {
        if (level < 1 || level > specs.Count) return default;

        var left = 0f;
        var top = 0f;
        var width = 1f;
        var height = 1f;

        for (var i = 0; i < level; i++)
        {
            var spec = specs[i];
            width /= spec.Cols;
            height /= spec.Rows;
            if (i == level - 1)
            {
                left += col * width;
                top += row * height;
            }
        }
        return new MapRect(left, top, width, height);
    }

    /// <summary>某一级网格中包含给定归一化坐标的那个格子。</summary>
    public static MapRect GetCellRectAt(IReadOnlyList<GridLevelSpec> specs, int level, float nx, float ny)
    {
        if (specs.Count == 0) return default;
        level = Math.Clamp(level, 1, specs.Count);

        // 先按层级逐级裁出父格，最后一级再取具体小格
        var left = 0f;
        var top = 0f;
        var width = 1f;
        var height = 1f;
        for (var i = 0; i < level; i++)
        {
            var spec = specs[i];
            width /= spec.Cols;
            height /= spec.Rows;
            var col = Math.Clamp((int)MathF.Floor((nx - left) / width), 0, spec.Cols - 1);
            var row = Math.Clamp((int)MathF.Floor((ny - top) / height), 0, spec.Rows - 1);
            left += col * width;
            top += row * height;
        }
        return new MapRect(left, top, width, height);
    }

    /// <summary>单元格标签：列 A/B/C…（最多 A~J），行 0/1/2…（最多 0~9），如 "C3"。</summary>
    public static string GetCellLabel(int col, int row) => GridReference.ColumnLabel(col) + GridReference.RowLabel(row);

    /// <summary>
    /// 计算归一化坐标在「当前可见最深层级」下的网格引用，例如 <c>D5:A3:E1:A1</c>。
    /// 坐标在图外时返回 null。
    /// </summary>
    public static string? GetReference(float nx, float ny, IReadOnlyList<GridLevelSpec> specs, ViewportState viewport)
    {
        if (nx < 0f || nx > 1f || ny < 0f || ny > 1f) return null;
        return GetReference(nx, ny, specs, GetVisibleDepth(specs, viewport));
    }

    /// <summary>按指定层级计算网格引用。</summary>
    public static string? GetReference(float nx, float ny, IReadOnlyList<GridLevelSpec> specs, int level)
    {
        if (nx < 0f || nx > 1f || ny < 0f || ny > 1f) return null;
        if (specs.Count == 0) return null;
        level = Math.Clamp(level, 1, specs.Count);

        var parts = new List<string>(level);
        var left = 0f;
        var top = 0f;
        var width = 1f;
        var height = 1f;

        for (var i = 0; i < level; i++)
        {
            var spec = specs[i];
            width /= spec.Cols;
            height /= spec.Rows;

            var col = Math.Clamp((int)MathF.Floor((nx - left) / width), 0, spec.Cols - 1);
            var row = Math.Clamp((int)MathF.Floor((ny - top) / height), 0, spec.Rows - 1);
            parts.Add(GetCellLabel(col, row));
            left += col * width;
            top += row * height;
        }

        return string.Join(':', parts);
    }

    /// <summary>网格引用拆成各级标签（"D5:A3" → ["D5","A3"]）。</summary>
    public static IReadOnlyList<string> SplitReference(string? reference)
        => string.IsNullOrEmpty(reference)
            ? Array.Empty<string>()
            : reference.Split(':', StringSplitOptions.RemoveEmptyEntries);
}
