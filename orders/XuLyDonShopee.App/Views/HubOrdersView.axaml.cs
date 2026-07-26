using Avalonia.Controls;

namespace XuLyDonShopee.App.Views;

/// <summary>
/// Màn "Đơn toàn hệ thống" (CHỈ ĐỌC): xem đơn của MỌI shop / MỌI máy đọc thẳng từ Hub. Toàn bộ hành vi nằm ở
/// <see cref="ViewModels.HubOrdersViewModel"/> — code-behind KHÔNG có thao tác nào (khác màn "Đơn hàng":
/// đơn ở đây thuộc máy khác nên không mở hộp thoại đổi trạng thái / in phiếu).
/// </summary>
public partial class HubOrdersView : UserControl
{
    public HubOrdersView()
    {
        InitializeComponent();
    }
}
