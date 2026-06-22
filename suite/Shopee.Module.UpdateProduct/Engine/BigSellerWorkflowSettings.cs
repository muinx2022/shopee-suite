namespace UpdateProduct;

// record để clone per-lane bằng 'with' (orchestrator song song set ProfileDir/DebugPort riêng mỗi worker).
internal sealed record BigSellerWorkflowSettings
{
    public string BravePath { get; init; } = "";
    public string ProfileDir { get; init; } = "";
    public int DebugPort { get; init; }
    public string ImportProfileDir { get; init; } = "";
    public int ImportDebugPort { get; init; }
    public string ShopName { get; init; } = "";
    public string WorkbookPath { get; init; } = "";
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

    // Ánh xạ field ↔ cột Excel (1-based) cho sheet của shop; mặc định layout cũ A/C/D/E/F/G.
    public int LinkColumn { get; init; } = 1;
    public int PriceColumn { get; init; } = 3;
    public int SkuColumn { get; init; } = 4;
    public int ItemIdColumn { get; init; } = 5;
    public int ProductNameColumn { get; init; } = 6;
    public int RewrittenNameColumn { get; init; } = 7;
}
