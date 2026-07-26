using Avalonia.Controls;
using Avalonia.Interactivity;

namespace XuLyDonShopee.App.Views;

public partial class ConfirmDialog : Window
{
    // Constructor không tham số cho designer/XAML previewer.
    public ConfirmDialog() : this("Xác nhận", string.Empty)
    {
    }

    public ConfirmDialog(string title, string message, bool infoOnly = false)
    {
        InitializeComponent();

        Title = title;
        MessageText.Text = message;

        if (infoOnly)
        {
            // Đổi NHÃN thôi (không gán lại Content — Content nay là icon + nhãn, gán chuỗi sẽ mất icon).
            OkText.Text = "Đóng";
            CancelButton.IsVisible = false;
        }
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
