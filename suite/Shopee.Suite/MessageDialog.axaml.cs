using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
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

        // Icon nút theo bảng ánh xạ chung (Icons.axaml): đồng ý → IconCheck, hủy/đóng → IconClose.
        if (confirm)
        {
            AddButton("Không", "IconClose", result: false, primary: false);
            AddButton("Có", "IconCheck", result: true, primary: true);
        }
        else
        {
            AddButton("OK", "IconCheck", result: false, primary: true);
        }

        // Enter = đóng với true nếu là confirm (nút Có), Esc = đóng với false.
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close(false);
            else if (e.Key == Key.Enter) Close(confirm);
        };
    }

    /// <summary>
    /// Nút của hộp thoại dựng bằng code (không có XAML) nhưng vẫn phải theo DÁNG CHUNG của app: icon vector
    /// bên trái + nhãn chữ, màu ngữ nghĩa do class quyết (theme chỉ tô màu ICON). Geometry lấy từ bộ icon dùng
    /// chung ở <c>Icons.axaml</c> qua Application.Resources — KHÔNG chép path data vào đây.
    /// </summary>
    private void AddButton(string content, string iconKey, bool result, bool primary)
    {
        var b = new Button
        {
            MinWidth = 92,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        };
        if (primary) b.Classes.Add("primary");

        var label = new TextBlock { Text = content, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        if (Application.Current?.TryFindResource(iconKey, out var res) == true && res is Geometry geometry)
        {
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 7 };
            row.Children.Add(new PathIcon { Data = geometry });
            row.Children.Add(label);
            b.Content = row;
        }
        else
        {
            // Không tìm thấy icon (không nên xảy ra) → vẫn hiện nhãn, đừng để nút trống trơn.
            b.Content = label;
        }

        b.Click += (_, _) => Close(result);
        Buttons.Children.Add(b);
    }
}
