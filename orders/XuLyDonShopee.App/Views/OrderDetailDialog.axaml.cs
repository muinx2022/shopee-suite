using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using XuLyDonShopee.App.ViewModels;

namespace XuLyDonShopee.App.Views;

/// <summary>
/// Hộp thoại thông tin cơ bản của MỘT đơn (chỉ đọc) + cho đổi trạng thái. ComboBox trạng thái nhận
/// nguồn là danh sách trạng thái ĐÃ SYNC về (chuỗi tự do, không enum). Bấm "Lưu" → <c>Close</c> trả về
/// trạng thái đã chọn; "Hủy" → <c>Close(null)</c>. Người gọi (OrdersViewModel) tự quyết có ghi DB không.
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
        // Chọn sẵn trạng thái hiện tại nếu nó có trong danh sách đã sync (thường có, vì status của đơn này
        // cũng là một trạng thái đã sync). Không có → để trống cho người dùng tự chọn.
        StatusCombo.SelectedItem = statuses.Contains(row.Status) ? row.Status : null;
    }

    /// <summary>Lưu: trả về trạng thái đang chọn (null nếu chưa chọn gì).</summary>
    private void OnSave(object? sender, RoutedEventArgs e) => Close(StatusCombo.SelectedItem as string);

    /// <summary>Hủy: trả về null → người gọi không ghi gì.</summary>
    private void OnCancel(object? sender, RoutedEventArgs e) => Close((string?)null);
}
