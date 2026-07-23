using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shopee.Core.Infrastructure;
using Shopee.Suite.Services;
using OrdersSettingsViewModel = XuLyDonShopee.App.ViewModels.SettingsViewModel;

namespace Shopee.Suite.Modules.Settings;

/// <summary>Một lựa chọn "Chế độ ứng dụng" cho ComboBox: giá trị <see cref="AppMode"/> + nhãn tiếng Việt
/// (ComboBox hiển thị nhãn qua <see cref="ToString"/>).</summary>
public sealed record AppModeOption(AppMode Mode, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// ViewModel cho màn Cài đặt GỘP: giữ 2 VM cài đặt con (suite + đơn hàng) để một màn chia section hiển thị
/// cả hai, CỘNG selector "Chế độ ứng dụng" (LUÔN hiện ở mọi chế độ; đổi = lưu + khởi động lại app). Không
/// thêm logic nghiệp vụ khác — mỗi section vẫn dùng đúng VM/command sẵn có. Section "Đơn hàng" ẩn khi module
/// đơn hàng không khởi tạo được (<see cref="Orders"/> null).
/// </summary>
public sealed partial class UnifiedSettingsViewModel : ObservableObject
{
    public UnifiedSettingsViewModel(SettingsViewModel suite, OrdersSettingsViewModel? orders)
    {
        Suite = suite;
        Orders = orders;

        Modes = new[]
        {
            new AppModeOption(AppMode.Full, "Đầy đủ — Workspace + Cấu hình BigSeller + Shopee"),
            new AppModeOption(AppMode.Workspace, "Chỉ Workspace — Workspace + Cấu hình BigSeller"),
            new AppModeOption(AppMode.Shopee, "Chỉ Shopee — đơn hàng"),
        };
        var current = AppModeStore.Shared.Current;
        _selectedMode = Array.Find(Modes, o => o.Mode == current) ?? Modes[0];
    }

    /// <summary>Cài đặt Shopee Suite (hiệu năng · đồng bộ Hub · phiên bản/cập nhật).</summary>
    public SettingsViewModel Suite { get; }

    /// <summary>Cài đặt module Đơn hàng (trình duyệt · thư mục phiếu · chu kỳ · GSheet · webhook). null nếu module không chạy.</summary>
    public OrdersSettingsViewModel? Orders { get; }

    /// <summary>true khi có module đơn hàng → hiện section "Đơn hàng".</summary>
    public bool HasOrders => Orders is not null;

    /// <summary>Danh sách chế độ cho ComboBox (giá trị enum + nhãn tiếng Việt).</summary>
    public AppModeOption[] Modes { get; }

    /// <summary>Chế độ đang chọn trong ComboBox (CHƯA lưu tới khi bấm "Lưu &amp; khởi động lại").</summary>
    [ObservableProperty] private AppModeOption _selectedMode;

    /// <summary>Lưu chế độ đã chọn rồi khởi động lại app để áp dụng (đổi chế độ = restart, không hot-swap).
    /// Không đổi so với hiện tại → chỉ báo, không restart. Cập nhật vẫn tải bản đầy đủ (không gate theo chế độ).</summary>
    [RelayCommand]
    private async Task SaveModeAndRestart()
    {
        var target = SelectedMode.Mode;
        if (target == AppModeStore.Shared.Current)
        {
            await Dialogs.InfoAsync("Chế độ không đổi.", "Chế độ ứng dụng");
            return;
        }

        var ok = await Dialogs.ConfirmAsync(
            $"Đổi sang chế độ \"{SelectedMode.Label}\"?\n\nApp sẽ khởi động lại để áp dụng. Cập nhật vẫn tải bản đầy đủ.",
            "Đổi chế độ ứng dụng", DialogIcon.Question);
        if (!ok) return;

        AppModeStore.Shared.Save(target);
        AppRestart.Restart();
    }
}
