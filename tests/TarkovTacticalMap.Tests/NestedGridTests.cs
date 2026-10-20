using TarkovTacticalMap.Core.Geometry;
using TarkovTacticalMap.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace TarkovTacticalMap.Tests;

/// <summary>嵌套网格：一级密度保持宽高比、按缩放自动递归、引用形如 D5:A3:E1:A0。</summary>
public class NestedGridTests
{
    private readonly ITestOutputHelper _output;

    public NestedGridTests(ITestOutputHelper output) => _output = output;
    private static MapInfo NewMap(int width, int height, int density = MapInfo.DefaultGridDensity)
    {
        var map = MapInfo.CreateBlank("hash", width, height);
        map.ApplyGridDensity(density);
        return map;
    }

    private static ViewportState NewViewport(int imageWidth, int imageHeight, float zoom = 1f,
        float canvasWidth = 900f, float canvasHeight = 700f)
    {
        var vp = new ViewportState();
        vp.SetCanvas(canvasWidth, canvasHeight);
        vp.SetImage(imageWidth, imageHeight);
        if (MathF.Abs(zoom - 1f) > 1e-4f) vp.ZoomAt(canvasWidth / 2f, canvasHeight / 2f, zoom);
        return vp;
    }

    [Theory]
    [InlineData(1600, 1200)]
    [InlineData(3000, 1000)]
    [InlineData(1000, 3000)]
    [InlineData(1920, 1080)]
    [InlineData(1000, 1000)]
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

        // 密度尽量高：格子总数不应太少
        Assert.True(cols * rows >= 12, $"{width}×{height} 的网格过稀：{cols}×{rows}");
    }

    [Fact]
    public void BaseGrid_PrintsCommonAspectRatios()
    {
        // 便于人工核对：不同图片比例下自动推导出的一级网格
        var cases = new (int W, int H, string Name)[]
        {
            (1000, 1000, "1:1"),
            (1600, 1200, "4:3"),
            (1920, 1080, "16:9"),
            (3440, 1440, "21:9"),
            (3000, 1000, "3:1"),
            (1000, 3000, "1:3"),
            (2048, 2048, "2K 方图"),
        };

        foreach (var (w, h, name) in cases)
        {
            var (cols, rows) = MapInfo.ComputeAspectGrid(w, h, MapInfo.DefaultGridDensity);
            var cellW = w / (float)cols;
            var cellH = h / (float)rows;
            _output.WriteLine(
                $"{name,-10} {w}×{h} → {cols} 列(A~{GridReference.ColumnLabel(cols - 1)}) × {rows} 行(0~{rows - 1})，" +
                $"格子 {cellW:F0}×{cellH:F0}px（宽高比 {cellW / cellH:F3}）");

            Assert.InRange(cellW / cellH, 0.94, 1.06);
            Assert.True(cols <= MapInfo.MaxGrid && rows <= MapInfo.MaxGrid);
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

        // 格子仍然接近正方形
        var cellW = 3000 / (double)cols;
        var cellH = 1000 / (double)rows;
        Assert.InRange(cellW / cellH, 0.94, 1.06);
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
    public void VisibleDepth_GrowsWithZoom()
    {
        var map = NewMap(1600, 1200);
        var specs = NestedGrid.BuildSpecs(map);

        // 1x：只显示一级网格（细分成小格没有意义）
        Assert.Equal(1, NestedGrid.GetVisibleDepth(specs, NewViewport(1600, 1200, 1f)));

        // 中等缩放：至少多一级
        Assert.True(NestedGrid.GetVisibleDepth(specs, NewViewport(1600, 1200, 10f)) >= 2);

        // 最大缩放：层级只会更多，但不超过上限
        var deepest = NestedGrid.GetVisibleDepth(specs, NewViewport(1600, 1200, ViewportState.MaxZoom));
        Assert.InRange(deepest, 2, NestedGrid.MaxLevels);
    }

    [Fact]
    public void Reference_IsSingleLevelWhenZoomedOut()
    {
        var map = NewMap(1600, 1200);
        var specs = NestedGrid.BuildSpecs(map);
        var vp = NewViewport(1600, 1200, 1f);

        var reference = NestedGrid.GetReference(0.31f, 0.42f, specs, vp);
        Assert.NotNull(reference);
        Assert.DoesNotContain(':', reference!);
        Assert.Equal(NestedGrid.GetCellLabel(
            Math.Clamp((int)(0.31f * map.GridCols), 0, map.GridCols - 1),
            Math.Clamp((int)(0.42f * map.GridRows), 0, map.GridRows - 1)), reference);
    }

    [Fact]
    public void Reference_BecomesNestedPathWhenZoomedIn()
    {
        var map = NewMap(1600, 1200);
        var specs = NestedGrid.BuildSpecs(map);
        var vp = NewViewport(1600, 1200, ViewportState.MaxZoom);

        var reference = NestedGrid.GetReference(0.4242f, 0.3711f, specs, vp);
        Assert.NotNull(reference);

        var parts = NestedGrid.SplitReference(reference);
        var depth = NestedGrid.GetVisibleDepth(specs, vp);
        Assert.Equal(depth, parts.Count);
        Assert.True(parts.Count >= 2, $"放大后应为多级引用（{reference}）");
        foreach (var part in parts)
        {
            Assert.Matches("^[A-Z]+[0-9]+$", part);

            // 每级上限：列 A~J、行 0~9
            var colPart = part.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            Assert.True(colPart.Length == 1 && colPart[0] is >= 'A' and <= 'J', $"列超出 A~J：{part}");
            var rowPart = part[colPart.Length..];
            Assert.True(int.Parse(rowPart) <= 9, $"行超出 0~9：{part}");
        }
    }

    [Fact]
    public void Reference_ReturnsNullOutsideImage()
    {
        var map = NewMap(1600, 1200);
        var specs = NestedGrid.BuildSpecs(map);
        var vp = NewViewport(1600, 1200);
        Assert.Null(NestedGrid.GetReference(-0.1f, 0.5f, specs, vp));
        Assert.Null(NestedGrid.GetReference(0.5f, 1.2f, specs, vp));
    }

    [Fact]
    public void Reference_IsConsistentWithCellRect()
    {
        var map = NewMap(1600, 1200);
        var specs = NestedGrid.BuildSpecs(map);

        // 取某一级格子的中心点，其引用应恰好指向该格
        var cell = NestedGrid.GetCellRect(specs, 1, 3, 2);
        var centerX = cell.Left + cell.Width / 2f;
        var centerY = cell.Top + cell.Height / 2f;

        var reference = NestedGrid.GetReference(centerX, centerY, specs, 1);
        Assert.Equal(NestedGrid.GetCellLabel(3, 2), reference);

        var rectAt = NestedGrid.GetCellRectAt(specs, 1, centerX, centerY);
        Assert.Equal(cell.Left, rectAt.Left, 4);
        Assert.Equal(cell.Top, rectAt.Top, 4);
        Assert.Equal(cell.Width, rectAt.Width, 4);
        Assert.Equal(cell.Height, rectAt.Height, 4);
    }

    [Fact]
    public void CellSizes_ShrinkBySubdivisionsEachLevel()
    {
        var map = NewMap(1600, 1200);
        var specs = NestedGrid.BuildSpecs(map);

        var (w1, h1) = NestedGrid.GetCellSize(specs, 1);
        var (w2, h2) = NestedGrid.GetCellSize(specs, 2);
        var (w3, h3) = NestedGrid.GetCellSize(specs, 3);

        Assert.Equal(1f / map.GridCols, w1, 5);
        Assert.Equal(w1 / NestedGrid.SubDivisions, w2, 6);
        Assert.Equal(w2 / NestedGrid.SubDivisions, w3, 7);
        Assert.Equal(h1 / NestedGrid.SubDivisions, h2, 6);
    }

    [Fact]
    public void BuildSpecs_FirstLevelUsesMapGrid()
    {
        var map = NewMap(1600, 1200, 12);
        var specs = NestedGrid.BuildSpecs(map);

        Assert.Equal(map.GridCols, specs[0].Cols);
        Assert.Equal(map.GridRows, specs[0].Rows);
        for (var i = 1; i < specs.Count; i++)
        {
            Assert.Equal(NestedGrid.SubDivisions, specs[i].Cols);
            Assert.Equal(NestedGrid.SubDivisions, specs[i].Rows);
        }
    }

    [Fact]
    public void ApplyGridDensity_UpdatesColsAndRows()
    {
        var map = NewMap(1600, 1200, 26);
        var (cols26, rows26) = (map.GridCols, map.GridRows);

        map.ApplyGridDensity(8);
        Assert.Equal(8, Math.Max(map.GridCols, map.GridRows));
        Assert.True(map.GridCols < cols26);
        Assert.True(map.GridRows < rows26);
    }
}
