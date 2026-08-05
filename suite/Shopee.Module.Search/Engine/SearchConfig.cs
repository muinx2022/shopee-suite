namespace ShopeeStatApp.Models;

public sealed class SearchConfig
{
    public string Keyword { get; set; } = "";
    public string RegionFilterText { get; set; } = "";
    public long MinPriceVnd { get; set; } = 100_000;
    public int MinMonthlySold { get; set; } = 50;
    public bool CheckVariantStock { get; set; } = true;
    public int ResumeCategoryIndex { get; set; } = 1;

    /// <summary>1-based page to resume at WITHIN the resumed category (account swap → continue
    /// where the failed attempt stopped instead of restarting that category at page 1).</summary>
    public int ResumePage { get; set; } = 1;

    /// <summary>
    /// Chế độ cào. Hiện CHỈ CÒN "categoryFromLink" (mở link category → lặp mọi sub-category → lọc Nơi Bán +
    /// Bán chạy → cào): phía C# chỉ dựng đúng giá trị này (<c>FileRunCoordinator</c>) và extension chỉ đấu
    /// <c>startCategoryFromLink</c> cho lệnh <c>start</c>. Mặc định cũ là "keyword" — trỏ vào flow KHÔNG còn
    /// tồn tại, nên đổi về đúng flow đang sống.
    /// </summary>
    public string Mode { get; set; } = "categoryFromLink";

    /// <summary>URL category để mở khi chạy (Mode "categoryFromLink").</summary>
    public string ProductLink { get; set; } = "";
}
