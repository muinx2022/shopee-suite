using Shopee.Core.Coordination;
using Shopee.Hub;

namespace Shopee.Hub.Web.Services;

/// <summary>
/// Bộ điều phối phía HUB (port của suite\Shopee.Suite\Infrastructure\HubDispatcher.cs — trước chỉ chạy trên
/// máy Hub WPF). Khi BẬT + chế độ tự-theo-vai-trò: mỗi 10s duyệt từng shop trong config/bigseller.json của
/// chính Hub và tạo "việc kế tiếp" theo dây chuyền scrape → import → update (đọc trạng thái từ ledger DB) để
/// client đúng vai trò nhận. Ràng buộc single-session + thứ tự pipeline được siết lần cuối ở ClaimNext.
/// Bật/tắt lưu trong bảng settings (dispatcher.enabled / dispatcher.auto) — đổi từ trang /fleet.
/// </summary>
public sealed class DispatcherService : BackgroundService
{
    private readonly HubDatabase _db;
    private readonly FileStoreConfigService _config;
    private readonly ILogger<DispatcherService> _log;

    public DispatcherService(HubDatabase db, FileStoreConfigService config, ILogger<DispatcherService> log)
    {
        _db = db; _config = config; _log = log;
    }

    public bool Enabled
    {
        get => _db.GetSetting(SettingKeys.DispatcherEnabled) == "1";
        set => _db.SetSetting(SettingKeys.DispatcherEnabled, value ? "1" : "0");
    }

    /// <summary>true = tự theo vai trò (auto-enqueue cả pipeline); false = thủ công (chỉ giao tay). Mặc định true.</summary>
    public bool AutoMode
    {
        get => _db.GetSetting(SettingKeys.DispatcherAuto) != "0";
        set => _db.SetSetting(SettingKeys.DispatcherAuto, value ? "1" : "0");
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try { Tick(); }
            catch (Exception ex) { _log.LogWarning(ex, "dispatcher tick failed"); }
        }
    }

    private void Tick()
    {
        if (!Enabled || !AutoMode) return;
        var fleet = _db.Fleet();
        foreach (var acct in _config.BigSellerAccounts())
        foreach (var shop in acct.Shops)
        {
            if (string.IsNullOrWhiteSpace(shop.ShopeeDataSheet)) continue;   // chỉ shop đã gán sheet
            var op = NextOp(fleet, acct.Id, shop.Id);
            if (op is null) continue;   // shop đã xong cả pipeline
            _db.CreateAssignment(new CreateAssignmentRequest(acct.Id, shop.Id, shop.ShopeeDataSheet, op, null, false));
        }
    }

    /// <summary>Op kế tiếp của 1 shop theo dây chuyền (scrape→import→update). null nếu đã xong hết.
    /// Port verbatim từ HubDispatcher.NextOp.</summary>
    public static string? NextOp(FleetSnapshot fleet, string bsId, string shopId)
    {
        string St(string op) => fleet.Ledger.FirstOrDefault(l => l.Key == $"{bsId}__{shopId}__{op}")?.Status ?? "";
        if (St("scrape") != "completed") return "scrape";
        if (St("import") != "completed") return "import";
        if (St("update") != "completed") return "update";
        return null;
    }
}
