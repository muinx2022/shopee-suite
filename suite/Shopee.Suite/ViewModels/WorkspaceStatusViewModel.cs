using CommunityToolkit.Mvvm.ComponentModel;
using Shopee.Core.Accounts;
using Shopee.Core.BigSeller;
using Shopee.Core.Coordination;
using Shopee.Core.Proxy;
using Shopee.Suite.Services;

namespace Shopee.Suite.ViewModels;

/// <summary>
/// ViewModel đoạn "Workspace" của footer trạng thái cấp shell: 6 counter chỉ ĐỌC-HIỂN THỊ từ các kho dùng
/// chung (TK BigSeller · shop · acc Shopee · proxy · máy online · trình duyệt). Không thêm nghiệp vụ; chỉ
/// đếm. Sống suốt vòng đời app (dựng 1 lần trong <see cref="ShellViewModel"/> khi chế độ có Workspace) nên
/// đăng ký event Changed không rò. Marshal về UI thread qua <see cref="UiThread.Post"/>.
/// </summary>
public sealed partial class WorkspaceStatusViewModel : ObservableObject
{
    [ObservableProperty] private string _bigSellerText = "";
    [ObservableProperty] private string _shopText = "";
    [ObservableProperty] private string _shopeeAccountText = "";
    [ObservableProperty] private string _proxyText = "";
    [ObservableProperty] private string _machineText = "";
    [ObservableProperty] private string _browserText = "Trình duyệt: Brave";

    public WorkspaceStatusViewModel()
    {
        Refresh();

        // Các kho phát Changed khi thêm/xóa/lưu/đồng bộ về → làm mới số. Máy online: Hub có thể null (tắt đồng
        // bộ) → không có event, dựa vào catch-all refresh khi đổi tab (ShellViewModel.OnSelectedTabChanged).
        BigSellerStore.Shared.Changed += () => UiThread.Post(Refresh);
        AccountStore.Shared.Changed += () => UiThread.Post(Refresh);
        KiotProxyPoolStore.Shared.Changed += () => UiThread.Post(Refresh);
        if (CoordinationRuntime.Hub is { } hub) hub.Changed += () => UiThread.Post(Refresh);
    }

    /// <summary>Đọc lại 6 số liệu từ các kho dùng chung. Best-effort: kho lỗi → giữ giá trị cũ, KHÔNG ném.</summary>
    public void Refresh()
    {
        try
        {
            var bigSellers = BigSellerStore.Shared.Accounts;
            var shopCount = bigSellers.Sum(a => a.Shops.Count);
            // "Máy online" = máy có heartbeat < 180s (khớp ngưỡng offline của FleetViewModel.MachineOffline) — KHÔNG
            // đếm máy vừa tắt còn trong snapshot. Hub null (tắt đồng bộ đa máy) → 0.
            var fleet = CoordinationRuntime.Hub?.CurrentFleet;
            var machineCount = fleet is null ? 0
                : fleet.Machines.Count(m => (System.DateTimeOffset.Now - m.LastSeen).TotalSeconds < 180);

            BigSellerText = $"{bigSellers.Count} tài khoản BigSeller";
            ShopText = $"{shopCount} shop";
            ShopeeAccountText = $"{AccountStore.Shared.Accounts.Count} acc Shopee";
            ProxyText = $"{KiotProxyPoolStore.Shared.Count} proxy";
            MachineText = $"{machineCount} máy online";
            // BrowserText cố định "Trình duyệt: Brave" (suite chỉ dùng Brave) — không cần tính lại.
        }
        catch { }
    }
}
