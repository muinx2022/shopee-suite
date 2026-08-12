using Shopee.Core.BigSeller;
using Shopee.Core.Coordination;
using Shopee.Core.Infrastructure;
using Shopee.Core.Scrape;

namespace Shopee.Suite.Modules.Scrape;

/// <summary>
/// XOÁ TIẾN ĐỘ scrape của một (tk BigSeller + sheet) — MỘT đường duy nhất cho cả nút "Xoá tiến độ" ở cửa sổ
/// Thống kê lẫn đường Reset (bấm Chạy) của <see cref="ScrapeViewModel"/>.
/// <para>Xoá local CHƯA ĐỦ: ledger Hub còn nguyên khoảng-dòng cũ → lượt Tiếp tục sau (hoặc mở lại app:
/// fold TOÀN BỘ ledger về tiến độ local) kéo tiến độ CŨ về → resume tưởng đã xong đúng phần user vừa muốn
/// cào lại → BỎ SÓT dòng ("fold-poisoning"). Nên phải đặt ledger op scrape của shop về
/// <see cref="LedgerStatus.Idle"/> (server xoá bản ghi ledger + tiến độ dòng).</para>
/// </summary>
internal static class ScrapeProgressReset
{
    /// <summary>Xoá tiến độ khi ĐÃ biết chính xác shop của lượt chạy (đường Reset trong ScrapeViewModel).</summary>
    public static void Reset(string accountId, string sheet, BigSellerShop? shop, string accountName, Action<string>? log = null)
    {
        ScrapeProgressStore.Shared.Clear(accountId, sheet);

        var hub = CoordinationRuntime.Hub;
        if (hub is null) return;   // Hub tắt (chạy 1 máy) → không có ledger nào để xoá

        if (shop is null)
        {
            log?.Invoke($"⚠ Đã xoá tiến độ trên máy này của sheet \"{sheet}\" nhưng KHÔNG tìm thấy shop khớp sheet " +
                        "→ ledger Hub giữ nguyên; lượt Tiếp tục sau có thể kéo lại tiến độ cũ.");
            return;
        }

        // Fire-and-forget có gắn tag: offline / lỗi mạng → thôi (local đã sạch là đủ để chạy lại từ đầu),
        // lỗi vẫn hiện ở tab Log Hub thay vì chìm hẳn.
        TaskExt.FireAndForget(
            hub.SetLedgerStatusAsync(new CoordKey(accountId, shop.Id, sheet, CoordOp.Scrape), LedgerStatus.Idle),
            $"xoá ledger hub (idle) khi xoá tiến độ · {accountName}/{sheet}");
    }

    /// <summary>Xoá tiến độ khi chỉ biết sheet (cửa sổ Thống kê): tự tra shop theo sheet trong kho BigSeller.
    /// UI không chặn nhiều shop cùng trỏ một sheet → phải đặt Idle ledger của TẤT CẢ shop khớp; sót một là
    /// ledger đó fold tiến độ cũ về lại ("fold-poisoning") đúng như trước khi vá.</summary>
    public static void Reset(string accountId, string sheet, Action<string>? log = null)
    {
        var account = BigSellerStore.Shared.Find(accountId);
        var shops = account?.Shops.Where(s =>
            string.Equals(s.ShopeeDataSheet ?? "", sheet ?? "", StringComparison.OrdinalIgnoreCase)).ToList() ?? [];
        if (shops.Count == 0)
        {
            Reset(accountId, sheet ?? "", (BigSellerShop?)null, account?.DisplayName ?? accountId, log);
            return;
        }
        foreach (var shop in shops)
            Reset(accountId, sheet ?? "", shop, account?.DisplayName ?? accountId, log);
    }
}
