using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shopee.Core.Accounts;
using Shopee.Core.Ai;
using Shopee.Core.Coordination;
using Shopee.Core.Infrastructure;
using Shopee.Modules.Search;
using Shopee.Suite.Infrastructure;
using Shopee.Suite.Services;
using ShopeeStatApp.Models;
using ShopeeStatApp.Services;

namespace Shopee.Suite.Modules.Search;

/// <summary>
/// Module "Shopee Search" — tìm theo FILE link CATEGORY. Nạp file (.txt mỗi dòng 1 link, hoặc .xlsx),
/// tick chọn link → chạy MỖI LINK 1 tab/lane/account: mở category → lặp mọi sub-category → lọc Nơi Bán
/// khớp khu vực + Bán chạy → cào sp. Bộ lọc (giá / đã bán / danh mục) áp lúc hiển thị + xuất Excel.
/// </summary>
public sealed partial class SearchViewModel : ModuleViewModelBase
{
    private readonly List<ShopeeAccount> _pool = [];
    private readonly HashSet<string> _usedAccounts = new(StringComparer.Ordinal);
    [ObservableProperty] private int _poolCount;

    // Danh sách link đã nạp (tick chọn để search) + tab cho TỪNG link đang chạy.
    public ObservableCollection<SearchFileLinkRow> LoadedLinks { get; } = [];
    public ObservableCollection<SearchFileTab> LinkTabs { get; } = [];
    [ObservableProperty] private SearchFileTab? _selectedLinkTab;

    public ObservableCollection<ErroredAccountRow> ErroredAccounts { get; } = [];
    /// <summary>Mục "không lọc" của ComboBox Danh mục (tab Xuất Excel).</summary>
    private const string TatCaDanhMuc = "(Tất cả)";
    public ObservableCollection<string> Categories { get; } = [TatCaDanhMuc];

    // Tab "Danh mục (AI)"
    public ObservableCollection<SearchTaskStore.CategoryRow> CategoryRows { get; } = [];
    public ObservableCollection<SearchProductRow> CategoryProducts { get; } = [];

    private readonly List<SearchProductRow> _all = [];
    /// <summary>Link SP đã tính vào "Tổng phiên" (<see cref="_all"/>) — CHỈ để đếm không trùng. Khử trùng
    /// HIỂN THỊ nằm ở từng <see cref="SearchFileTab"/>, xem <see cref="SearchFileTab.AddProduct"/>.</summary>
    private readonly HashSet<string> _seenLinks = new(StringComparer.Ordinal);

    // Lọc theo KHU VỰC để tick "Nơi Bán" trong trình duyệt (vd "Hà Nội"). Trống = không lọc nơi bán.
    [ObservableProperty] private string _region = "Hà Nội";

    private readonly List<string> _filePaths = [];
    [ObservableProperty] private string _filesDisplay = "(chưa chọn file)";

    [ObservableProperty] private int _laneCount = 3;
    [ObservableProperty] private string _outputDir = Path.Combine(SuitePaths.ModuleDir("search"), "output");

    // Bộ lọc hiển thị + export
    [ObservableProperty] private long _minPrice;
    [ObservableProperty] private int _minSoldFrom;
    [ObservableProperty] private int _minSoldTo;
    [ObservableProperty] private string _categoryFilter = TatCaDanhMuc;

    // Tab Danh mục (AI)
    [ObservableProperty] private string _categoryDocxPath = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AiIdle))]
    [NotifyCanExecuteChangedFor(nameof(UpdateCategoriesAiCommand), nameof(UpdateCategoriesExcelCommand), nameof(StopAiCommand))]
    private bool _aiBusy;
    public bool AiIdle => !AiBusy;
    [ObservableProperty] private string _aiProgress = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedCategoryName))]
    private SearchTaskStore.CategoryRow? _selectedCategoryRow;
    public string SelectedCategoryName => SelectedCategoryRow?.Name ?? "";
    public string AiProviderName => AiConfigStore.Shared.Current.Provider;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(ResumeCommand), nameof(StopCommand), nameof(CloseLinkTabCommand))]
    private bool _isRunning;
    public bool IsIdle => !IsRunning;

    private CancellationTokenSource? _cts;
    private SearchRunner? _runner;
    private CancellationTokenSource? _aiCts;
    /// <summary>Id việc Hub đang chạy (op "search") — để AssignmentWorker biết lượt chạy này thuộc việc nào.
    /// null = đang chạy tay (không phải việc Hub giao).</summary>
    private string? _assignmentId;
    /// <summary>ItemId các sản phẩm ĐÃ đẩy lên Hub trong lượt chạy này (chỉ đẩy phần MỚI mỗi chu kỳ → liên tục, không gửi lại).</summary>
    private readonly HashSet<long> _pushedItemIds = [];
    /// <summary>Kết quả TERMINAL của việc Search theo id (completed | stopped | failed) — AssignmentWorker đọc
    /// để báo Hub đúng (crash/dừng dở KHÔNG báo "done"). Search không ghi ledger nên cần kênh này.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _assignmentOutcomes = new(StringComparer.Ordinal);

    private SearchRunner? _db;
    private SearchRunner Db => _db ??= new SearchRunner();

    public SearchViewModel() : base("search.log", "Shopee Search")
    {
        LoadUiSettings();
        Reload();
        NapDanhMucLucKhoiDong();
        if (_filePaths.Count > 0) LoadLinks();
        AccountStore.Shared.Changed += () =>
        {
            if (IsRunning) return;
            UiThread.Post(Reload);
        };
    }

    partial void OnCategoryDocxPathChanged(string value) => SaveUiSettings();
    partial void OnOutputDirChanged(string value) => SaveUiSettings();
    partial void OnRegionChanged(string value) => SaveUiSettings();

    partial void OnSelectedCategoryRowChanged(SearchTaskStore.CategoryRow? value)
    {
        CategoryProducts.Clear();
        if (value is null) return;
        foreach (var p in Db.GetProductsByCategory(value.Name)) CategoryProducts.Add(p);
    }

    [RelayCommand]
    private void Reload()
    {
        _pool.Clear();
        _pool.AddRange(AccountStore.Shared.Accounts.Where(x => !x.Disabled));
        PoolCount = _pool.Count;
        Status = $"{PoolCount} tài khoản Shopee (tự xoay vòng).";
    }

    // ── Chạy ────────────────────────────────────────────────────────────────────
    /// <summary>Tìm kiếm = chạy MỚI (mỗi link tạo lượt mới từ danh mục đầu).</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task Run() => StartAsync(resume: false);

    /// <summary>Tiếp tục = resume các link đang dở ĐÚNG từ danh mục/trang đã dừng (giữ SP đã cào).</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task Resume() => StartAsync(resume: true);

    private async Task StartAsync(bool resume)
    {
        if (_pool.Count == 0) { Warn("Kho chưa có tài khoản Shopee (thêm ở mục Tài khoản & Proxy)."); return; }
        var selected = LoadedLinks.Where(l => l.IsSelected).ToList();
        if (selected.Count == 0) { Warn("Tick chọn ít nhất 1 link để search."); return; }

        // Khử link TRÙNG trước khi dựng gì hết: cùng một link nằm ở 2 file (hoặc 2 dòng) mà cùng được tick
        // thì thành 2 lane cào song song một link — ghi đè file Excel của nhau, đè bản ghi hủy-theo-link, và
        // chung một tab UI nên user không thấy gì bất thường. Số lane cũng đếm theo danh sách ĐÃ khử trùng.
        var chon = selected.Select(l => (Index: l.Row, l.Link, l.SourceFile)).ToList();
        var items = SearchRunner.DedupLinks(chon);
        if (items.Count < chon.Count) Log($"⚠ bỏ {chon.Count - items.Count} link trùng giữa các file.");

        var lanes = Math.Max(1, Math.Min(Math.Min(LaneCount, _pool.Count), items.Count));

        // Dựng tab cho từng link đã chọn (mỗi link 1 tab). Gộp với tab cũ theo link.
        foreach (var it in items)
        {
            var tab = LinkTabs.FirstOrDefault(t => t.Link == it.Link);
            if (tab is null)
            {
                tab = new SearchFileTab(it.Index, it.Link, it.SourceFile, FileRunCoordinator.CatLabel(it.Link));
                LinkTabs.Add(tab);
            }
            tab.Status = "chờ";
        }
        SelectedLinkTab ??= LinkTabs.FirstOrDefault();

        IsRunning = true;
        _cts = new CancellationTokenSource();
        _usedAccounts.Clear();
        try
        {
            var specs = _pool.Select(ShopeeAccountSpecFactory.ToSearchSpec).ToList();
            Log($"{(resume ? "⏯ Tiếp tục" : "▶ Search")} {items.Count} link · {specs.Count} account · {lanes} lane · khu vực \"{Region}\". Xuất: {OutputDir}\\categories");
            await RunCoreAsync(items, specs, lanes, Region, resume, _cts.Token);
            Status = _cts.IsCancellationRequested ? "Đã dừng (giữ phiên)." : "Hoàn tất.";
            Log($"── {Status} Tổng {_all.Count} sản phẩm trong phiên. ──");
        }
        catch (Exception ex)
        {
            if (_cts?.IsCancellationRequested == true) { Status = "Đã dừng."; Log("── Đã dừng. ──"); }
            else Warn("Lỗi tìm kiếm: " + ex.Message);
        }
        finally
        {
            ResetUsedAccounts();
            _cts?.Dispose();
            _cts = null;
            IsRunning = false;
        }
    }

    /// <summary>Lõi chạy dùng chung (chạy tay + việc Hub giao): dựng runner, chạy đúng <paramref name="items"/>
    /// với <paramref name="specs"/> đã lọc, rồi làm mới danh mục/tiến độ. KHÔNG đụng khoá/cờ chạy (caller lo).</summary>
    private async Task RunCoreAsync(
        IReadOnlyList<(int Index, string Link, string SourceFile)> items,
        IReadOnlyList<SearchAccountSpec> specs, int lanes, string region, bool resume, CancellationToken ct,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<SearchAccountSpec?>>? acquireReplacement = null)
    {
        _runner = new SearchRunner();
        WireRunner();
        await _runner.RunCategoryLinksAsync(specs, items, lanes, region, OutputDir, resume, ct, acquireReplacement);
        RefreshCategories();
        RefreshLinkProgress();
    }

    /// <summary>Sau 1 lượt chạy: nhả cờ OpenWithShopeeAccount của các tk đã đăng nhập (lần sau khỏi login lại).</summary>
    private void ResetUsedAccounts()
    {
        var changed = false;
        foreach (var id in _usedAccounts)
        {
            var acc = AccountStore.Shared.Accounts.FirstOrDefault(a => a.Id == id);
            if (acc is not null && acc.OpenWithShopeeAccount) { acc.OpenWithShopeeAccount = false; changed = true; }
        }
        if (changed) AccountStore.Shared.Save();
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop()
    {
        _cts?.Cancel();
        _runner?.Stop();
        Status = "Đang dừng…";
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void CloseLinkTab(SearchFileTab? tab)
    {
        if (tab is null) return;
        // KHÔNG gỡ link SP khỏi _seenLinks nữa: sổ khử trùng hiển thị nằm trong chính tab và chết theo tab
        // (mở lại tab là nạp đủ), còn _seenLinks chỉ để đếm "Tổng phiên" — gỡ ra là cùng một SP được đếm hai
        // lần khi cào lại.
        LinkTabs.Remove(tab);
        if (ReferenceEquals(SelectedLinkTab, tab)) SelectedLinkTab = LinkTabs.FirstOrDefault();
    }

    private void WireRunner()
    {
        SearchFileTab? Tab(string link) => LinkTabs.FirstOrDefault(t => t.Link == link);

        _runner!.LinkStatus += (link, m) => OnUi(() =>
        {
            var t = Tab(link); if (t is not null) t.Status = m;
            var row = LoadedLinks.FirstOrDefault(x => x.Link == link);
            if (row is not null) row.Progress = m;   // hiện hoạt động live ở cột Tiến độ
            LogLines.Add($"[{FileRunCoordinator.CatLabel(link)}] {m}");
        });
        _runner.LinkAssigned += (link, acc) => OnUi(() => { var t = Tab(link); if (t is not null) t.Account = acc; });
        _runner.LinkConnection += (link, c) => OnUi(() =>
        {
            var t = Tab(link); if (t is not null && !c && t.Status == "chờ") t.Status = "mất kết nối…";
        });
        _runner.LinkProduct += (link, p) => OnUi(() =>
        {
            // Hiển thị: khử trùng TRONG TỪNG TAB (tab tự giữ sổ) — SP nằm ở 2 link thì hiện ở CẢ 2 tab.
            Tab(link)?.AddProduct(p);
            // "Tổng phiên" (_all) vẫn khử trùng TOÀN CỤC: đây là số đếm, không phải danh sách hiển thị.
            if (IsNewProduct(p)) _all.Add(p);
        });
        _runner.LinkFinished += link => OnUi(() => { var t = Tab(link); if (t is not null && t.Status.StartsWith("Đang")) t.Status = "đã đóng"; });
        _runner.AccountLoggedIn += id => OnUi(() => _usedAccounts.Add(id));
        _runner.AccountErrored += (id, reason, captchaUrl) => OnUi(() =>
        {
            var label = _pool.FirstOrDefault(a => a.Id == id)?.DisplayName ?? id;
            AccountErrorReporter.Report(ErroredAccounts, id, label, reason, "Search", captchaUrl);
            LogLines.Add($"⚠ Tk lỗi: {label} — {reason} (engine tự đổi account khác)");
        });
    }

    // ── Xuất Excel ──────────────────────────────────────────────────────────────
    private SearchFilter? BuildFilter()
    {
        var cat = CategoryFilter == TatCaDanhMuc ? null : CategoryFilter;
        return new SearchFilter(MinPrice, MinSoldFrom, MinSoldTo, cat);
    }

    [RelayCommand]
    private void ExportAll()
    {
        var path = Db.ExportAllShops(OutputDir, BuildFilter());
        Status = path is null ? "Không có sản phẩm để xuất." : "Đã xuất: " + path;
        Log(Status);
    }

    [RelayCommand]
    private async Task BrowseOutputDirAsync()
    {
        var dir = await FilePicker.PickFolderAsync("Chọn thư mục lưu Excel");
        if (dir is not null) OutputDir = dir;
    }

    [RelayCommand]
    private void OpenOutput()
    {
        try { if (Directory.Exists(OutputDir)) ShellOpener.OpenFolder(OutputDir); }
        catch (Exception ex) { Warn("Không mở được thư mục: " + ex.Message); }
    }

    [RelayCommand]
    private async Task ClearDataAsync()
    {
        if (!await Dialogs.ConfirmAsync("Xóa toàn bộ sản phẩm đã quét trong CSDL? Không thể hoàn tác.", "Xóa dữ liệu",
                DialogIcon.Warning)) return;
        Db.ClearFileHistory(OutputDir, _filePaths);
        _all.Clear(); _seenLinks.Clear();
        foreach (var t in LinkTabs) t.ClearProducts();   // xoá cả sổ khử trùng của tab, không chỉ dòng hiển thị
        RefreshCategoryGrid();
        // ComboBox lọc Danh mục cũng phải theo: ClearFileHistory xoá cả bảng categories, để nguyên thì combo
        // còn treo danh mục của dữ liệu vừa bị xoá (chọn vào là xuất ra file rỗng).
        ReloadCategoryFilterFromDb();
        Status = "Đã xóa dữ liệu đã quét.";
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────
    /// <summary>SP chưa được TÍNH vào tổng phiên (không phải "chưa hiển thị" — mỗi tab tự khử trùng).</summary>
    private bool IsNewProduct(SearchProductRow p) => string.IsNullOrEmpty(p.Link) || _seenLinks.Add(p.Link);

    private void RefreshCategories()
    {
        foreach (var c in _all.Select(x => x.Category).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().OrderBy(c => c))
            if (!Categories.Contains(c)) Categories.Add(c);
    }

    /// <summary>
    /// Nạp ComboBox lọc Danh mục (tab Xuất Excel) TỪ CSDL. Nguồn phải là CSDL vì nút "Xuất tất cả" xuất từ CSDL
    /// (<c>Db.ExportAllShops</c> → <c>shop_products</c>), không phải từ dữ liệu phiên: trước đây
    /// <see cref="Categories"/> chỉ được <see cref="RefreshCategories"/> bơm từ <c>_all</c> (kết quả của lượt chạy
    /// hiện tại) nên mở app lên chưa chạy gì là combo trống trơn — không lọc nổi kho đã quét từ trước.
    /// <para>Bảng <c>categories</c> chính là từ điển danh mục của <c>shop_products</c> (upsert mỗi lần
    /// <c>SaveShopProducts</c>, xoá cùng lúc trong <c>ClearFileSearchHistory</c>) nên dùng thẳng làm nguồn.</para>
    /// Giữ lựa chọn đang có nếu danh mục đó còn trong CSDL, ngược lại về "(Tất cả)".
    /// </summary>
    private void ReloadCategoryFilterFromDb(IReadOnlyList<SearchTaskStore.CategoryRow>? rows = null)
    {
        var dangChon = CategoryFilter;
        Categories.Clear();
        Categories.Add(TatCaDanhMuc);
        try
        {
            foreach (var name in (rows ?? Db.GetCategoryRows())
                         .Select(c => c.Name)
                         .Where(n => !string.IsNullOrWhiteSpace(n))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase))
                Categories.Add(name);
        }
        catch (Exception ex)
        {
            // CSDL hỏng/khoá → combo vẫn dùng được với mỗi "(Tất cả)"; không để chết constructor VM.
            Log("Không nạp được danh mục từ CSDL: " + ex.Message);
        }

        CategoryFilter = Categories.Contains(dangChon) ? dangChon : TatCaDanhMuc;
    }

}
