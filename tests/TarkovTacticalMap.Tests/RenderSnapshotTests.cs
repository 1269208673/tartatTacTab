using SkiaSharp;
using TarkovTacticalMap.App.Rendering;
using TarkovTacticalMap.Core.Geometry;
using TarkovTacticalMap.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace TarkovTacticalMap.Tests;

/// <summary>
/// 渲染层自检：把场景真正画到 SKBitmap 上，断言关键像素颜色，并把 PNG 快照写到 artifacts/renders 供人工核对。
/// 同时验证「未知 IconId / 未知 MarkerType 安全回退」。
/// </summary>
public class RenderSnapshotTests
{
    private readonly ITestOutputHelper _output;

    public RenderSnapshotTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void IconLibrary_LoadsAllFiveBuiltInIcons()
    {
        var lib = IconLibrary.Default;
        Assert.Equal(5, lib.Loaded.Count);
        foreach (var id in IconLibrary.IconIds)
        {
            Assert.True(lib.Contains(id), $"缺少内置图标 {id}");
            Assert.True(IconLibrary.Labels.ContainsKey(id));
        }
        Assert.False(lib.Contains("unknown-icon"));
        Assert.False(lib.Contains(null));
    }

    [Fact]
    public void IconLibrary_DrawsOnlyInsideTargetRect()
    {
        const int size = 48;
        using var bitmap = new SKBitmap(size, size);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        var color = new SKColor(0xFFFF5252);

        Assert.True(IconLibrary.Default.TryDraw(canvas, "kill", new SKRect(0, 0, size, size), color));

        // 图标内部应有着色像素
        var colored = CountPixels(bitmap, color, tolerance: 12);
        Assert.True(colored > 40, $"着色像素过少：{colored}");

        // 未着色像素仍为透明
        Assert.Equal(0, bitmap.GetPixel(0, 0).Alpha);
    }

    [Fact]
    public void IconLibrary_RespectsColorArgument()
    {
        const int size = 48;
        using var bitmap = new SKBitmap(size, size);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        var color = new SKColor(0xFF40C4FF);
        Assert.True(IconLibrary.Default.TryDraw(canvas, "target", new SKRect(0, 0, size, size), color));
        Assert.True(CountPixels(bitmap, color, tolerance: 12) > 40);
    }

    [Fact]
    public void TryDraw_ReturnsFalse_ForUnknownOrNullIcon()
    {
        using var bitmap = new SKBitmap(32, 32);
        using var canvas = new SKCanvas(bitmap);
        Assert.False(IconLibrary.Default.TryDraw(canvas, "no-such-icon", new SKRect(0, 0, 32, 32), SKColors.White));
        Assert.False(IconLibrary.Default.TryDraw(canvas, null, new SKRect(0, 0, 32, 32), SKColors.White));
    }

    [Fact]
    public void Renderer_DrawsAllMarkerTypes_WithExpectedColors()
    {
        var artifactDir = ArtifactDirectory();
        Directory.CreateDirectory(artifactDir);

        using var background = SKImage.FromEncodedData(BlankMapFactory.CreatePng(1200, 800));
        Assert.NotNull(background);

        var viewport = new ViewportState();
        viewport.SetCanvas(900f, 600f);
        viewport.SetImage(1200, 800);

        var map = new MapInfo
        {
            ImageHash = "hash",
            ImageWidth = 1200,
            ImageHeight = 800,
            GridCols = 8,
            GridRows = 6,
            GridVisible = true,
        };

        var markers = new List<Marker>
        {
            IconMarker("kill", 0.10f, 0.12f, "#FF5252"),
            IconMarker("scout", 0.22f, 0.12f, "#FF9800"),
            IconMarker("document", 0.34f, 0.12f, "#FFE066"),
            IconMarker("key", 0.46f, 0.12f, "#69F0AE"),
            IconMarker("target", 0.58f, 0.12f, "#40C4FF"),
            IconMarker(null, 0.70f, 0.12f, "#7C4DFF"),
            new Marker
            {
                Id = "text", Type = MarkerType.Text, X = 0.08f, Y = 0.26f, Size = 0.05f,
                Color = "#FFFFFF", Text = "狙击点位 A1", OwnerId = "p", CreatedAt = 8,
            },
            new Marker
            {
                Id = "arrow", Type = MarkerType.Arrow, X = 0.42f, Y = 0.28f, Size = 0.16f,
                Rotation = 35f, Color = "#FF5252", OwnerId = "p", CreatedAt = 9,
            },
            new Marker
            {
                Id = "line", Type = MarkerType.Line, X = 0.08f, Y = 0.52f, EndX = 0.38f, EndY = 0.52f,
                Size = 0.006f, Color = "#69F0AE", OwnerId = "p", CreatedAt = 10,
            },
            new Marker
            {
                Id = "circle", Type = MarkerType.Circle, X = 0.62f, Y = 0.52f, Size = 0.09f,
                Color = "#40C4FF", OwnerId = "p", CreatedAt = 11,
            },
            new Marker
            {
                Id = "rect", Type = MarkerType.Rectangle, X = 0.08f, Y = 0.68f, EndX = 0.40f, EndY = 0.90f,
                Size = 0.005f, Color = "#FF80AB", OwnerId = "p", CreatedAt = 12,
            },
        };

        var scene = new RenderScene
        {
            Viewport = viewport,
            Map = map,
            Markers = markers,
            SelectedMarkerId = "circle",
            Background = background,
            HoverGridLabel = "C3",
        };

        using var bitmap = new SKBitmap(900, 600, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            new MapRenderer().Draw(canvas, scene);
        }

        var path = Path.Combine(artifactDir, "scene-all-markers.png");
        Save(bitmap, path);
        _output.WriteLine("渲染快照：" + path);

        // 五种图标与默认圆点都应出现各自的颜色
        AssertColorNear(bitmap, viewport, "#FF5252", 0.10f, 0.12f, 16);
        AssertColorNear(bitmap, viewport, "#FF9800", 0.22f, 0.12f, 16);
        AssertColorNear(bitmap, viewport, "#FFE066", 0.34f, 0.12f, 16);
        AssertColorNear(bitmap, viewport, "#69F0AE", 0.46f, 0.12f, 16);
        AssertColorNear(bitmap, viewport, "#40C4FF", 0.58f, 0.12f, 16);
        AssertColorNear(bitmap, viewport, "#7C4DFF", 0.70f, 0.12f, 16);

        // 文本 / 箭头 / 线 / 圆 / 方框
        AssertColorNear(bitmap, viewport, "#FFFFFF", 0.09f, 0.28f, 30);
        AssertColorNear(bitmap, viewport, "#FF5252", 0.47f, 0.32f, 30);
        AssertColorNear(bitmap, viewport, "#69F0AE", 0.22f, 0.52f, 6);
        // 圆的描边：取圆顶部（右下角被半径控制点覆盖）。半径 81px → 归一化纵向偏移 81/600
        AssertColorNear(bitmap, viewport, "#40C4FF", 0.62f, 0.52f - 81f / 600f, 6);
        AssertColorNear(bitmap, viewport, "#FF80AB", 0.24f, 0.90f, 8);

        // 选中标记的虚线框 / 控制点（强调色）应出现在半径控制点附近
        AssertColorNear(bitmap, viewport, "#4FC3F7", 0.71f, 0.52f, 12);
    }

    [Fact]
    public void Renderer_HandlesUnknownIconAndUnknownTypeWithoutCrash()
    {
        using var background = SKImage.FromEncodedData(BlankMapFactory.CreatePng(400, 300));
        var viewport = new ViewportState();
        viewport.SetCanvas(400f, 300f);
        viewport.SetImage(400, 300);

        var markers = new List<Marker>
        {
            new()
            {
                Id = "a", Type = MarkerType.Point, X = 0.3f, Y = 0.3f, Size = 0.05f,
                Color = "#FF5252", IconId = "totally-unknown", OwnerId = "p", CreatedAt = 1,
            },
            new()
            {
                Id = "b", Type = (MarkerType)77, X = 0.7f, Y = 0.7f, Size = 0.05f,
                Color = "#69F0AE", OwnerId = "p", CreatedAt = 2,
            },
        };

        var scene = new RenderScene
        {
            Viewport = viewport,
            Map = new MapInfo { ImageWidth = 400, ImageHeight = 300, GridVisible = true },
            Markers = markers,
            Background = background,
        };

        using var bitmap = new SKBitmap(400, 300, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            var exception = Record.Exception(() => new MapRenderer().Draw(canvas, scene));
            Assert.Null(exception);
        }

        // 未知 IconId → 回退默认圆点；未知 Type → 回退默认圆点
        AssertColorNear(bitmap, viewport, "#FF5252", 0.3f, 0.3f, 12);
        AssertColorNear(bitmap, viewport, "#69F0AE", 0.7f, 0.7f, 12);
    }

    [Fact]
    public void Renderer_DrawsGridAndHandlesSelectionWithoutBackground()
    {
        var viewport = new ViewportState();
        viewport.SetCanvas(600f, 400f);
        viewport.SetImage(600, 400);
        viewport.ZoomAt(300f, 200f, 3f);

        var scene = new RenderScene
        {
            Viewport = viewport,
            Map = new MapInfo { ImageWidth = 600, ImageHeight = 400, GridVisible = true, GridCols = 8, GridRows = 6 },
            Markers = new List<Marker>
            {
                new() { Id = "r", Type = MarkerType.Rectangle, X = 0.3f, Y = 0.3f, EndX = 0.6f, EndY = 0.6f, Size = 0.006f, Color = "#FFE066", OwnerId = "p", CreatedAt = 1 },
            },
            SelectedMarkerId = "r",
            Background = null,
        };

        using var bitmap = new SKBitmap(600, 400, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            Assert.Null(Record.Exception(() => new MapRenderer().Draw(canvas, scene)));
        }

        var artifactDir = ArtifactDirectory();
        Directory.CreateDirectory(artifactDir);
        Save(bitmap, Path.Combine(artifactDir, "scene-grid-selection.png"));
    }

    [Fact]
    public void RendererExportsIconSheet_ForVisualInspection()
    {
        var artifactDir = ArtifactDirectory();
        Directory.CreateDirectory(artifactDir);

        const int cell = 96;
        using var bitmap = new SKBitmap(cell * IconLibrary.IconIds.Length, cell, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(0xFF23262D));
            var colors = new[] { "#FF5252", "#FF9800", "#FFE066", "#69F0AE", "#40C4FF" };
            for (var i = 0; i < IconLibrary.IconIds.Length; i++)
            {
                var dest = new SKRect(i * cell + 12, 12, i * cell + cell - 12, cell - 12);
                Assert.True(IconLibrary.Default.TryDraw(canvas, IconLibrary.IconIds[i], dest,
                    new SKColor(Core.Utils.ColorHex.ToArgb(colors[i]))));
            }
        }

        Save(bitmap, Path.Combine(artifactDir, "icons-sheet.png"));
    }

    // ── 辅助 ────────────────────────────────────────────────────────────

    private static Marker IconMarker(string? iconId, float x, float y, string color) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Type = MarkerType.Point,
        X = x,
        Y = y,
        Size = 0.045f,
        Color = color,
        IconId = iconId,
        OwnerId = "p",
        CreatedAt = (long)(x * 1000),
    };

    private static void Save(SKBitmap bitmap, string path)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    private static void AssertColorNear(SKBitmap bitmap, ViewportState viewport, string hexColor,
        float nx, float ny, int radius)
    {
        var expected = new SKColor(Core.Utils.ColorHex.ToArgb(hexColor));
        var screen = viewport.NormalizedToScreen(nx, ny);
        var left = Math.Clamp((int)screen.X - radius, 0, bitmap.Width - 1);
        var right = Math.Clamp((int)screen.X + radius, 0, bitmap.Width - 1);
        var top = Math.Clamp((int)screen.Y - radius, 0, bitmap.Height - 1);
        var bottom = Math.Clamp((int)screen.Y + radius, 0, bitmap.Height - 1);

        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (ColorNear(pixel, expected, 24)) return;
            }
        }

        Assert.Fail($"在 ({screen.X:F0},{screen.Y:F0}) 附近未找到颜色 {hexColor} 的像素");
    }

    private static bool ColorNear(SKColor actual, SKColor expected, int tolerance)
        => Math.Abs(actual.Red - expected.Red) <= tolerance
           && Math.Abs(actual.Green - expected.Green) <= tolerance
           && Math.Abs(actual.Blue - expected.Blue) <= tolerance
           && actual.Alpha > 30;

    private static int CountPixels(SKBitmap bitmap, SKColor color, int tolerance)
    {
        var count = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Alpha > 30 && ColorNear(pixel, color, tolerance)) count++;
            }
        }
        return count;
    }

    private static string ArtifactDirectory()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            if (File.Exists(Path.Combine(dir, "TarkovTacticalMap.sln")) ||
                File.Exists(Path.Combine(dir, "TarkovTacticalMap.slnx")))
            {
                return Path.Combine(dir, "artifacts", "renders");
            }
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar)) ?? string.Empty;
        }
        return Path.Combine(Path.GetTempPath(), "tarkov-tactical-map-renders");
    }
}
