namespace Shopee.Core.BigSeller;

/// <summary>Một shop thuộc một tài khoản BigSeller. Mỗi shop ứng với 1 sheet trong workbook.</summary>
public sealed class BigSellerShop
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>Tên sheet trong workbook chứa dữ liệu/link của shop này.</summary>
    public string ShopeeDataSheet { get; set; } = "";

    /// <summary>Ánh xạ field BigSeller ↔ cột Excel cho sheet của shop này (thay map cứng trong code).</summary>
    public BigSellerColumnMap ColumnMap { get; set; } = new();

    // Cấu hình "Import to store" (BigSeller) — dùng ở module Update Product.
    /// <summary>URL trang crawl BigSeller; trống = mặc định.</summary>
    public string BigSellerCrawlUrl { get; set; } = "";
    /// <summary>Import từ tab "Claimed" thay vì danh sách crawl.</summary>
    public bool BigSellerImportFromClaimedTab { get; set; }

    // Cấu hình AI (dùng khi rewrite tên sản phẩm ở module Update Product).
    public string OpenAiModel { get; set; } = "gpt-4.1-mini";
    public string OpenAiApiKeyFile { get; set; } = "";
    public int OpenAiBatchSize { get; set; } = 40;

    // Cấu hình CHẠY (Update/Import) RIÊNG theo shop — LƯU để khôi phục sau khi mở lại app
    // (trước đây chỉ ở UI-state UpdateRunTargetViewModel nên khởi động lại là mất).
    /// <summary>Bắt đầu từ dòng nào của sheet (≥2 vì dòng 1 là header).</summary>
    public int StartRow { get; set; } = 2;
    /// <summary>Đến dòng (0 = hết).</summary>
    public int EndRow { get; set; }
    /// <summary>Số worker (lane) cho Import to store.</summary>
    public int ImportWorkers { get; set; } = 1;
    /// <summary>Số worker (lane) cho Update product.</summary>
    public int UpdateWorkers { get; set; } = 1;
    /// <summary>Reload trang listing mỗi N giây.</summary>
    public int ListingReloadSeconds { get; set; } = 20;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Shop mới" : Name;
}
