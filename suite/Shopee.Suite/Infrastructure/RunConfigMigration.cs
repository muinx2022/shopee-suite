using Shopee.Core.BigSeller;
using Shopee.Core.Scrape;

namespace Shopee.Suite.Infrastructure;

/// <summary>
/// Migration 1 LẦN: gộp cấu hình chạy (trước đây rải ở scrape-store theo account + field theo shop) về
/// <see cref="BigSellerAccount.RunConfig"/> mức account. Chạy khi VM đầu tiên chạm tới acc (ctor của
/// ScrapeTargetViewModel / UpdateRunTargetViewModel). Acc đã có RunConfig → no-op.
/// </summary>
public static class RunConfigMigration
{
    /// <summary>Đảm bảo acc có <see cref="BigSellerAccount.RunConfig"/>. Đã có → trả nguyên; chưa có → seed
    /// từ nguồn cũ (scrape-store theo account + shop đang chọn / shop đầu) rồi gán + lưu store.</summary>
    public static BigSellerRunConfig EnsureRunConfig(BigSellerAccount a)
    {
        if (a.RunConfig is not null) return a.RunConfig;

        var cfg = new BigSellerRunConfig();   // seed = default của class (không nguồn nào → giữ default)

        // Nguồn 1: cấu hình SCRAPE cũ (theo account, file scrape-targets.json) — nếu có.
        var scrape = ScrapeTargetConfigStore.Shared.Find(a.Id);
        if (scrape is not null)
        {
            cfg.StartRow = scrape.StartRow;
            cfg.EndRow = scrape.EndRow;
            cfg.RowsPerAccount = scrape.RowsPerAccount;
            cfg.Processes = scrape.MaxProcess;
            cfg.FrameSize = scrape.FrameSize > 0 ? scrape.FrameSize : cfg.FrameSize;
        }

        // Nguồn 2: cấu hình UPDATE cũ (theo shop) — shop đang chọn, fallback shop đầu.
        var shop = a.Shops.FirstOrDefault(s => s.Id == a.UpdateSelectedShopId) ?? a.Shops.FirstOrDefault();
        if (shop is not null)
        {
            cfg.ReloadSeconds = shop.ListingReloadSeconds;
            // Số process = lane LỚN NHẤT giữa 2 op cũ (scrape MaxProcess đã seed vs update worker của shop).
            cfg.Processes = Math.Max(cfg.Processes, shop.UpdateWorkers);
            // Khoảng dòng: scrape-store là nguồn chính; thiếu store thì lấy của shop.
            if (scrape is null) { cfg.StartRow = shop.StartRow; cfg.EndRow = shop.EndRow; }
        }

        a.RunConfig = cfg;
        BigSellerStore.Shared.Save();
        return cfg;
    }
}
