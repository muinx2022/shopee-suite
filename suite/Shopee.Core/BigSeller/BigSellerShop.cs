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

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Shop mới" : Name;
}
