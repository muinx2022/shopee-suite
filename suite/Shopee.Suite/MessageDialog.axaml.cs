using Avalonia.Controls;
using Avalonia.Input;
using Shopee.Suite.Services;

namespace Shopee.Suite;

/// <summary>
/// Hộp thoại tự vẽ, căn giữa CỬA SỔ APP (WindowStartupLocation=CenterOwner). Kết quả trả qua
/// <c>ShowDialog&lt;bool&gt;</c>: Có/OK → true, Không/Hủy → false. Thay MessageBox của WPF.
/// </summary>
public partial class MessageDialog : Window
{
    public MessageDialog() => InitializeComponent();

    public MessageDialog(string text, string caption, bool confirm, DialogIcon icon)
    {
        InitializeComponent();
        Title = string.IsNullOrWhiteSpace(caption) ? " " : caption;
        MessageText.Text = text;

        IconText.Text = icon switch
        {
            DialogIcon.Error => "⛔",
            DialogIcon.Warning => "⚠",
            DialogIcon.Question => "❓",
            DialogIcon.Info => "ℹ",
            _ => "",
        };
        if (IconText.Text.Length == 0) IconText.IsVisible = false;

        if (confirm)
        {
            AddButton("Không", result: false, primary: false);
            AddButton("Có", result: true, primary: true);
        }
        else
        {
            AddButton("OK", result: false, primary: true);
        }

        // Enter = đóng với true nếu là confirm (nút Có), Esc = đóng với false.
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close(false);
            else if (e.Key == Key.Enter) Close(confirm);
        };
    }

    private void AddButton(string content, bool result, bool primary)
    {
        var b = new Button
        {
            Content = content,
            MinWidth = 92,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        };
        if (primary) b.Classes.Add("primary");
        b.Click += (_, _) => Close(result);
        Buttons.Children.Add(b);
    }
}
