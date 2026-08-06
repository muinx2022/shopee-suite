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

    // ── Bộ 3 giá trị ĐIỀN FORM của workflow Update product (trước đây là 3 hằng cứng trong
    //    BigSellerProductUpdateRunner: StockValue="30069", WeightValue="500", kênh vận chuyển "Nhanh").
    //    Đây là cấu hình DÙNG CHUNG toàn fleet theo TỪNG SHOP, CHỦ SỞ HỮU = HUB:
    //      • nằm trong BackupService.SharedSignature + được chép trong MergeShopsKeepInstance (Hub → client);
    //      • CỐ Ý không nhận từ client (xem FileStoreConfigService.UpdateSharedShopFields / FreshShopFromClient,
    //        cùng lý do như DataSource): client KHÔNG có UI cho 3 field này nên bản push của client cũ mang giá
    //        trị RỖNG — nếu hub nhận thì mỗi lượt upsert sẽ XOÁ TRẮNG cấu hình admin vừa đặt.
    //    RỖNG = dùng mặc định (3 hằng Default* dưới đây) → không đặt gì thì hành vi y như trước.
    /// <summary>Tồn kho điền cho MỌI biến thể khi Update product. Rỗng = <see cref="DefaultUpdateStock"/>.</summary>
    public string UpdateStockValue { get; set; } = "";
    /// <summary>Cân nặng (gram) điền khi Update product. Rỗng = <see cref="DefaultUpdateWeight"/>.</summary>
    public string UpdateWeightValue { get; set; } = "";
    /// <summary>Tên kênh vận chuyển cần tick khi Update product. Rỗng = <see cref="DefaultUpdateShippingChannel"/>.</summary>
    public string UpdateShippingChannel { get; set; } = "";

    /// <summary>Mặc định tồn kho (giá trị hằng cũ trong runner).</summary>
    public const string DefaultUpdateStock = "30069";
    /// <summary>Mặc định cân nặng, gram (giá trị hằng cũ trong runner).</summary>
    public const string DefaultUpdateWeight = "500";
    /// <summary>Mặc định kênh vận chuyển (giá trị hằng cũ trong runner).</summary>
    public const string DefaultUpdateShippingChannel = "Nhanh";

    /// <summary>Giá trị thực dùng cho 1 trong 3 field trên: trắng/rỗng → mặc định.</summary>
    public static string OrDefault(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    // Cấu hình AI (dùng khi rewrite tên sản phẩm ở module Update Product).
    public string OpenAiModel { get; set; } = "gpt-4.1-mini";
    public string OpenAiApiKeyFile { get; set; } = "";
    public int OpenAiBatchSize { get; set; } = 40;

    // LEGACY — đã gộp về BigSellerAccount.RunConfig (mức account); chỉ còn đọc 1 lần khi migrate
    // (RunConfigMigration). Đừng dùng cho luồng chạy. Giữ lại để deserialize file cũ + migration đọc.
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
