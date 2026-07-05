using System.Collections.Concurrent;
using Shopee.Core.Ai;
using ShopeeStatApp.Models;
using ShopeeStatApp.Services;

namespace Shopee.Modules.Search;

/// <summary>Tài khoản Shopee từ kho chung, để engine stat mở Brave tìm kiếm.</summary>
public sealed record SearchAccountSpec(
    string Id, string Label, string ShopeeAccountLogin, bool OpenWithShopeeAccount,
    string KiotProxyKey, string ProxyType, string ManualProxy, string ProfileRelativePath,
    bool RequireProxy = true);

/// <summary>Một sản phẩm tìm thấy (chiếu từ ProductResult của engine để Suite không phụ thuộc model engine).</summary>
public sealed record SearchProductRow(
    int Lane, string Name, decimal Price, int MonthlySold, double Rating,
    string Category, string ShopLocation, string ShopName, string Link, long ShopId);

/// <summary>Bộ lọc áp lúc hiển thị/xuất Excel (engine crawl hết, không lọc khi crawl).</summary>
public sealed record SearchFilter(long MinPrice, int MinSoldFrom, int MinSoldTo, string? Category);

/// <summary>
/// Facade công khai bọc engine tìm kiếm stat — chỉ tìm theo FILE link category (FileRunCoordinator,
/// Mode "categoryFromLink"). Chạy nhiều lane song song trên kho account dùng chung. Tích lũy sản phẩm
/// để xuất Excel có lọc.
/// </summary>
public sealed class SearchRunner
{
    private readonly AppSettingsService _settings = new();
    private readonly SearchTaskStore _store = new();
    private readonly ConcurrentBag<ProductResult> _collected = new();
    private FileRunCoordinator? _file;

    public event Action<string, string>? AccountErrored;
    /// <summary>accountId — tk vừa được dùng (đã đăng nhập/chạy) để ghi lại "không cần login lần sau".</summary>
    public event Action<string>? AccountUsed;
    /// <summary>accountId — login Shopee THÀNH CÔNG (đánh dấu "lần sau khỏi login" đúng tk có phiên).</summary>
    public event Action<string>? AccountLoggedIn;

    // ── Sự kiện THEO LINK (mỗi link category 1 tab) ──
    public event Action<string, string>? LinkStatus;            // link, status/log
    public event Action<string, SearchProductRow>? LinkProduct; // link, product
    public event Action<string, string>? LinkAssigned;          // link, accountName
    public event Action<string, bool>? LinkConnection;          // link, connected
    public event Action<string>? LinkFinished;                  // link

    // ── Chế độ THEO LINK CATEGORY (mỗi link 1 tab/lane/account) ─────────────────
    public Task RunCategoryLinksAsync(
        IReadOnlyList<SearchAccountSpec> specs,
        IReadOnlyList<(int Index, string Link, string SourceFile)> links,
        int laneCount, string region, string outputDir, bool resume, CancellationToken ct,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<SearchAccountSpec?>>? acquireReplacement = null)
    {
        var accounts = ToAccounts(specs);
        var items = links.Select(l => new FileRunCoordinator.LinkItem(l.Index, l.Link, l.SourceFile)).ToList();
        // BÙ TK THAY THẾ: gói provider (trả SearchAccountSpec) thành provider trả InstanceConfig cho engine.
        Func<IReadOnlyCollection<string>, CancellationToken, Task<InstanceConfig?>>? acquireInstance = acquireReplacement is null
            ? null
            : async (excludeIds, c) => { var s = await acquireReplacement(excludeIds, c).ConfigureAwait(false); return s is null ? null : ToAccount(s); };
        _file = new FileRunCoordinator(_settings, _store, accounts, items, laneCount, region, resume, acquireInstance);
        _file.LinkStatus += (link, m) => LinkStatus?.Invoke(link, m);
        _file.LinkProduct += (link, p) => { _collected.Add(p); LinkProduct?.Invoke(link, Project(0, p)); };
        _file.LinkAssigned += (link, acc, _) => LinkAssigned?.Invoke(link, acc);
        _file.LinkConnection += (link, c) => LinkConnection?.Invoke(link, c);
        _file.LinkFinished += link => LinkFinished?.Invoke(link);
        _file.AccountErrored += (id, reason) => AccountErrored?.Invoke(id, reason);
        _file.AccountUsed += id => AccountUsed?.Invoke(id);
        _file.AccountLoggedIn += id => AccountLoggedIn?.Invoke(id);
        _file.SaveLinkExcel = (label, products) =>
            ExportSafe(Path.Combine(outputDir, "categories"), products, SafeName(label));
        return _file.RunAsync(ct);
    }

    public void Stop()
    {
        try { _file?.KillAllBrowsers(); } catch { }
    }

    /// <summary>Toàn bộ sản phẩm đã cào trong lượt chạy hiện tại (đủ field ProductResult) — để đẩy gộp lên Hub.</summary>
    public IReadOnlyList<ProductResult> CollectedProducts() => _collected.ToList();

    // ── XUẤT GỘP TỪ CSDL (toàn bộ đã quét, không chỉ lần chạy hiện tại) ──────────────
    /// <summary>Xuất GỘP mọi sản phẩm của tất cả shop đã quét (theo file), loại trùng theo ItemId.</summary>
    public string? ExportAllShops(string outputDir, SearchFilter? filter) =>
        ExportDedup(_store.GetAllShopProducts(), outputDir, filter, "tonghop-file");

    private static string? ExportDedup(List<ProductResult> all, string outputDir, SearchFilter? filter, string prefix)
    {
        var items = all.GroupBy(p => p.ItemId).Select(g => g.First()).ToList();
        if (filter is not null) items = items.Where(p => Pass(p, filter)).ToList();
        if (items.Count == 0) return null;
        return ExcelExporter.Export(items, outputDir, $"{prefix}-{items.Count}sp");
    }

    // ── XÓA DỮ LIỆU (CSDL + store phụ) ─────────────────────────────────────────────
    public void ClearFileHistory(string outputDir, IEnumerable<string> filePaths)
    {
        _store.ClearFileSearchHistory();
        foreach (var f in filePaths)
            try { new LinkFileStore(f).ClearAllStatuses(); } catch { }
        _collected.Clear();
    }

    // ── DANH MỤC (cho lưới tab Danh mục) ───────────────────────────────────────────
    public IReadOnlyList<SearchTaskStore.CategoryRow> GetCategoryRows() => _store.GetCategories();

    public IReadOnlyList<SearchProductRow> GetProductsByCategory(string category) =>
        _store.GetShopProductsByCategory(category).Select(p => Project(0, p)).ToList();

    /// <summary>Tiến độ lượt chạy gần nhất của 1 link: (trạng thái, danh mục, trang, danh mục #, số SP). Null nếu chưa chạy.</summary>
    public (string Status, string Category, int Page, int CategoryIndex, int ProductCount)? GetLinkProgress(string link)
    {
        var p = _store.GetLinkProgress(link);
        return p is null ? null : (p.Status, p.Category, p.Page, p.CategoryIndex, p.ProductCount);
    }

    /// <summary>Đọc danh sách link của 1 file (.xlsx) để dựng lưới tiến trình (Dòng/Link/Trạng thái/ShopId).</summary>
    public IReadOnlyList<(int Row, string Link, string Status, long ShopId)> LoadFileLinks(string filePath)
    {
        try
        {
            return new LinkFileStore(filePath).Load()
                .Select(r => (r.RowNumber, r.Link, r.Status, FileRunCoordinator.ParseShopId(r.Link)))
                .ToList();
        }
        catch { return []; }
    }

    // ── CẬP NHẬT DANH MỤC BẰNG AI (dùng AiConfig DÙNG CHUNG) ────────────────────────
    /// <summary>Phân loại lại danh mục cho TOÀN BỘ sản phẩm trong CSDL bằng AI. Trả về số dòng cập nhật.</summary>
    public async Task<int> UpdateCategoriesDbAsync(AiConfig ai, string docxPath, Action<int, int>? progress, CancellationToken ct)
    {
        var paths = ShopeeCategoryReference.LoadLeafCategories(docxPath).Select(l => l.Path).ToList();
        if (paths.Count == 0) return 0;
        var rows = _store.GetAllShopProductsForCategory();   // (ShopId, ItemId, Name)
        if (rows.Count == 0) return 0;

        var updater = MakeUpdater(ai);
        var names = rows.Select(r => r.Name).ToList();
        var total = names.Count;
        var cats = await updater.ClassifyAllAsync(names, paths, Math.Clamp(ai.BatchSize, 1, 500), 2,
            d => progress?.Invoke(d, total), ct).ConfigureAwait(false);

        var updates = new List<(long, long, string)>();
        for (var i = 0; i < rows.Count; i++)
            if (!string.IsNullOrWhiteSpace(cats[i])) updates.Add((rows[i].ShopId, rows[i].ItemId, cats[i]));
        if (updates.Count > 0)
        {
            _store.SetShopProductCategories(updates);
            _store.UpsertCategories(updates.Select(u => u.Item3).Distinct());
            _store.PruneUnusedCategories();
        }
        return updates.Count;
    }

    /// <summary>Phân loại danh mục cho 1 file Excel (cột tên sp) bằng AI rồi ghi lại. Trả về số dòng ghi.</summary>
    public async Task<int> UpdateCategoriesExcelAsync(AiConfig ai, string docxPath, string xlsxPath, Action<int, int>? progress, CancellationToken ct)
    {
        var paths = ShopeeCategoryReference.LoadLeafCategories(docxPath).Select(l => l.Path).ToList();
        if (paths.Count == 0) return 0;
        using var file = new ExcelCategoryFile(xlsxPath);
        if (file.Names.Count == 0) return 0;

        var updater = MakeUpdater(ai);
        var total = file.Names.Count;
        var cats = await updater.ClassifyAllAsync(file.Names, paths, Math.Clamp(ai.BatchSize, 1, 500), 2,
            d => progress?.Invoke(d, total), ct).ConfigureAwait(false);
        return file.ApplyAndSave(cats);
    }

    private static CategoryAiUpdater MakeUpdater(AiConfig ai) =>
        new(CategoryAiUpdater.ParseProvider(ai.Provider), ai.ActiveApiKey, ai.ActiveModel);

    // ── Helpers ──────────────────────────────────────────────────────────────────
    private static List<InstanceConfig> ToAccounts(IReadOnlyList<SearchAccountSpec> specs) =>
        specs.Select(ToAccount).ToList();

    private static InstanceConfig ToAccount(SearchAccountSpec s) => new()
    {
        Id = s.Id,
        Label = s.Label,
        ShopeeAccountLogin = s.ShopeeAccountLogin,
        OpenWithShopeeAccount = s.OpenWithShopeeAccount,
        KiotProxyKey = s.KiotProxyKey,
        ProxyType = string.IsNullOrWhiteSpace(s.ProxyType) ? "http" : s.ProxyType,
        ManualProxy = s.ManualProxy,
        ProfileRelativePath = s.ProfileRelativePath,
        RequireProxy = s.RequireProxy,
    };

    /// <summary>Áp bộ lọc (giá / đã bán / danh mục) lên 1 danh sách sản phẩm bất kỳ. null = không lọc.
    /// Dùng lại cho xuất Excel gộp ở Hub (cùng logic với bộ lọc tab Search).</summary>
    public static IReadOnlyList<ProductResult> ApplyFilter(IReadOnlyList<ProductResult> items, SearchFilter? filter)
        => filter is null ? items : items.Where(p => Pass(p, filter)).ToList();

    private static bool Pass(ProductResult p, SearchFilter f) =>
        (f.MinPrice <= 0 || p.PriceVnd >= f.MinPrice)
        && (f.MinSoldFrom <= 0 || p.MonthlySold >= f.MinSoldFrom)
        && (f.MinSoldTo <= 0 || p.MonthlySold <= f.MinSoldTo)
        && (string.IsNullOrWhiteSpace(f.Category) || string.Equals(p.Category, f.Category, StringComparison.OrdinalIgnoreCase));

    private static Task ExportSafe(string outputDir, IReadOnlyList<ProductResult> products, string fileName)
    {
        try { if (!string.IsNullOrWhiteSpace(outputDir) && products.Count > 0) ExcelExporter.Export(products, outputDir, fileName); }
        catch { }
        return Task.CompletedTask;
    }

    private static SearchProductRow Project(int lane, ProductResult p) => new(
        lane, p.Name, p.PriceVnd, p.MonthlySold, p.Rating, p.Category, p.ShopLocation, p.ShopName,
        $"https://shopee.vn/product/{p.ShopId}/{p.ItemId}", p.ShopId);

    private static string SafeName(string kw)
    {
        var cleaned = string.Join("_", (kw ?? "kw").Split(Path.GetInvalidFileNameChars())).Trim();
        return string.IsNullOrEmpty(cleaned) ? "ketqua" : cleaned;
    }
}
