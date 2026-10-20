using System.Text;
using SkiaSharp;
using TarkovTacticalMap.App.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace TarkovTacticalMap.Tests;

/// <summary>
/// 内置图标形状自检：把每个图标栅格化成 ASCII 字符画输出到测试日志，便于人工核对形状是否合理，
/// 并断言基本几何特征（覆盖率、中心/角落的填充状态）。
/// </summary>
public class IconShapeTests
{
    private readonly ITestOutputHelper _output;

    public IconShapeTests(ITestOutputHelper output) => _output = output;

    private const int Grid = 24;

    [Fact]
    public void AllIcons_HaveSaneRasterCoverage()
    {
        var asciiAll = new StringBuilder();
        foreach (var iconId in IconLibrary.IconIds)
        {
            var mask = Rasterize(iconId);
            var filled = mask.Count(c => c);
            var ratio = filled / (double)(Grid * Grid);

            var art = new StringBuilder();
            art.AppendLine($"=== {iconId} ({IconLibrary.Labels[iconId]}) 覆盖率 {ratio:P1} ===");
            for (var y = 0; y < Grid; y++)
            {
                for (var x = 0; x < Grid; x++)
                {
                    art.Append(mask[y * Grid + x] ? '#' : '.');
                }
                art.AppendLine();
            }
            _output.WriteLine(art.ToString());

            Assert.InRange(ratio, 0.05, 0.75);
            // 图标应当基本居中，四角大多为空
            var corners = new[] { 0, Grid - 1, Grid * (Grid - 1), Grid * Grid - 1 };
            Assert.True(corners.Count(i => mask[i]) <= 1, $"{iconId} 四角不应被填充");

            asciiAll.AppendLine(art.ToString());
        }

        // 把字符画落盘，便于人工核对形状
        var dir = ArtifactDirectory();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "icons-ascii.txt"), asciiAll.ToString());
    }

    [Fact]
    public void Kill_HasFilledCenterAndHollowRing()
    {
        var mask = Rasterize("kill");
        // 中心点（中心点标记）应填充
        Assert.True(Get(mask, 12, 12), "kill 中心应为实心点");
        // 环外（半径外）应为空
        Assert.False(Get(mask, 2, 2), "kill 远角应为空");
    }

    [Fact]
    public void Target_HasBullseyeRings()
    {
        var mask = Rasterize("target");
        // 中心点（半径 &lt; 1.3）
        Assert.True(Get(mask, 12, 12), "target 中心应为实心");
        // 中心点与中盘之间的空隙（半径约 2）
        Assert.False(Get(mask, 12, 10), "target 中心点与中盘之间应为空");
        // 中盘（半径约 3）
        Assert.True(Get(mask, 12, 9), "target 中盘应有填充");
        // 中盘与外环之间的空隙（半径约 5）
        Assert.False(Get(mask, 12, 7), "target 中盘与外环之间应为空");
        // 外环（半径约 8）
        Assert.True(Get(mask, 12, 4), "target 外环应有填充");
    }

    [Fact]
    public void Key_HasHollowRingAtTheHead()
    {
        var mask = Rasterize("key");
        Assert.False(Get(mask, 7, 9), "key 钥匙孔应为空");
        Assert.True(Get(mask, 7, 4), "key 环体应有填充");
        Assert.True(Get(mask, 18, 8), "key 杆部应有填充");
    }

    [Fact]
    public void Document_HasFoldedCornerHole()
    {
        var mask = Rasterize("document");
        Assert.False(Get(mask, 17, 3), "document 折角应为空");
        Assert.True(Get(mask, 10, 9), "document 纸张应有填充");
        Assert.False(Get(mask, 10, 12), "document 文本行应为空");
    }

    [Fact]
    public void Scout_IsEyeShaped()
    {
        var mask = Rasterize("scout");
        Assert.True(Get(mask, 12, 12), "scout 瞳孔中心应填充");
        Assert.True(Get(mask, 4, 12), "scout 眼形左端应有描边");
        Assert.False(Get(mask, 12, 4), "scout 顶部中心应为空");
    }

    private static bool Get(bool[] mask, int x, int y) => mask[y * Grid + x];

    private static string ArtifactDirectory()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            if (File.Exists(Path.Combine(dir, "TarkovTacticalMap.sln")))
            {
                return Path.Combine(dir, "artifacts", "renders");
            }
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar)) ?? string.Empty;
        }
        return Path.Combine(Path.GetTempPath(), "tarkov-tactical-map-renders");
    }

    private static bool[] Rasterize(string iconId)
    {
        using var bitmap = new SKBitmap(Grid, Grid, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            Assert.True(IconLibrary.Default.TryDraw(canvas, iconId, new SKRect(0, 0, Grid, Grid), SKColors.White));
        }

        var mask = new bool[Grid * Grid];
        for (var y = 0; y < Grid; y++)
        {
            for (var x = 0; x < Grid; x++)
            {
                mask[y * Grid + x] = bitmap.GetPixel(x, y).Alpha > 128;
            }
        }
        return mask;
    }
}
