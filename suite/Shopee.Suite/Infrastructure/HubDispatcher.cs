using System.Windows.Threading;
using Shopee.Core.BigSeller;
using Shopee.Core.Coordination;

namespace Shopee.Suite.Infrastructure;

/// <summary>
/// Bộ điều phối phía HUB: khi BẬT + chế độ "tự theo vai trò", định kỳ duyệt từng shop và TỰ tạo "việc kế
/// tiếp" theo dây chuyền scrape → import → update (đọc trạng thái từ ledger) để client đúng vai trò nhận.
/// Chỉ chạy trên máy Hub. Ràng buộc single-session (1 op/1 tài khoản) + thứ tự pipeline được DB siết lại
/// lần cuối khi <c>ClaimNext</c>. Việc giao TAY (ghim máy) tạo trực tiếp từ bảng, không qua đây.
/// </summary>
public sealed class HubDispatcher
{
    public static HubDispatcher Shared { get; } = new();

    private readonly DispatcherTimer _timer;
    private bool _ticking;

    /// <summary>Bật/tắt việc Hub tự đẩy job ("▶ Bật điều phối" / "■ Dừng").</summary>
    public bool Enabled { get; set; }

    /// <summary>true = tự theo vai trò (auto-enqueue cả pipeline); false = thủ công (chỉ giao tay).</summary>
    public bool AutoMode { get; set; } = true;

    private HubDispatcher()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>Gọi 1 lần lúc app khởi động trên máy Hub.</summary>
    public void Start() { if (!_timer.IsEnabled) _timer.Start(); }

    private async void Tick()
    {
        if (_ticking) return;
        _ticking = true;
        try { await TickAsync(); } catch { } finally { _ticking = false; }
    }

    private async Task TickAsync()
    {
        if (!Enabled || !AutoMode) return;
        if (!HubServerConfigStore.Shared.Current.Enabled) return;   // chỉ máy Hub mới điều phối
        var hub = CoordinationRuntime.Hub;
        if (hub is null) return;

        var fleet = hub.CurrentFleet;
        foreach (var acct in BigSellerStore.Shared.Accounts)
        foreach (var shop in acct.Shops)
        {
            if (string.IsNullOrWhiteSpace(shop.ShopeeDataSheet)) continue;   // chỉ shop đã gán sheet
            var op = NextOp(fleet, acct.Id, shop.Id);
            if (op is null) continue;   // shop đã xong cả pipeline
            await hub.CreateAssignmentAsync(
                new CreateAssignmentRequest(acct.Id, shop.Id, shop.ShopeeDataSheet, op, null, false));
        }
    }

    /// <summary>Op kế tiếp của 1 shop theo dây chuyền (scrape→import→update). null nếu đã xong hết.</summary>
    public static string? NextOp(FleetSnapshot fleet, string bsId, string shopId)
    {
        string St(string op) => fleet.Ledger.FirstOrDefault(l => l.Key == $"{bsId}__{shopId}__{op}")?.Status ?? "";
        if (St("scrape") != "completed") return "scrape";
        if (St("import") != "completed") return "import";
        if (St("update") != "completed") return "update";
        return null;
    }
}
