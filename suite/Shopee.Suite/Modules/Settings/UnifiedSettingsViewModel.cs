using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shopee.Core.Infrastructure;
using Shopee.Suite.Services;
using OrdersSettingsViewModel = XuLyDonShopee.App.ViewModels.SettingsViewModel;

namespace Shopee.Suite.Modules.Settings;

/// <summary>Một lựa chọn "Chế độ ứng dụng" cho ComboBox: giá trị <see cref="AppMode"/> + nhãn tiếng Việt đầy
/// đủ (<paramref name="Label"/>, ComboBox hiển thị qua <see cref="ToString"/>) + nhãn ngắn
/// (<paramref name="ShortLabel"/>) dùng đặt tên shortcut.</summary>
public sealed record AppModeOption(AppMode Mode, string Label, string ShortLabel)
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
            new AppModeOption(AppMode.Full, "Đầy đủ — Workspace + Cấu hình BigSeller + Shopee", "Đầy đủ"),
            new AppModeOption(AppMode.Workspace, "Chỉ Workspace — Workspace + Cấu hình BigSeller", "Workspace"),
            new AppModeOption(AppMode.Shopee, "Chỉ Shopee — đơn hàng", "Shopee"),
        };
        var current = AppModeStore.Shared.Current;
        _selectedMode = Array.Find(Modes, o => o.Mode == current) ?? Modes[0];
        ShowsWorkspaceSettings = AppModeStore.ShowsWorkspace(current);
    }

    /// <summary>Cài đặt Shopee Suite (hiệu năng · đồng bộ Hub · phiên bản/cập nhật).</summary>
    public SettingsViewModel Suite { get; }

    /// <summary>Cài đặt module Đơn hàng (trình duyệt · thư mục phiếu · chu kỳ · GSheet · webhook). null nếu module không chạy.</summary>
    public OrdersSettingsViewModel? Orders { get; }

    /// <summary>true khi có module đơn hàng → hiện section "Đơn hàng".</summary>
    public bool HasOrders => Orders is not null;

    /// <summary>true khi chế độ hiện tại (Full|Workspace) hiện nhóm Workspace → gate section "WORKSPACE"
    /// (Hiệu năng · đồng bộ Hub) ở màn Cài đặt gộp. Đọc 1 lần trong ctor: chế độ không đổi giữa vòng đời
    /// (đổi chế độ = restart). Section "Phiên bản &amp; cập nhật" KHÔNG bị gate — dùng chung mọi chế độ.</summary>
    public bool ShowsWorkspaceSettings { get; }

    /// <summary>Danh sách chế độ cho ComboBox (giá trị enum + nhãn tiếng Việt).</summary>
    public AppModeOption[] Modes { get; }

    /// <summary>true khi chế độ bị KHOÁ bởi tham số <c>--mode</c> (chạy từ shortcut). View dùng để ẩn nút
    /// "Lưu &amp; khởi động lại" (restart giữ <c>--mode</c> nên đổi mode ở đây vô nghĩa) + hiện chú thích.
    /// Vẫn cho chọn chế độ khác trong ComboBox để TẠO shortcut cho chế độ đó.</summary>
    public bool ModeLockedByArg => AppModeStore.Shared.ModeLockedByArg;

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

    /// <summary>Tạo shortcut ngoài Desktop mở thẳng chế độ đang chọn qua <c>--mode</c> — để chạy song song với
    /// chế độ khác trên cùng bản cài, DÙNG CHUNG tài khoản/dữ liệu hiện có (không tách kho). Trỏ
    /// <see cref="Environment.ProcessPath"/> (ổn định qua auto-update Velopack). Chỉ Windows; nền khác báo lỗi.</summary>
    [RelayCommand]
    private async Task CreateShortcutForMode()
    {
        var mode = SelectedMode.Mode;
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            await Dialogs.InfoAsync("Không xác định được đường dẫn app để tạo shortcut.", "Tạo shortcut", DialogIcon.Error);
            return;
        }

        var args = $"--mode {mode}";
        var name = $"Shopee Suite ({SelectedMode.ShortLabel})";
        var (ok, message) = ShortcutCreator.CreateDesktopShortcut(name, exe, args, iconPath: null);
        await Dialogs.InfoAsync(
            ok ? $"Đã tạo shortcut:\n{message}" : message,
            "Tạo shortcut", ok ? DialogIcon.Info : DialogIcon.Error);
    }
}
