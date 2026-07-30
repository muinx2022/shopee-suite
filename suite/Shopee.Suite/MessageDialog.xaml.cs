using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shopee.Suite.Controls;
using Shopee.Suite.Services;

namespace Shopee.Suite;

/// <summary>
/// Hộp thoại tự vẽ, căn giữa CỬA SỔ APP (WindowStartupLocation=CenterOwner) — không lệch khi Windows
/// scaling ≠ 100% (MessageBox native căn owner sai dưới DPI). Kết quả đọc qua <see cref="Result"/>:
/// Có/OK → true, Không/Hủy → false (<c>ShowDialog()</c> chỉ trả <c>bool?</c> nên kết quả để ở property).
/// </summary>
public partial class MessageDialog : Window
{
    /// <summary>Kết quả người dùng chọn: true = Có/OK.</summary>
    public bool Result { get; private set; }

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
        if (IconText.Text.Length == 0) IconText.Visibility = Visibility.Collapsed;

        // Icon nút theo bảng ánh xạ chung (Icons.xaml): đồng ý → IconCheck, hủy/đóng → IconClose.
        // Enter kích nút IsDefault (Có/OK), Esc kích nút IsCancel (Không/OK) — thay handler KeyDown của bản cũ.
        if (confirm)
        {
            AddButton("Không", "IconClose", result: false, primary: false, isDefault: false, isCancel: true);
            AddButton("Có", "IconCheck", result: true, primary: true, isDefault: true, isCancel: false);
        }
        else
        {
            AddButton("OK", "IconCheck", result: false, primary: true, isDefault: true, isCancel: true);
        }
    }

    /// <summary>
    /// Nút của hộp thoại dựng bằng code (không có XAML) nhưng vẫn phải theo DÁNG CHUNG của app: icon vector
    /// bên trái + nhãn chữ, màu ngữ nghĩa do style quyết (theme chỉ tô màu ICON). Geometry lấy từ bộ icon dùng
    /// chung ở <c>Themes/Icons.xaml</c> qua Application.Resources — KHÔNG chép path data vào đây.
    /// </summary>
    private void AddButton(string content, string iconKey, bool result, bool primary, bool isDefault, bool isCancel)
    {
        var b = new Button
        {
            MinWidth = 92,
            Margin = new Thickness(10, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsDefault = isDefault,
            IsCancel = isCancel,
        };
        if (primary && TryFindResource("primary") is Style ps) b.Style = ps;

        var label = new TextBlock { Text = content, VerticalAlignment = VerticalAlignment.Center };
        if (TryFindResource(iconKey) is Geometry geometry)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new PathIcon { Data = geometry, Margin = new Thickness(0, 0, 7, 0) });
            row.Children.Add(label);
            b.Content = row;
        }
        else
        {
            // Không tìm thấy icon (không nên xảy ra) → vẫn hiện nhãn, đừng để nút trống trơn.
            b.Content = label;
        }

        b.Click += (_, _) =>
        {
            Result = result;
            // DialogResult chỉ gán được khi mở bằng ShowDialog; đường fallback Show() (chưa có cửa sổ chính)
            // thì đóng thẳng.
            try { DialogResult = true; }
            catch (InvalidOperationException) { Close(); }
        };
        Buttons.Children.Add(b);
    }
}
