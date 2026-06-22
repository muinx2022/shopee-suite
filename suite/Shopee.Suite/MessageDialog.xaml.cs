using System.Windows;
using System.Windows.Controls;

namespace Shopee.Suite;

/// <summary>
/// Hộp thoại WPF tự vẽ, căn giữa CỬA SỔ APP (WindowStartupLocation=CenterOwner) — không lệch khi
/// Windows scaling ≠ 100% (native MessageBox căn owner sai dưới DPI). Thay cho MessageBox mặc định.
/// </summary>
public partial class MessageDialog : Window
{
    public MessageBoxResult Result { get; private set; }

    public MessageDialog(string text, string caption, MessageBoxButton buttons, MessageBoxImage icon)
    {
        InitializeComponent();
        Title = string.IsNullOrWhiteSpace(caption) ? " " : caption;
        MessageText.Text = text;

        IconText.Text = icon switch
        {
            MessageBoxImage.Error => "⛔",
            MessageBoxImage.Warning => "⚠",
            MessageBoxImage.Question => "❓",
            MessageBoxImage.Information => "ℹ",
            _ => "",
        };
        if (IconText.Text.Length == 0) IconText.Visibility = Visibility.Collapsed;

        // ESC kích nút IsCancel, Enter kích nút IsDefault → trả Result đúng kể cả không bấm chuột.
        Result = buttons switch
        {
            MessageBoxButton.OK => MessageBoxResult.OK,
            MessageBoxButton.YesNo => MessageBoxResult.No,
            _ => MessageBoxResult.Cancel,
        };

        switch (buttons)
        {
            case MessageBoxButton.OK:
                AddButton("OK", MessageBoxResult.OK, primary: true, isDefault: true, isCancel: true);
                break;
            case MessageBoxButton.OKCancel:
                AddButton("Hủy", MessageBoxResult.Cancel, primary: false, isDefault: false, isCancel: true);
                AddButton("OK", MessageBoxResult.OK, primary: true, isDefault: true, isCancel: false);
                break;
            case MessageBoxButton.YesNo:
                AddButton("Không", MessageBoxResult.No, primary: false, isDefault: false, isCancel: true);
                AddButton("Có", MessageBoxResult.Yes, primary: true, isDefault: true, isCancel: false);
                break;
            case MessageBoxButton.YesNoCancel:
                AddButton("Hủy", MessageBoxResult.Cancel, primary: false, isDefault: false, isCancel: true);
                AddButton("Không", MessageBoxResult.No, primary: false, isDefault: false, isCancel: false);
                AddButton("Có", MessageBoxResult.Yes, primary: true, isDefault: true, isCancel: false);
                break;
        }
    }

    private void AddButton(string content, MessageBoxResult result, bool primary, bool isDefault, bool isCancel)
    {
        var b = new Button
        {
            Content = content,
            MinWidth = 92,
            Margin = new Thickness(10, 0, 0, 0),
            IsDefault = isDefault,
            IsCancel = isCancel,
        };
        if (primary && TryFindResource("PrimaryButton") is Style ps) b.Style = ps;
        b.Click += (_, _) => { Result = result; DialogResult = true; };
        Buttons.Children.Add(b);
    }
}
