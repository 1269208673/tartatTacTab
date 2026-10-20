using TarkovTacticalMap.Core.Geometry;
using TarkovTacticalMap.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace TarkovTacticalMap.Tests;

/// <summary>
/// 网格布局（只有一级）：每边最多 10 格（列 A~J、行 0~9），
/// 非正方形图片按宽高比减少较短那边，使格子接近正方形。
/// </summary>
public class GridLayoutTests
{
    private readonly ITestOutputHelper _output;

    public GridLayoutTests(ITestOutputHelper output) => _output = output;

    private static MapInfo NewMap(int width, int height, int density = MapInfo.DefaultGridDensity)
    {
        var map = MapInfo.CreateBlank("hash", width, height);
        map.ApplyGridDensity(density);
        return map;
    }

    [Theory]
    [InlineData(1600, 1200)]
    [InlineData(3000, 1000)]
    [InlineData(1000, 3000)]
    [InlineData(1920, 1080)]
    [InlineData(1000, 1000)]
    [InlineData(3440, 1440)]
    public void BaseGrid_KeepsCellsSquareAndWithinLimits(int width, int height)
    {
        var (cols, rows) = MapInfo.ComputeAspectGrid(width, height, MapInfo.DefaultGridDensity);

        // 每级上限：列 A~J（10）、行 0~9（10）
        Assert.InRange(cols, 1, MapInfo.MaxGrid);
        Assert.InRange(rows, 1, MapInfo.MaxGrid);

        // 格子接近正方形（误差 ≤ 6%）
        var cellW = width / (double)cols;
        var cellH = height / (double)rows;
        Assert.InRange(cellW / cellH, 0.94, 1.06);

        // 密度尽量高
        Assert.True(cols * rows >= 12, $"{width}×{height} 的网格过稀：{cols}×{rows}");
    }

    [Fact]
    public void BaseGrid_PrintsCommonAspectRatios()
    {
        var cases = new (int W, int H, string Name)[]
        {
            (1000, 1000, "1:1"),
            (1600, 1200, "4:3"),
            (1920, 1080, "16:9"),
            (3440, 1440, "21:9"),
            (3000, 1000, "3:1"),
            (1000, 3000, "1:3"),
        };

        foreach (var (w, h, name) in cases)
        {
            var (cols, rows) = MapInfo.ComputeAspectGrid(w, h, MapInfo.DefaultGridDensity);
            var cellW = w / (float)cols;
            var cellH = h / (float)rows;
            _output.WriteLine(
                $"{name,-6} {w}×{h} → {cols} 列(A~{GridReference.ColumnLabel(cols - 1)}) × {rows} 行(0~{rows - 1})，" +
                $"格子 {cellW:F0}×{cellH:F0}px（宽高比 {cellW / cellH:F3}）");

            Assert.InRange(cellW / cellH, 0.94, 1.06);
        }
    }

    [Fact]
    public void BaseGrid_SquareImageUsesFullDensity()
    {
        var (cols, rows) = MapInfo.ComputeAspectGrid(1200, 1200, MapInfo.DefaultGridDensity);
        Assert.Equal(MapInfo.MaxGrid, cols);
        Assert.Equal(MapInfo.MaxGrid, rows);
    }

    [Fact]
    public void BaseGrid_WideImageReducesRows()
    {
        var (cols, rows) = MapInfo.ComputeAspectGrid(3000, 1000, MapInfo.DefaultGridDensity);
        Assert.True(cols > rows, $"宽图应列多于行（{cols}×{rows}）");
        Assert.True(cols <= MapInfo.MaxGrid && rows <= MapInfo.MaxGrid);
    }

    [Fact]
    public void BaseGrid_TallImageReducesColumns()
    {
        var (cols, rows) = MapInfo.ComputeAspectGrid(1000, 3000, MapInfo.DefaultGridDensity);
        Assert.True(rows > cols, $"高图应行多于列（{cols}×{rows}）");
        Assert.True(cols <= MapInfo.MaxGrid && rows <= MapInfo.MaxGrid);
    }

    [Fact]
    public void CellLabels_CoverAtoJAnd0to9()
    {
        Assert.Equal("A", GridReference.ColumnLabel(0));
        Assert.Equal("J", GridReference.ColumnLabel(MapInfo.MaxGrid - 1));
        Assert.Equal("0", GridReference.RowLabel(0));
        Assert.Equal("9", GridReference.RowLabel(MapInfo.MaxGrid - 1));
    }

    [Fact]
    public void Reference_IsSingleLevelAndMatchesCellRect()
    {
        var map = NewMap(1600, 1200);
        var (cols, rows) = GridLayout.GetSize(map);

        var cell = GridLayout.GetCellRect(cols, rows, 3, 2);
        var centerX = cell.Left + cell.Width / 2f;
        var centerY = cell.Top + cell.Height / 2f;

        var reference = GridLayout.GetReference(centerX, centerY, cols, rows);
        Assert.Equal(GridReference.ColumnLabel(3) + GridReference.RowLabel(2), reference);
        Assert.DoesNotContain(':', reference!); // 已取消次级网格，引用只有一级

        var rectAt = GridLayout.GetCellRectAt(cols, rows, centerX, centerY);
        Assert.Equal(cell.Left, rectAt.Left, 5);
        Assert.Equal(cell.Top, rectAt.Top, 5);
        Assert.Equal(cell.Width, rectAt.Width, 5);
        Assert.Equal(cell.Height, rectAt.Height, 5);
    }

    [Fact]
    public void Reference_ReturnsNullOutsideImage()
    {
        var map = NewMap(1600, 1200);
        var (cols, rows) = GridLayout.GetSize(map);
        Assert.Null(GridLayout.GetReference(-0.1f, 0.5f, cols, rows));
        Assert.Null(GridLayout.GetReference(0.5f, 1.2f, cols, rows));
    }

    [Fact]
    public void GridSize_ShrinksWhenDensityIsLowered()
    {
        var map = NewMap(1600, 1200, MapInfo.MaxGrid);
        var before = (map.GridCols, map.GridRows);

        map.ApplyGridDensity(4);
        var after = (map.GridCols, map.GridRows);

        Assert.True(after.GridCols < before.GridCols || after.GridRows < before.GridRows);
        Assert.True(after.GridCols <= 4);
        Assert.True(after.GridRows <= 4);
    }

    [Fact]
    public void GridSize_IsClampedToLimits()
    {
        var map = NewMap(1000, 1000, 99);
        Assert.Equal(MapInfo.MaxGrid, Math.Max(map.GridCols, map.GridRows));
        Assert.InRange(map.GridCols, 1, MapInfo.MaxGrid);
        Assert.InRange(map.GridRows, 1, MapInfo.MaxGrid);
    }
}
