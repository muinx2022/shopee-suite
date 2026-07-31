using System;
using System.Windows;

namespace XuLyDonShopee.App.Views;

/// <summary>
/// Hộp thoại xác nhận (Đồng ý / Hủy) của module Đơn hàng. Bản Avalonia trả kết quả bằng
/// <c>Close(bool)</c>; ở WPF dùng <see cref="Window.DialogResult"/> (QĐ 13 của plan tổng).
/// <paramref name="infoOnly"/> = chỉ báo (một nút "Đóng").
/// </summary>
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
            CancelButton.Visibility = Visibility.Collapsed;
        }
    }

    private void OnOk(object sender, RoutedEventArgs e) => CloseWith(true);

    private void OnCancel(object sender, RoutedEventArgs e) => CloseWith(false);

    /// <summary>Đóng kèm kết quả. <c>DialogResult</c> chỉ gán được khi mở bằng <c>ShowDialog()</c>; nếu vì lý
    /// do nào đó cửa sổ được mở bằng <c>Show()</c> thì đóng thẳng (cùng lối phòng hờ như RowEditWindow bên suite).</summary>
    private void CloseWith(bool result)
    {
        try { DialogResult = result; }
        catch (InvalidOperationException) { Close(); }
    }
}
