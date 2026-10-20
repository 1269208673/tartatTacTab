using System.Windows;
using System.Windows.Input;

namespace TarkovTacticalMap.App.Views;

/// <summary>通用文本输入对话框（放置 / 编辑文本标记时使用）。</summary>
public partial class TextInputDialog : Window
{
    public TextInputDialog(string title, string prompt, string initial)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        InputBox.Text = initial;
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    public string Value => InputBox.Text;

    /// <summary>弹出对话框；用户取消返回 null。</summary>
    public static string? Ask(Window? owner, string title, string prompt, string initial = "")
    {
        var dialog = new TextInputDialog(title, prompt, initial);
        if (owner is not null && owner.IsLoaded) dialog.Owner = owner;
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    private void OnOkClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) DialogResult = false;
        else if (e.Key == Key.Enter && !Keyboard.IsKeyDown(Key.LeftShift) && !Keyboard.IsKeyDown(Key.RightShift))
        {
            DialogResult = true;
            e.Handled = true;
        }
    }
}
