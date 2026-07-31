using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using XuLyDonShopee.App.ViewModels;

namespace XuLyDonShopee.App.Views;

/// <summary>
/// Hộp thoại thông tin cơ bản của MỘT đơn (chỉ đọc) + cho đổi trạng thái. ComboBox trạng thái nhận nguồn là
/// danh sách trạng thái ĐÃ SYNC về (chuỗi tự do, không enum). Bấm "Lưu" → <see cref="Result"/> = trạng thái
/// đã chọn; "Hủy" → <see cref="Result"/> = null. Người gọi (OrdersViewModel qua DialogService) tự quyết có
/// ghi DB không.
/// <para>
/// Bản Avalonia trả kết quả tuỳ biến bằng <c>Close(string?)</c>; WPF chỉ có <c>DialogResult</c> kiểu
/// <c>bool?</c> nên kết quả chuỗi để ở property <see cref="Result"/> (QĐ 13 của plan tổng).
/// </para>
/// </summary>
public partial class OrderDetailDialog : Window
{
    // Constructor không tham số cho designer/XAML previewer.
    public OrderDetailDialog()
    {
        InitializeComponent();
    }

    public OrderDetailDialog(OrderRowViewModel row, IReadOnlyList<string> statuses)
    {
        InitializeComponent();

        OrderSnText.Text = row.OrderSn;
        BuyerText.Text = row.Buyer;
        ProductText.Text = row.Product;
        TotalText.Text = row.Total;
        PaymentText.Text = row.Payment;
        StatusText.Text = row.Status;
        CarrierText.Text = row.Carrier;
        TrackingText.Text = row.Tracking;
        SyncedAtText.Text = row.SyncedAtDisplay;

        StatusCombo.ItemsSource = statuses;
        // Chọn sẵn trạng thái hiện tại nếu nó có trong danh sách đã sync (thường có). Không có → để trống.
        StatusCombo.SelectedItem = statuses.Contains(row.Status) ? row.Status : null;
    }

    /// <summary>Trạng thái người dùng chọn khi bấm "Lưu"; null khi Hủy (hoặc chưa chọn gì).</summary>
    public string? Result { get; private set; }

    /// <summary>Lưu: trả về trạng thái đang chọn (null nếu chưa chọn gì).</summary>
    private void OnSave(object sender, RoutedEventArgs e)
    {
        Result = StatusCombo.SelectedItem as string;
        CloseWith(true);
    }

    /// <summary>Hủy: trả về null → người gọi không ghi gì.</summary>
    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Result = null;
        CloseWith(false);
    }

    private void CloseWith(bool result)
    {
        try { DialogResult = result; }
        catch (InvalidOperationException) { Close(); }
    }
}
