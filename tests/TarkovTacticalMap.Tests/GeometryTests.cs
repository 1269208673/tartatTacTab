using TarkovTacticalMap.Core.Geometry;
using TarkovTacticalMap.Core.Models;
using Xunit;

namespace TarkovTacticalMap.Tests;

/// <summary>坐标换算、网格引用与控制点几何测试（对应文档 4.1 / 4.2 / 6.4 / 9）。</summary>
public class GeometryTests
{
    private static ViewportState NewViewport(float canvasWidth = 1000f, float canvasHeight = 600f,
        int imageWidth = 2000, int imageHeight = 1000)
    {
        var vp = new ViewportState();
        vp.SetCanvas(canvasWidth, canvasHeight);
        vp.SetImage(imageWidth, imageHeight);
        return vp;
    }

    [Fact]
    public void ImageRect_IsCenteredAndFitInside()
    {
        var vp = NewViewport(); // 2000x1000 图，1000x600 画布 → fit = min(0.5, 0.6) = 0.5
        var rect = vp.ImageRect;

        Assert.Equal(0.5f, vp.FitScale, 4);
        Assert.Equal(1000f, rect.Width, 3);
        Assert.Equal(500f, rect.Height, 3);
        Assert.Equal(0f, rect.Left, 3);       // 水平居中后正好贴边
        Assert.Equal(50f, rect.Top, 3);       // (600 - 500) / 2
    }

    [Fact]
    public void ScreenToNormalized_RoundTrips()
    {
        var vp = NewViewport();
        vp.ZoomAt(500f, 300f, 2.3f);
        vp.PanBy(-120f, 42f);

        foreach (var (nx, ny) in new[] { (0f, 0f), (0.5f, 0.5f), (1f, 1f), (0.31f, 0.78f) })
        {
            var screen = vp.NormalizedToScreen(nx, ny);
            var back = vp.ScreenToNormalized(screen.X, screen.Y);
            Assert.Equal(nx, back.X, 4);
            Assert.Equal(ny, back.Y, 4);
        }
    }

    [Fact]
    public void ZoomAt_KeepsAnchorPointUnderCursor()
    {
        var vp = NewViewport();
        const float cursorX = 380f, cursorY = 220f;
        var before = vp.ScreenToNormalized(cursorX, cursorY);

        vp.ZoomAt(cursorX, cursorY, 3.7f);

        var afterScreen = vp.NormalizedToScreen(before.X, before.Y);
        Assert.Equal(cursorX, afterScreen.X, 1);
        Assert.Equal(cursorY, afterScreen.Y, 1);
        Assert.Equal(3.7f, vp.Zoom, 4);
    }

    [Theory]
    [InlineData(0.0001f, ViewportState.MinZoom)]
    [InlineData(100f, ViewportState.MaxZoom)]
    public void Zoom_IsClampedToRange(float requested, float expected)
    {
        var vp = NewViewport();
        vp.ZoomAt(500f, 300f, requested);
        Assert.Equal(expected, vp.Zoom, 4);
    }
    [Fact]
    public void MarkerScreenPosition_StaysCorrectAfterZoomAndResize()
    {
        var vp = NewViewport();
        vp.ZoomAt(300f, 200f, 2.5f);

        var marker = new Marker { X = 0.25f, Y = 0.75f, Size = 0.02f };

        var screenBefore = vp.NormalizedToScreen(marker.X, marker.Y);
        var backBefore = vp.ScreenToNormalized(screenBefore.X, screenBefore.Y);
        Assert.Equal(0.25f, backBefore.X, 4);
        Assert.Equal(0.75f, backBefore.Y, 4);

        // 窗口尺寸变化后：归一化坐标不变，屏幕位置按新尺寸重算且可逆
        vp.SetCanvas(1400f, 900f);
        var screenAfter = vp.NormalizedToScreen(marker.X, marker.Y);
        var backAfter = vp.ScreenToNormalized(screenAfter.X, screenAfter.Y);
        Assert.Equal(0.25f, backAfter.X, 4);
        Assert.Equal(0.75f, backAfter.Y, 4);
    }

    [Fact]
    public void Size_ScalesWithImageDisplayWidth()
    {
        var vp = NewViewport();
        vp.ZoomAt(500f, 300f, 2f);
        Assert.Equal(vp.ImageRect.Width * 0.05f, vp.DisplayWidth * 0.05f, 3);
    }

    [Fact]
    public void GridReference_ColumnLabels()
    {
        Assert.Equal("A", GridReference.ColumnLabel(0));
        Assert.Equal("B", GridReference.ColumnLabel(1));
        Assert.Equal("H", GridReference.ColumnLabel(7));
        Assert.Equal("Z", GridReference.ColumnLabel(25));
        Assert.Equal("AA", GridReference.ColumnLabel(26));
        Assert.Equal("AB", GridReference.ColumnLabel(27));
    }

    [Fact]
    public void GridReference_ComputesCellLabel()
    {
        // 8 列 6 行、行标签从 0 开始：x=0.31 → floor(2.48)=2 → C；y=0.42 → floor(2.52)=2 → 2
        Assert.Equal("C2", GridReference.GetLabel(0.31f, 0.42f, 8, 6));

        // 边界值
        Assert.Equal("A0", GridReference.GetLabel(0f, 0f, 8, 6));
        Assert.Equal("H5", GridReference.GetLabel(1f, 1f, 8, 6));
    }

    [Fact]
    public void GridReference_RowLabelsStartAtZero()
    {
        Assert.Equal("0", GridReference.RowLabel(0));
        Assert.Equal("9", GridReference.RowLabel(9));
        Assert.Equal("A0", GridReference.GetLabel(0.01f, 0.01f, 10, 10));
        Assert.Equal("J9", GridReference.GetLabel(0.999f, 0.999f, 10, 10));
    }

    [Fact]
    public void GridReference_ReturnsNullOutsideImage()
    {
        Assert.Null(GridReference.GetLabel(-0.01f, 0.5f, 8, 6));
        Assert.Null(GridReference.GetLabel(0.5f, 1.01f, 8, 6));
    }

    [Fact]
    public void GridReference_ClampsWhenCellsOutOfRange()
    {
        var (col, row) = GridReference.GetCell(5f, 5f, 8, 6);
        Assert.Equal(7, col);
        Assert.Equal(5, row);
    }

    [Fact]
    public void ArrowGeometry_ComputesRotationAndSize()
    {
        var start = new MapPoint(100f, 100f);
        var target = new MapPoint(200f, 100f);

        var (rotation, size) = MarkerGeometry.ComputeArrowGeometry(start, target, 1000f);
        Assert.Equal(0f, rotation, 3);
        Assert.Equal(0.1f, size, 4);

        // 屏幕 Y 向下，向下拖拽应为 90 度
        var down = MarkerGeometry.ComputeArrowGeometry(start, new MapPoint(100f, 300f), 1000f);
        Assert.Equal(90f, down.Rotation, 3);
        Assert.Equal(0.2f, down.Size, 4);
    }

    [Fact]
    public void ArrowTip_IsScreenSpaceAndMatchesRotation()
    {
        var vp = NewViewport();
        var marker = new Marker { Type = MarkerType.Arrow, X = 0.5f, Y = 0.5f, Size = 0.1f, Rotation = 90f };

        var start = vp.NormalizedToScreen(marker.X, marker.Y);
        var tip = MarkerGeometry.GetArrowTipScreen(marker, vp);

        Assert.Equal(start.X, tip.X, 3);
        Assert.Equal(start.Y + 0.1f * vp.DisplayWidth, tip.Y, 3);
        Assert.Equal(start.DistanceTo(tip), 0.1f * vp.DisplayWidth, 3);
    }

    [Fact]
    public void Handles_AreOnlyForSupportedTypes()
    {
        var vp = NewViewport();
        Assert.Single(MarkerGeometry.GetHandles(new Marker { Type = MarkerType.Circle, Size = 0.05f }, vp));
        Assert.Single(MarkerGeometry.GetHandles(new Marker { Type = MarkerType.Rectangle, EndX = 0.6f, EndY = 0.6f }, vp));
        Assert.Single(MarkerGeometry.GetHandles(new Marker { Type = MarkerType.Arrow, Size = 0.1f }, vp));
        Assert.Single(MarkerGeometry.GetHandles(new Marker { Type = MarkerType.Line, EndX = 0.6f, EndY = 0.6f }, vp));

        // 图标点与文本提供「大小」控制点（放置后可继续调整大小）
        var pointHandle = Assert.Single(MarkerGeometry.GetHandles(new Marker { Type = MarkerType.Point, Size = 0.02f }, vp));
        Assert.Equal(HandleKind.Size, pointHandle.Kind);
        var textHandle = Assert.Single(MarkerGeometry.GetHandles(new Marker { Type = MarkerType.Text, Size = 0.03f, Text = "abc" }, vp));
        Assert.Equal(HandleKind.Size, textHandle.Kind);

        // 笔画没有控制点（粗细用面板 / 工具栏调整）
        Assert.Empty(MarkerGeometry.GetHandles(new Marker
        {
            Type = MarkerType.Stroke,
            Points = new[] { new MarkerPoint(0.1f, 0.1f), new MarkerPoint(0.2f, 0.2f) },
        }, vp));
    }

    [Fact]
    public void ArrowThickness_DoesNotGrowWithLength()
    {
        var vp = NewViewport();
        var shortArrow = new Marker { Type = MarkerType.Arrow, X = 0.2f, Y = 0.2f, Size = 0.05f, Thickness = 0.005f };
        var longArrow = new Marker { Type = MarkerType.Arrow, X = 0.2f, Y = 0.2f, Size = 0.5f, Thickness = 0.005f };

        var shortWidth = MarkerGeometry.GetStrokeWidthPixels(shortArrow, vp);
        var longWidth = MarkerGeometry.GetStrokeWidthPixels(longArrow, vp);

        // 杆粗只取决于 Thickness，与长度无关
        Assert.Equal(shortWidth, longWidth, 4);
        Assert.Equal(0.005f * vp.DisplayWidth, longWidth, 3);
    }

    [Fact]
    public void StrokeWidth_FallsBackToLegacyRulesWhenThicknessIsZero()
    {
        var vp = NewViewport();
        var circle = new Marker { Type = MarkerType.Circle, Size = 0.2f };
        Assert.Equal(MathF.Min(2f, 0.2f * vp.DisplayWidth * 0.1f), MarkerGeometry.GetStrokeWidthPixels(circle, vp), 3);

        var line = new Marker { Type = MarkerType.Line, Size = 0.004f };
        Assert.Equal(0.004f * vp.DisplayWidth, MarkerGeometry.GetStrokeWidthPixels(line, vp), 3);
    }

    [Fact]
    public void StrokeMarker_HitTestAndBoundsFollowPolyline()
    {
        var vp = NewViewport();
        var stroke = new Marker
        {
            Type = MarkerType.Stroke,
            Thickness = 0.005f,
            Points = new[]
            {
                new MarkerPoint(0.1f, 0.1f),
                new MarkerPoint(0.5f, 0.1f),
                new MarkerPoint(0.5f, 0.5f),
            },
        };

        Assert.True(MarkerGeometry.HitTest(stroke, vp.NormalizedToScreen(0.3f, 0.1f), vp));
        Assert.True(MarkerGeometry.HitTest(stroke, vp.NormalizedToScreen(0.5f, 0.3f), vp));
        Assert.False(MarkerGeometry.HitTest(stroke, vp.NormalizedToScreen(0.9f, 0.9f), vp));

        var bounds = MarkerGeometry.GetScreenBounds(stroke, vp);
        var topLeft = vp.NormalizedToScreen(0.1f, 0.1f);
        var bottomRight = vp.NormalizedToScreen(0.5f, 0.5f);
        Assert.True(bounds.Left <= topLeft.X);
        Assert.True(bounds.Top <= topLeft.Y);
        Assert.True(bounds.Right >= bottomRight.X);
        Assert.True(bounds.Bottom >= bottomRight.Y);
    }

    [Fact]
    public void SizeHandle_ComputesSizeForIconAndText()
    {
        var vp = NewViewport();
        var point = new Marker { Type = MarkerType.Point, X = 0.5f, Y = 0.5f, Size = 0.02f };
        var anchor = vp.NormalizedToScreen(0.5f, 0.5f);

        // 从中心向右上拖到 40px 处 → 图标宽度 80px
        var dragged = new MapPoint(anchor.X + 40f, anchor.Y - 40f);
        var size = MarkerGeometry.ComputeSizeFromHandle(point, dragged, vp);
        Assert.Equal(80f / vp.DisplayWidth, size, 4);

        var text = new Marker { Type = MarkerType.Text, X = 0.2f, Y = 0.2f, Size = 0.03f, Text = "abc" };
        var textAnchor = vp.NormalizedToScreen(0.2f, 0.2f);
        var textDragged = new MapPoint(textAnchor.X + 10f, textAnchor.Y + 27f);
        var fontPx = MarkerGeometry.ComputeSizeFromHandle(text, textDragged, vp) * vp.DisplayWidth;
        Assert.Equal(20f, fontPx, 1); // 27 / 1.35 = 20
    }

    [Fact]
    public void HandleHitTest_UsesFixedScreenTolerance()
    {
        var vp = NewViewport();
        var marker = new Marker { Type = MarkerType.Circle, X = 0.5f, Y = 0.5f, Size = 0.1f };

        var handle = MarkerGeometry.GetHandles(marker, vp)[0];
        var near = new MapPoint(handle.ScreenX + 6f, handle.ScreenY + 6f);
        var far = new MapPoint(handle.ScreenX + 40f, handle.ScreenY);

        Assert.NotNull(MarkerGeometry.HitTestHandle(marker, near, vp));
        Assert.Null(MarkerGeometry.HitTestHandle(marker, far, vp));
    }

    [Fact]
    public void HitTest_FindsMarkersOfEveryType()
    {
        var vp = NewViewport();

        var point = new Marker { Type = MarkerType.Point, X = 0.2f, Y = 0.2f, Size = 0.02f };
        var pointScreen = vp.NormalizedToScreen(point.X, point.Y);
        Assert.True(MarkerGeometry.HitTest(point, pointScreen, vp));

        var circle = new Marker { Type = MarkerType.Circle, X = 0.5f, Y = 0.5f, Size = 0.1f };
        var circleCenter = vp.NormalizedToScreen(circle.X, circle.Y);
        var circleEdge = new MapPoint(circleCenter.X + 0.1f * vp.DisplayWidth, circleCenter.Y);
        Assert.True(MarkerGeometry.HitTest(circle, circleEdge, vp));

        var line = new Marker { Type = MarkerType.Line, X = 0.1f, Y = 0.1f, EndX = 0.9f, EndY = 0.9f, Size = 0.004f };
        var lineMid = vp.NormalizedToScreen(0.5f, 0.5f);
        Assert.True(MarkerGeometry.HitTest(line, lineMid, vp));

        var rect = new Marker { Type = MarkerType.Rectangle, X = 0.1f, Y = 0.1f, EndX = 0.6f, EndY = 0.6f, Size = 0.004f };
        var rectCornerTopLeft = vp.NormalizedToScreen(0.1f, 0.1f);
        Assert.True(MarkerGeometry.HitTest(rect, rectCornerTopLeft, vp));

        var arrow = new Marker { Type = MarkerType.Arrow, X = 0.2f, Y = 0.2f, Size = 0.15f, Rotation = 0f };
        var arrowShaft = vp.NormalizedToScreen(0.2f, 0.2f);
        Assert.True(MarkerGeometry.HitTest(arrow, arrowShaft, vp));

        var text = new Marker { Type = MarkerType.Text, X = 0.3f, Y = 0.3f, Size = 0.03f, Text = "测试文本" };
        var textAnchor = vp.NormalizedToScreen(0.3f, 0.3f);
        Assert.True(MarkerGeometry.HitTest(text, textAnchor, vp));
    }

    [Fact]
    public void HitTest_MissesFarAwayPoint()
    {
        var vp = NewViewport();
        var marker = new Marker { Type = MarkerType.Point, X = 0.1f, Y = 0.1f, Size = 0.02f };
        var far = vp.NormalizedToScreen(0.9f, 0.9f);
        Assert.False(MarkerGeometry.HitTest(marker, far, vp));
    }
}
