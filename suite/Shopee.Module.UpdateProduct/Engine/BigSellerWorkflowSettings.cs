namespace UpdateProduct;

// record để clone per-lane bằng 'with' (orchestrator song song set ProfileDir/DebugPort riêng mỗi worker).
internal sealed record BigSellerWorkflowSettings
{
    public string BravePath { get; init; } = "";
    public string ProfileDir { get; init; } = "";
    public int DebugPort { get; init; }
    // Cho auto-login tự mint token (Phase 4) — mỗi máy tự đăng nhập bằng credential này.
    public string AccountId { get; init; } = "";
    public string Email { get; init; } = "";
    public string Password { get; init; } = "";
    public string ShopName { get; init; } = "";
    public string WorkbookPath { get; init; } = "";
    /// <summary>Tk này lấy dữ liệu sản phẩm từ kho Hub (Postgres) thay vì workbook Excel local. Bật →
    /// các runner (update/import/rewrite) đọc/ghi dòng qua HubClient (<see cref="AccountId"/> khoá kho),
    /// KHÔNG mở <see cref="WorkbookPath"/>. Mọi nhánh hub-mode phải nằm SAU cờ này để acc excel giữ nguyên.</summary>
    public bool UseHubData { get; init; }
    public string DataSheet { get; init; } = "";
    public string BigSellerCookieFile { get; init; } = "";
    public string BatchId { get; init; } = "";
    public int StartRow { get; init; } = 2;
    public int EndRow { get; init; }
    public string ImagePath { get; init; } = "";
    public string VideoFolder { get; init; } = "";
    public string CrawlUrl { get; init; } = "";
    public bool ImportFromClaimedTab { get; init; }
    public int ImportMaxProcess { get; init; } = 1;
    public int UpdateMaxProcess { get; init; } = 1;
    public int ListingReloadSeconds { get; init; } = 20;
    public string OpenAiModel { get; init; } = "gpt-4.1-mini";
    public string OpenAiApiKeyFile { get; init; } = "";
    /// <summary>Key OpenAI truyền thẳng từ Cài đặt (ưu tiên hơn env/file) — tránh đẩy key vào biến môi trường.</summary>
    public string OpenAiApiKey { get; init; } = "";
    public int OpenAiBatchSize { get; init; } = 40;

    // ── Bộ 3 giá trị ĐIỀN FORM của workflow Update product (cấu hình per-shop do HUB đặt; xem
    //    BigSellerShop.UpdateStockValue/…). Ở đây LUÔN là giá trị ĐÃ hợp lệ hoá (rỗng đã được thay bằng hằng
    //    Default* ở BigSellerContextFactory) → runner dùng thẳng, không phải nhớ fallback. ──
    /// <summary>Tồn kho điền cho mọi biến thể.</summary>
    public string UpdateStockValue { get; init; } = Shopee.Core.BigSeller.BigSellerShop.DefaultUpdateStock;
    /// <summary>Cân nặng (gram) điền vào form.</summary>
    public string UpdateWeightValue { get; init; } = Shopee.Core.BigSeller.BigSellerShop.DefaultUpdateWeight;
    /// <summary>Tên kênh vận chuyển cần tick trên form.</summary>
    public string UpdateShippingChannel { get; init; } = Shopee.Core.BigSeller.BigSellerShop.DefaultUpdateShippingChannel;

    // Ánh xạ field ↔ cột Excel (1-based) cho sheet của shop; mặc định layout cũ A/C/D/E/F/G.
    public int LinkColumn { get; init; } = 1;
    public int PriceColumn { get; init; } = 3;
    public int SkuColumn { get; init; } = 4;
    public int ItemIdColumn { get; init; } = 5;
    public int ProductNameColumn { get; init; } = 6;
    public int RewrittenNameColumn { get; init; } = 7;
}
