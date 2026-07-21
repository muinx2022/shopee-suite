using OrdersSettingsViewModel = XuLyDonShopee.App.ViewModels.SettingsViewModel;

namespace Shopee.Suite.Modules.Settings;

/// <summary>
/// ViewModel MỎNG cho màn Cài đặt GỘP: chỉ giữ 2 VM cài đặt con (suite + đơn hàng) để một màn duy nhất chia
/// section hiển thị cả hai. Không thêm logic — mỗi section vẫn dùng đúng VM/command sẵn có của nó. Section
/// "Đơn hàng" ẩn khi module đơn hàng không khởi tạo được (<see cref="Orders"/> null).
/// </summary>
public sealed class UnifiedSettingsViewModel
{
    public UnifiedSettingsViewModel(SettingsViewModel suite, OrdersSettingsViewModel? orders)
    {
        Suite = suite;
        Orders = orders;
    }

    /// <summary>Cài đặt Shopee Suite (hiệu năng · đồng bộ Hub · phiên bản/cập nhật).</summary>
    public SettingsViewModel Suite { get; }

    /// <summary>Cài đặt module Đơn hàng (trình duyệt · thư mục phiếu · chu kỳ · GSheet · webhook). null nếu module không chạy.</summary>
    public OrdersSettingsViewModel? Orders { get; }

    /// <summary>true khi có module đơn hàng → hiện section "Đơn hàng".</summary>
    public bool HasOrders => Orders is not null;
}
