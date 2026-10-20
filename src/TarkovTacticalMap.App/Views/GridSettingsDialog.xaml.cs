using System.Windows;

namespace TarkovTacticalMap.App.Views;

/// <summary>网格行列数设置对话框（仅本机视图设置，不写入协议）。</summary>
public partial class GridSettingsDialog : Window
{
    public GridSettingsDialog(int cols, int rows)
    {
        InitializeComponent();
        ColsBox.Text = cols.ToString();
        RowsBox.Text = rows.ToString();
        Loaded += (_, _) => ColsBox.Focus();
    }

    public int Cols { get; private set; }

    public int Rows { get; private set; }

    public static (int Cols, int Rows)? Ask(Window? owner, int cols, int rows)
    {
        var dialog = new GridSettingsDialog(cols, rows);
        if (owner is not null && owner.IsLoaded) dialog.Owner = owner;
        return dialog.ShowDialog() == true ? (dialog.Cols, dialog.Rows) : null;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(ColsBox.Text, out var cols) || cols < 1 || cols > 26)
        {
            MessageBox.Show("列数必须是 1~26 的整数（列标签为 A~Z）。", "输入有误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(RowsBox.Text, out var rows) || rows < 1 || rows > 26)
        {
            MessageBox.Show("行数必须是 1~26 的整数。", "输入有误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Cols = cols;
        Rows = rows;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
