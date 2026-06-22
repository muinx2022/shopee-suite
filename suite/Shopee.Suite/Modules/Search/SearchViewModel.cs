using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Shopee.Core.Accounts;
using Shopee.Core.Ai;
using Shopee.Core.Infrastructure;
using Shopee.Modules.Search;
using Shopee.Suite.Infrastructure;
using ShopeeStatApp.Services;

namespace Shopee.Suite.Modules.Search;

/// <summary>
/// Module "Shopee Search" — tìm theo FILE link CATEGORY. Nạp file (.txt mỗi dòng 1 link, hoặc .xlsx),
/// tick chọn link → chạy MỖI LINK 1 tab/lane/account: mở category → lặp mọi sub-category → lọc Nơi Bán
/// khớp khu vực + Bán chạy → cào sp. Bộ lọc (giá / đã bán / danh mục) áp lúc hiển thị + xuất Excel.
/// </summary>
public sealed partial class SearchViewModel : ObservableObject
{
    private readonly List<ShopeeAccount> _pool = [];
    private readonly HashSet<string> _usedAccounts = new(StringComparer.Ordinal);
    [ObservableProperty] private int _poolCount;

    // Danh sách link đã nạp (tick chọn để search) + tab cho TỪNG link đang chạy.
    public ObservableCollection<SearchFileLinkRow> LoadedLinks { get; } = [];
    public ObservableCollection<SearchFileTab> LinkTabs { get; } = [];
    [ObservableProperty] private SearchFileTab? _selectedLinkTab;

    public ObservableCollection<ErroredAccountRow> ErroredAccounts { get; } = [];
    public ObservableCollection<string> Categories { get; } = ["(Tất cả)"];
    public ObservableCollection<string> LogLines { get; } = [];

    // Tab "Danh mục (AI)"
    public ObservableCollection<SearchTaskStore.CategoryRow> CategoryRows { get; } = [];
    public ObservableCollection<SearchProductRow> CategoryProducts { get; } = [];

    private readonly List<SearchProductRow> _all = [];
    private readonly HashSet<string> _seenLinks = new(StringComparer.Ordinal);

    // Lọc theo KHU VỰC để tick "Nơi Bán" trong trình duyệt (vd "Hà Nội"). Trống = không lọc nơi bán.
    [ObservableProperty] private string _region = "Hà Nội";

    private readonly List<string> _filePaths = [];
    [ObservableProperty] private string _filesDisplay = "(chưa chọn file)";

    [ObservableProperty] private int _laneCount = 3;
    [ObservableProperty] private string _outputDir = Path.Combine(SuitePaths.ModuleDir("search"), "output");
    [ObservableProperty] private string _status = "Sẵn sàng.";

    // Bộ lọc hiển thị + export
    [ObservableProperty] private long _minPrice;
    [ObservableProperty] private int _minSoldFrom;
    [ObservableProperty] private int _minSoldTo;
    [ObservableProperty] private string _categoryFilter = "(Tất cả)";

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

    private SearchRunner? _db;
    private SearchRunner Db => _db ??= new SearchRunner();

    public SearchViewModel()
    {
        LoadUiSettings();
        Reload();
        RefreshCategoryGrid();
        if (_filePaths.Count > 0) LoadLinks();
        AccountStore.Shared.Changed += () =>
        {
            if (IsRunning) return;
            var d = Application.Current?.Dispatcher;
            if (d is null || d.CheckAccess()) Reload();
            else d.BeginInvoke(Reload);
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

    // ── Nạp file link + chọn link ───────────────────────────────────────────────
    [RelayCommand]
    private void ChooseFiles()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Text (mỗi dòng 1 link)|*.txt|Excel|*.xlsx;*.xlsm|Tất cả|*.*",
            Multiselect = true,
            Title = "Chọn file link category",
        };
        if (dlg.ShowDialog() != true) return;
        _filePaths.Clear();
        _filePaths.AddRange(dlg.FileNames);
        FilesDisplay = $"{_filePaths.Count} file: " + string.Join(", ", _filePaths.Select(Path.GetFileName));
        LoadLinks();
        SaveUiSettings();
    }

    [RelayCommand]
    private void ClearFiles()
    {
        _filePaths.Clear();
        LoadedLinks.Clear();
        FilesDisplay = "(chưa chọn file)";
        SaveUiSettings();
    }

    // Nạp link từ tất cả file đã chọn vào danh sách tick chọn (gộp lại).
    private void LoadLinks()
    {
        LoadedLinks.Clear();
        foreach (var f in _filePaths)
        {
            List<(int Row, string Link, string Status, long ShopId)> rows;
            try { rows = Db.LoadFileLinks(f).ToList(); }
            catch { rows = []; }
            foreach (var (row, link, status, _) in rows)
            {
                var item = new SearchFileLinkRow(row, link, f, status);
                item.Progress = FormatLinkProgress(Db.GetLinkProgress(link));
                LoadedLinks.Add(item);
            }
        }
        Status = $"Đã nạp {LoadedLinks.Count} link ({LoadedLinks.Count(x => x.IsSelected)} đang chọn).";
    }

    [RelayCommand]
    private void SelectAllLinks() { foreach (var l in LoadedLinks) l.IsSelected = true; }

    [RelayCommand]
    private void UnselectAllLinks() { foreach (var l in LoadedLinks) l.IsSelected = false; }

    /// <summary>Xóa các link đang tick khỏi danh sách đã nạp (không đụng file gốc; chọn lại file để nạp lại).</summary>
    [RelayCommand]
    private void RemoveSelectedLinks()
    {
        var sel = LoadedLinks.Where(l => l.IsSelected).ToList();
        if (sel.Count == 0) { Status = "Chưa tick link nào để xóa."; return; }
        foreach (var l in sel) LoadedLinks.Remove(l);
        Status = $"Đã xóa {sel.Count} link khỏi danh sách ({LoadedLinks.Count} còn lại).";
    }

    /// <summary>Định dạng tiến độ link để hiển thị ở cột "Tiến độ".</summary>
    private static string FormatLinkProgress((string Status, string Category, int Page, int CategoryIndex, int ProductCount)? p)
    {
        if (p is null) return "";
        var (st, cat, page, catIdx, count) = p.Value;
        var stVi = st switch
        {
            "Completed" => "✔ hoàn thành",
            "Running" => "▶ chưa kết thúc",
            "Failed" => "■ lỗi/dừng",
            "Stopped" => "■ dừng",
            _ => st,
        };
        var where = catIdx > 0
            ? $" · DM #{catIdx}" + (string.IsNullOrWhiteSpace(cat) ? "" : $" ({cat})") + $" · trang {page}"
            : "";
        return $"{stVi}{where} · {count} SP";
    }

    private void RefreshLinkProgress()
    {
        foreach (var l in LoadedLinks) l.Progress = FormatLinkProgress(Db.GetLinkProgress(l.Link));
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

        var lanes = Math.Max(1, Math.Min(Math.Min(LaneCount, _pool.Count), selected.Count));

        // Dựng tab cho từng link đã chọn (mỗi link 1 tab). Gộp với tab cũ theo link.
        var items = new List<(int Index, string Link, string SourceFile)>();
        foreach (var l in selected)
        {
            var tab = LinkTabs.FirstOrDefault(t => t.Link == l.Link);
            if (tab is null)
            {
                tab = new SearchFileTab(l.Row, l.Link, l.SourceFile, FileRunCoordinator.CatLabel(l.Link));
                LinkTabs.Add(tab);
            }
            tab.Status = "chờ";
            items.Add((l.Row, l.Link, l.SourceFile));
        }
        SelectedLinkTab ??= LinkTabs.FirstOrDefault();

        IsRunning = true;
        _cts = new CancellationTokenSource();
        _usedAccounts.Clear();
        try
        {
            _runner = new SearchRunner();
            WireRunner();
            var specs = _pool.Select(a => new SearchAccountSpec(
                a.Id, a.DisplayName, a.ShopeeAccountLogin, a.OpenWithShopeeAccount,
                a.KiotProxyKey, a.ProxyType, a.ManualProxy, a.ProfileRelativePath, a.RequireProxy)).ToList();

            Log($"{(resume ? "⏯ Tiếp tục" : "▶ Search")} {items.Count} link · {specs.Count} account · {lanes} lane · khu vực \"{Region}\". Xuất: {OutputDir}\\categories");
            await _runner.RunCategoryLinksAsync(specs, items, lanes, Region, OutputDir, resume, _cts.Token);
            Status = _cts.IsCancellationRequested ? "Đã dừng (giữ phiên)." : "Hoàn tất.";
            RefreshCategories();
            RefreshLinkProgress();
            Log($"── {Status} Tổng {_all.Count} sản phẩm trong phiên. ──");
        }
        catch (Exception ex)
        {
            if (_cts?.IsCancellationRequested == true) { Status = "Đã dừng."; Log("── Đã dừng. ──"); }
            else Warn("Lỗi tìm kiếm: " + ex.Message);
        }
        finally
        {
            var changed = false;
            foreach (var id in _usedAccounts)
            {
                var acc = AccountStore.Shared.Accounts.FirstOrDefault(a => a.Id == id);
                if (acc is not null && acc.OpenWithShopeeAccount) { acc.OpenWithShopeeAccount = false; changed = true; }
            }
            if (changed) AccountStore.Shared.Save();

            _cts?.Dispose();
            _cts = null;
            IsRunning = false;
        }
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
        foreach (var p in tab.Products) _seenLinks.Remove(p.Link);
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
            if (!IsNewProduct(p)) return;
            _all.Add(p);
            var t = Tab(link);
            if (t is not null) { t.Products.Add(p); t.ProductCount++; }
        });
        _runner.LinkFinished += link => OnUi(() => { var t = Tab(link); if (t is not null && t.Status.StartsWith("Đang")) t.Status = "đã đóng"; });
        _runner.AccountLoggedIn += id => OnUi(() => _usedAccounts.Add(id));
        _runner.AccountErrored += (id, reason) => OnUi(() =>
        {
            var label = _pool.FirstOrDefault(a => a.Id == id)?.DisplayName ?? id;
            var now = DateTime.Now.ToString("HH:mm:ss");
            var row = ErroredAccounts.FirstOrDefault(x => x.Id == id);
            if (row is null) ErroredAccounts.Insert(0, new ErroredAccountRow(id, label, reason, now));
            else { row.Reason = reason; row.Time = now; }
            LogLines.Add($"⚠ Tk lỗi: {label} — {reason} (engine tự đổi account khác)");
            FlagAccountErrored(id, $"Dính captcha/lỗi (Search) — {DateTime.Now:dd/MM HH:mm}: {reason}");
        });
    }

    private static void FlagAccountErrored(string id, string reason)
    {
        var acc = AccountStore.Shared.Accounts.FirstOrDefault(a => a.Id == id);
        if (acc is null || acc.Disabled) return;
        acc.Disabled = true;
        acc.LastError = reason;
        AccountStore.Shared.Save();
    }

    // ── Xuất Excel ──────────────────────────────────────────────────────────────
    private SearchFilter? BuildFilter()
    {
        var cat = CategoryFilter == "(Tất cả)" ? null : CategoryFilter;
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
    private void BrowseOutputDir()
    {
        var dlg = new OpenFolderDialog { Title = "Chọn thư mục lưu Excel" };
        if (dlg.ShowDialog() == true) OutputDir = dlg.FolderName;
    }

    [RelayCommand]
    private void OpenOutput()
    {
        try { if (Directory.Exists(OutputDir)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(OutputDir) { UseShellExecute = true }); }
        catch (Exception ex) { Warn("Không mở được thư mục: " + ex.Message); }
    }

    [RelayCommand]
    private void ClearData()
    {
        if (Dialogs.Show("Xóa toàn bộ sản phẩm đã quét trong CSDL? Không thể hoàn tác.", "Xóa dữ liệu",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        Db.ClearFileHistory(OutputDir, _filePaths);
        _all.Clear(); _seenLinks.Clear();
        foreach (var t in LinkTabs) { t.Products.Clear(); t.ProductCount = 0; }
        RefreshCategoryGrid();
        Status = "Đã xóa dữ liệu đã quét.";
    }

    // ── Tab Danh mục (AI) ───────────────────────────────────────────────────────
    [RelayCommand]
    private void BrowseDocx()
    {
        var dlg = new OpenFileDialog { Filter = "Word/Excel danh mục|*.docx;*.xlsx|Tất cả|*.*", Title = "Chọn file danh mục lá Shopee" };
        if (dlg.ShowDialog() == true) CategoryDocxPath = dlg.FileName;
    }

    [RelayCommand]
    private void RefreshCategoryGrid()
    {
        CategoryRows.Clear();
        foreach (var c in Db.GetCategoryRows()) CategoryRows.Add(c);
        Status = $"{CategoryRows.Count} danh mục trong CSDL.";
    }

    private bool CanRunAi() => AiIdle;

    [RelayCommand(CanExecute = nameof(CanRunAi))]
    private async Task UpdateCategoriesAiAsync()
    {
        if (!ValidateAi(out var ai)) return;
        AiBusy = true; _aiCts = new CancellationTokenSource();
        try
        {
            Log("🤖 Cập nhật danh mục (AI) cho TOÀN BỘ sản phẩm trong CSDL…");
            var n = await Db.UpdateCategoriesDbAsync(ai, CategoryDocxPath,
                (d, t) => OnUi(() => AiProgress = $"{d}/{t}"), _aiCts.Token);
            RefreshCategoryGrid();
            Status = $"Đã cập nhật danh mục {n} sản phẩm.";
            Log($"✔ AI: cập nhật danh mục {n} sản phẩm.");
        }
        catch (OperationCanceledException) { Status = "Đã dừng cập nhật danh mục."; }
        catch (Exception ex) { Warn("Lỗi cập nhật danh mục AI: " + ex.Message); }
        finally { AiBusy = false; AiProgress = ""; _aiCts?.Dispose(); _aiCts = null; }
    }

    [RelayCommand(CanExecute = nameof(CanRunAi))]
    private async Task UpdateCategoriesExcelAsync()
    {
        if (!ValidateAi(out var ai)) return;
        var dlg = new OpenFileDialog { Filter = "Excel|*.xlsx", Title = "Chọn file Excel (cột tên sản phẩm) để gán danh mục" };
        if (dlg.ShowDialog() != true) return;

        AiBusy = true; _aiCts = new CancellationTokenSource();
        try
        {
            Log("🤖 Cập nhật danh mục (AI) cho file: " + Path.GetFileName(dlg.FileName));
            var n = await Db.UpdateCategoriesExcelAsync(ai, CategoryDocxPath, dlg.FileName,
                (d, t) => OnUi(() => AiProgress = $"{d}/{t}"), _aiCts.Token);
            Status = $"Đã ghi danh mục cho {n} dòng trong file.";
            Log($"✔ AI: ghi danh mục {n} dòng → {dlg.FileName}");
        }
        catch (OperationCanceledException) { Status = "Đã dừng."; }
        catch (Exception ex) { Warn("Lỗi cập nhật danh mục Excel: " + ex.Message); }
        finally { AiBusy = false; AiProgress = ""; _aiCts?.Dispose(); _aiCts = null; }
    }

    [RelayCommand(CanExecute = nameof(AiBusy))]
    private void StopAi() => _aiCts?.Cancel();

    private bool ValidateAi(out AiConfig ai)
    {
        ai = AiConfigStore.Shared.Current;
        if (!ai.HasActiveKey) { Warn($"Chưa cấu hình API key cho {ai.Provider} (mục Cài đặt)."); return false; }
        if (string.IsNullOrWhiteSpace(CategoryDocxPath) || !File.Exists(CategoryDocxPath))
        { Warn("Chọn file danh mục lá Shopee (.docx) hợp lệ trước."); return false; }
        return true;
    }

    // ── Lưu/nạp cấu hình UI nhỏ ─────────────────────────────────────────────────
    private static string UiSettingsPath => Path.Combine(SuitePaths.ModuleDir("search"), "search-ui.json");
    private sealed record UiSettings(string Docx, string Output, string? Region = null, List<string>? FilePaths = null);
    private bool _loadingUi;

    private void LoadUiSettings()
    {
        _loadingUi = true;
        try
        {
            if (File.Exists(UiSettingsPath) &&
                JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(UiSettingsPath)) is { } j)
            {
                if (!string.IsNullOrWhiteSpace(j.Docx)) _categoryDocxPath = j.Docx;
                if (!string.IsNullOrWhiteSpace(j.Output)) _outputDir = j.Output;
                if (!string.IsNullOrWhiteSpace(j.Region)) _region = j.Region;
                if (j.FilePaths is { } fps)
                {
                    foreach (var f in fps) if (!string.IsNullOrWhiteSpace(f) && File.Exists(f)) _filePaths.Add(f);
                    if (_filePaths.Count > 0)
                        FilesDisplay = $"{_filePaths.Count} file: " + string.Join(", ", _filePaths.Select(Path.GetFileName));
                }
            }
        }
        catch { }
        finally { _loadingUi = false; }
    }

    private void SaveUiSettings()
    {
        if (_loadingUi) return;
        try
        {
            Directory.CreateDirectory(SuitePaths.ModuleDir("search"));
            File.WriteAllText(UiSettingsPath,
                JsonSerializer.Serialize(new UiSettings(CategoryDocxPath, OutputDir, Region, _filePaths.ToList())));
        }
        catch { }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────
    private bool IsNewProduct(SearchProductRow p) => string.IsNullOrEmpty(p.Link) || _seenLinks.Add(p.Link);

    private void RefreshCategories()
    {
        foreach (var c in _all.Select(x => x.Category).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().OrderBy(c => c))
            if (!Categories.Contains(c)) Categories.Add(c);
    }

    private void Log(string text) => OnUi(() => LogLines.Add(text));

    private void Warn(string msg)
    {
        Status = msg;
        Dialogs.Show(msg, "Shopee Search", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static void OnUi(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a();
        else d.BeginInvoke(a);
    }
}
