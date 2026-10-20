using System.Windows;

namespace TarkovTacticalMap.App.Views;

/// <summary>
/// 网格设置对话框：只调「一级网格密度（每边最多格数）」，
/// 行列数按图片宽高比自动推导，保证格子接近正方形；二级及以后由缩放自动递归显示。
/// </summary>
public partial class GridSettingsDialog : Window
{
    public GridSettingsDialog(int density, int cols, int rows, int imageWidth, int imageHeight)
    {
        InitializeComponent();
        DensityBox.Text = density.ToString();
        ImageInfoText.Text = imageWidth > 0 && imageHeight > 0
            ? $"当前图片 {imageWidth}×{imageHeight} px；按当前密度推导：{cols} 列 × {rows} 行"
            : "尚未加载地图图片";
        UpdatePreview(cols, rows, imageWidth, imageHeight);
        Loaded += (_, _) => DensityBox.Focus();
    }

    public int Density { get; private set; }

    public static int? Ask(Window? owner, int density, int cols, int rows, int imageWidth, int imageHeight)
    {
        var dialog = new GridSettingsDialog(density, cols, rows, imageWidth, imageHeight);
        if (owner is not null && owner.IsLoaded) dialog.Owner = owner;
        return dialog.ShowDialog() == true ? dialog.Density : null;
    }

    private void UpdatePreview(int cols, int rows, int imageWidth, int imageHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            PreviewText.Text = "";
            return;
        }

        var cellW = imageWidth / (float)Math.Max(cols, 1);
        var cellH = imageHeight / (float)Math.Max(rows, 1);
        PreviewText.Text =
            $"一级网格：{cols} 列 × {rows} 行（格子约 {cellW:F0}×{cellH:F0} px）\n" +
            $"每级上限 列 A~J / 行 0~9；二级起每格再按 A~J × 0~9 细分，缩放后自动出现更深层，\n" +
            $"引用形如 D5:A3:E1:A0";
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(DensityBox.Text.Trim(), out var density) || density < 2 || density > TarkovTacticalMap.Core.Models.MapInfo.MaxGrid)
        {
            MessageBox.Show($"网格密度必须是 2~{TarkovTacticalMap.Core.Models.MapInfo.MaxGrid} 的整数" +
                            "（列标签 A~J，行号 0~9）。",
                "输入有误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Density = density;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
