using System;
using System.Windows;
using XuLyDonShopee.App.ViewModels;

namespace XuLyDonShopee.App.Views;

/// <summary>
/// Cửa sổ CHẨN ĐOÁN "đơn kết thúc chưa dọn được" (H2.5) — mở từ badge "⏳ Chờ đẩy" trên thanh trạng thái.
/// Toàn bộ nội dung bind vào <see cref="ChanDoanDonViewModel"/>; code-behind chỉ lo nút Đóng.
/// </summary>
public partial class ChanDoanDonDialog : Window
{
    // Constructor không tham số cho designer/XAML previewer.
    public ChanDoanDonDialog()
    {
        InitializeComponent();
    }

    public ChanDoanDonDialog(ChanDoanDonViewModel vm) : this()
    {
        DataContext = vm;
    }

    /// <summary>Đóng cửa sổ. <c>DialogResult</c> chỉ gán được khi mở bằng <c>ShowDialog()</c> — mở bằng
    /// <c>Show()</c> thì đóng thẳng (cùng lối phòng hờ như <see cref="ConfirmDialog"/>).</summary>
    private void OnClose(object sender, RoutedEventArgs e)
    {
        try { DialogResult = false; }
        catch (InvalidOperationException) { Close(); }
    }
}
