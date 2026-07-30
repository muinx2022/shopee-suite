using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Shopee.Suite.Views;

/// <summary>
/// Cửa sổ TẠM cho các cửa sổ phụ chưa port sang WPF (đợt 2–4). Giữ đúng tên lớp + chữ ký hàm dựng của bản
/// cũ để ViewModel gọi mở cửa sổ KHÔNG phải sửa; nội dung chỉ là một dòng báo "đang port" + nút Đóng.
/// <para>
/// Khi port cửa sổ thật: XOÁ lớp con tương ứng (file .cs) và thêm cặp .xaml/.xaml.cs cùng tên.
/// </para>
/// </summary>
public abstract class PortingWindow : Window
{
    protected PortingWindow(string title, string screenName, string batch)
    {
        Title = title;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush?)TryFindResource("WindowBackgroundBrush") ?? Brushes.White;
        if (TryFindResource("UiFont") is FontFamily font) FontFamily = font;

        var stack = new StackPanel { Margin = new Thickness(28, 24, 28, 20) };
        stack.Children.Add(new TextBlock
        {
            Text = screenName,
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush?)TryFindResource("TextPrimaryBrush") ?? Brushes.Black,
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"Cửa sổ này đang được port sang bản Windows (WPF) — dự kiến {batch}. " +
                   "Phần xử lý bên dưới không đổi, chỉ giao diện là chưa dựng lại.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = (Brush?)TryFindResource("TextSecondaryBrush") ?? Brushes.Gray,
        });

        var close = new Button
        {
            Content = "Đóng",
            MinWidth = 92,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
            IsCancel = true,
            IsDefault = true,
        };
        close.Click += (_, _) =>
        {
            try { DialogResult = false; }
            catch (InvalidOperationException) { Close(); }
        };
        stack.Children.Add(close);

        Content = stack;
    }
}
