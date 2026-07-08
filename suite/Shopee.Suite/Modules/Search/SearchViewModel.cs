using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shopee.Core.Accounts;
using Shopee.Core.Ai;
using Shopee.Core.Coordination;
using Shopee.Core.Infrastructure;
using Shopee.Core.Proxy;
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
    public ObservableCollection<string> Categories { get; } = ["(Tất cả)"];

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
        RefreshCategoryGrid();
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

    // ── Nạp file link + chọn link ───────────────────────────────────────────────
    [RelayCommand]
    private async Task ChooseFilesAsync()
    {
        var files = await FilePicker.OpenFilesAsync("Chọn file link category",
            "Text (mỗi dòng 1 link)|*.txt|Excel|*.xlsx;*.xlsm|Tất cả|*.*");
        if (files.Length == 0) return;
        _filePaths.Clear();
        _filePaths.AddRange(files);
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
            var specs = _pool.Select(ToSpec).ToList();
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

    /// <summary>Dựng spec cho engine; proxy lấy XOAY VÒNG từ kho KiotProxy dùng chung (ghi đè proxy gắn sẵn
    /// của acc). Kho rỗng → giữ proxy của acc (fallback tương thích).</summary>
    private static SearchAccountSpec ToSpec(ShopeeAccount a)
    {
        var pooled = KiotProxyPoolStore.Shared.ProxyForAccount(a.Id);
        var kiot = pooled?.KiotKey ?? a.KiotProxyKey;
        var manual = pooled?.Manual ?? a.ManualProxy;
        return new(a.Id, a.DisplayName, a.ShopeeAccountLogin, a.OpenWithShopeeAccount,
            kiot, a.ProxyType, manual, LocalProfileDir(a), a.RequireProxy);
    }

    /// <summary>Thư mục profile trình duyệt RIÊNG-MÁY của tk: LUÔN dưới gốc profile CỤC BỘ theo Id. KHÔNG dùng
    /// <see cref="ShopeeAccount.ProfileRelativePath"/> thô — nó có thể là đường dẫn TUYỆT ĐỐI của MÁY KHÁC (acc
    /// đến từ Hub lưu path "C:\Users\&lt;user máy Hub&gt;\…") khiến client cố tạo profile dưới C:\Users\&lt;máy
    /// khác&gt;\ → "Access denied". Profile là riêng từng máy nên KHÔNG truyền xuyên máy.</summary>
    private static string LocalProfileDir(ShopeeAccount a) =>
        Path.Combine(SuitePaths.ModuleDir("shared"), "profiles", a.Id);

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

    // ── Việc Search Hub giao (đa máy) ─────────────────────────────────────────────
    /// <summary>true nếu lượt chạy hiện tại CHÍNH LÀ việc Hub <paramref name="id"/> (cho AssignmentWorker quan sát).</summary>
    public bool IsRunningAssignment(string id) => IsRunning && _assignmentId == id;

    /// <summary>Dừng lượt chạy nếu nó thuộc việc Hub <paramref name="id"/> (Hub huỷ việc → client dừng).</summary>
    public void StopAssignment(string id) { if (_assignmentId == id) Stop(); }

    /// <summary>Lấy (và xoá) kết quả terminal của việc Search <paramref name="id"/>: "completed" | "stopped" |
    /// "failed"; null nếu chưa có (AssignmentWorker sẽ suy theo grace).</summary>
    public string? TakeAssignmentOutcome(string id) => _assignmentOutcomes.TryRemove(id, out var v) ? v : null;

    /// <summary>
    /// Chạy đúng KHỐI link Hub giao (silent, KHÔNG mở dialog). Khóa tối đa <c>AccountsPerClient</c> tài khoản
    /// Shopee qua Hub account-lease (máy khác không đụng), heartbeat nền 60s, chạy resume theo từng link, rồi
    /// nhả khóa. Bám đúng cơ chế account-lease của Scrape. Trả về khi xong/dừng.
    /// </summary>
    public async Task RunAssignmentAsync(string assignmentId, SearchJobPayload p, CancellationToken externalCt)
    {
        if (IsRunning) return;   // máy đang chạy 1 search khác — bỏ (AssignmentWorker đã tiền-kiểm)
        var links = (p.Links ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (links.Count == 0) return;
        if (_pool.Count == 0) { Log("⚠ Việc Search Hub giao: kho tài khoản Shopee trống — bỏ qua."); return; }

        var region = string.IsNullOrWhiteSpace(p.Region) ? Region : p.Region!;
        var source = string.IsNullOrWhiteSpace(p.SourceFile) ? "(Hub giao)" : p.SourceFile!;

        // Dựng tab cho từng link của khối (mỗi link 1 tab) — giống chạy tay để theo dõi tiến độ.
        var items = new List<(int Index, string Link, string SourceFile)>();
        for (var i = 0; i < links.Count; i++)
        {
            var link = links[i];
            var tab = LinkTabs.FirstOrDefault(t => t.Link == link);
            if (tab is null) { tab = new SearchFileTab(i + 1, link, source, FileRunCoordinator.CatLabel(link)); LinkTabs.Add(tab); }
            tab.Status = "chờ";
            items.Add((i + 1, link, source));
        }
        SelectedLinkTab ??= LinkTabs.FirstOrDefault();

        _assignmentId = assignmentId;
        IsRunning = true;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _usedAccounts.Clear();
        lock (_pushedItemIds) _pushedItemIds.Clear();

        var accHub = CoordinationRuntime.Hub;
        AccountLeaseScope? accScope = null;   // khóa tk Shopee xuyên máy: reserve→heartbeat→bù→nhả (gói)
        System.Threading.Timer? pushTimer = null;
        var startedRun = false;
        var failedRun = false;
        // BeginRun TRƯỚC khi MarkHubLeased ở vòng giành acc (mirror Scrape) → _activeRuns của CHÍNH lượt này giữ
        // ≥1 suốt, để EndRun của module KHÁC (vd Scrape vừa xong) KHÔNG xóa nhầm dấu _hubLeased ta vừa đặt.
        ShopeeAccountUsage.Shared.BeginRun();
        try
        {
            // Khóa tk Shopee xuyên máy: giành cả pool (Hub trả tk KHÔNG bị máy khác giữ) rồi chỉ giữ N cái đầu,
            // nhả phần thừa để máy khác dùng. Offline (không Hub) → dùng cả pool như chạy 1 máy.
            var working = _pool.Select(a => a.Id).ToList();
            if (accHub is not null)
            {
                // Giành ĐÚNG số acc cần (N) — KHÔNG giành cả pool rồi trả. Cơ chế per-account (khớp lease cục bộ +
                // Hub từng cái, chống 2 module cùng máy xóa nhầm 1 dòng lease machine-scoped) gói trong
                // AccountLeaseScope; heartbeat nền 60s + bù tk cũng do scope lo. (Trước là bản mirror Scrape.)
                List<string> acquired;
                (accScope, acquired) = await AccountLeaseScope.AcquirePerAccountAsync(accHub, working, Math.Max(1, p.AccountsPerClient));
                working = acquired;   // finally (Dispose scope) nhả ĐÚNG những gì đã giành → không rò acc
                if (working.Count == 0)
                { Log("⚠ Việc Search Hub giao: mọi tài khoản Shopee đang được máy khác giữ — bỏ qua."); return; }
            }

            var specs = _pool.Where(a => working.Contains(a.Id)).Select(ToSpec).ToList();
            // Số lane do CHÍNH client quyết theo LaneCount cấu hình của MÁY NÀY (giống Scrape chạy tùy máy) —
            // KHÔNG theo p.Lanes của Hub. Vẫn kẹp theo số acc giành được và số link của khối (không thể nhiều
            // lane hơn acc/link).
            var lanes = Math.Max(1, Math.Min(Math.Min(LaneCount, specs.Count), items.Count));
            Log($"▶ Search (Hub giao) {items.Count} link · {specs.Count}/{_pool.Count} acc (khóa xuyên máy) · {lanes} lane · khu vực \"{region}\".");
            startedRun = true;
            // Đẩy sản phẩm cào được lên Hub theo CHU KỲ 20s trong lúc chạy → kết quả gộp cập nhật LIÊN TỤC.
            if (CoordinationRuntime.Client is not null)
                pushTimer = new System.Threading.Timer(_ => _ = PushNewCollectedAsync(_runner, source), null,
                    TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
            // BÙ TK THAY THẾ: khi 1 tk trong nhóm dính captcha, scope xin 1 tk RẢNH từ kho (đã khóa lease xuyên
            // máy), ghi vào sổ lease (heartbeat + nhả ở finally) rồi NHẢ giữ-chỗ cục bộ để lane borrow như tk ban đầu.
            Func<IReadOnlyCollection<string>, CancellationToken, Task<SearchAccountSpec?>>? acquireReplacement = null;
            if (accScope is not null)
                acquireReplacement = async (excludeIds, rct) =>
                {
                    var repl = await accScope.AcquireReplacementAsync(excludeIds, rct).ConfigureAwait(false);
                    return repl is null ? null : ToSpec(repl);
                };
            await RunCoreAsync(items, specs, lanes, region, resume: true, _cts.Token, acquireReplacement);
            Log(_cts.IsCancellationRequested ? "── Đã dừng việc Search (giữ phiên). ──" : "── Hoàn tất việc Search Hub giao. ──");
        }
        catch (OperationCanceledException) { Log("── Đã dừng việc Search. ──"); }
        catch (Exception ex) { failedRun = true; Log("✘ Lỗi việc Search Hub giao: " + ex.Message); }
        finally
        {
            if (pushTimer is not null) { try { await pushTimer.DisposeAsync(); } catch { } }
            // Nhả account-lease (heartbeat → UnmarkHubLeased → ReleaseAccountsAsync Hub), snapshot-under-lock chống rò.
            if (accScope is not null) { try { await accScope.DisposeAsync().ConfigureAwait(false); } catch { } }
            // Đẩy nốt phần sản phẩm còn lại lên Hub (kể cả khi dừng dở — gửi phần đã cào).
            if (startedRun) await PushNewCollectedAsync(_runner, source);
            // Kết quả terminal cho AssignmentWorker báo Hub đúng: chưa chạy được / lỗi = failed; bị dừng = stopped;
            // chạy hết bình thường = completed. (Search không ghi ledger nên phải tự ghi outcome ở đây.)
            var canceled = _cts?.IsCancellationRequested == true;
            _assignmentOutcomes[assignmentId] = !startedRun || failedRun ? "failed" : canceled ? "stopped" : "completed";
            ResetUsedAccounts();
            _assignmentId = null;
            _cts?.Dispose();
            _cts = null;
            IsRunning = false;
            ShopeeAccountUsage.Shared.EndRun();   // đối xứng BeginRun ở đầu (counter về, nhả lưới an toàn nếu là lượt cuối)
        }
    }

    /// <summary>Đẩy phần sản phẩm MỚI (chưa đẩy) của lượt chạy này lên Hub để gộp xuyên máy. Best-effort, chia
    /// lô 500; chỉ đánh dấu "đã đẩy" SAU khi gửi thành công (lỗi mạng → lần sau gửi lại). Gọi định kỳ + lúc kết thúc.</summary>
    private async Task PushNewCollectedAsync(SearchRunner? runner, string sourceFile)
    {
        var client = CoordinationRuntime.Client;
        if (client is null || runner is null) return;
        List<ProductResult> fresh;
        lock (_pushedItemIds)
            fresh = runner.CollectedProducts().Where(p => p.ItemId != 0 && !_pushedItemIds.Contains(p.ItemId)).ToList();
        if (fresh.Count == 0) return;
        var machineId = CoordinationRuntime.Hub?.MachineId ?? "";
        for (var i = 0; i < fresh.Count; i += 500)
        {
            var batch = fresh.GetRange(i, Math.Min(500, fresh.Count - i));
            var payload = batch.Select(p => new SearchProductItem(p.ItemId, JsonSerializer.Serialize(p))).ToList();
            try
            {
                await client.PushSearchProductsAsync(new SearchProductsPushRequest(machineId, sourceFile, payload));
                lock (_pushedItemIds) foreach (var p in batch) _pushedItemIds.Add(p.ItemId);
            }
            catch { }
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
        _runner.AccountErrored += (id, reason, captchaUrl) => OnUi(() =>
        {
            var label = _pool.FirstOrDefault(a => a.Id == id)?.DisplayName ?? id;
            var now = DateTime.Now.ToString("HH:mm:ss");
            var row = ErroredAccounts.FirstOrDefault(x => x.Id == id);
            if (row is null) ErroredAccounts.Insert(0, new ErroredAccountRow(id, label, reason, now));
            else { row.Reason = reason; row.Time = now; }
            LogLines.Add($"⚠ Tk lỗi: {label} — {reason} (engine tự đổi account khác)");
            FlagAccountErrored(id, $"Dính captcha/lỗi (Search) — {DateTime.Now:dd/MM HH:mm}: {reason}", captchaUrl);
        });
    }

    private static void FlagAccountErrored(string id, string reason, string? captchaUrl = null)
    {
        var acc = AccountStore.Shared.Accounts.FirstOrDefault(a => a.Id == id);
        if (acc is null) return;
        var alreadyFlagged = acc.Disabled;
        acc.Disabled = true;
        acc.LastError = reason;
        // Lưu LINK đang cào lúc dính captcha (KHÔNG lưu trang /verify) → "Kiểm tra tk lỗi" mở lại đúng link.
        if (!string.IsNullOrWhiteSpace(captchaUrl)) acc.CaptchaUrl = captchaUrl;
        if (!alreadyFlagged || !string.IsNullOrWhiteSpace(captchaUrl)) AccountStore.Shared.Save();
        // CLIENT: báo Hub acc này dính captcha (Hub xem ở panel + operator quyết giữ/xóa). Hub/standalone: khỏi báo.
        if (CoordinationRuntime.Active && !HubServerConfigStore.Shared.Current.Enabled)
            _ = CoordinationRuntime.Hub?.ReportErroredAccountAsync(id, reason, acc.CaptchaUrl, "captcha");
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
        foreach (var t in LinkTabs) { t.Products.Clear(); t.ProductCount = 0; }
        RefreshCategoryGrid();
        Status = "Đã xóa dữ liệu đã quét.";
    }

    // ── Tab Danh mục (AI) ───────────────────────────────────────────────────────
    [RelayCommand]
    private async Task BrowseDocxAsync()
    {
        var path = await FilePicker.OpenFileAsync("Chọn file danh mục lá Shopee", "Word/Excel danh mục|*.docx;*.xlsx|Tất cả|*.*");
        if (path is not null) CategoryDocxPath = path;
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
        var excelPath = await FilePicker.OpenFileAsync("Chọn file Excel (cột tên sản phẩm) để gán danh mục", "Excel|*.xlsx");
        if (excelPath is null) return;

        AiBusy = true; _aiCts = new CancellationTokenSource();
        try
        {
            Log("🤖 Cập nhật danh mục (AI) cho file: " + Path.GetFileName(excelPath));
            var n = await Db.UpdateCategoriesExcelAsync(ai, CategoryDocxPath, excelPath,
                (d, t) => OnUi(() => AiProgress = $"{d}/{t}"), _aiCts.Token);
            Status = $"Đã ghi danh mục cho {n} dòng trong file.";
            Log($"✔ AI: ghi danh mục {n} dòng → {excelPath}");
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
                // Gán THẲNG field (không qua property) để KHÔNG bắn PropertyChanged/OnChanged khi đang nạp UI.
#pragma warning disable MVVMTK0034
                if (!string.IsNullOrWhiteSpace(j.Docx)) _categoryDocxPath = j.Docx;
                if (!string.IsNullOrWhiteSpace(j.Output)) _outputDir = j.Output;
                if (!string.IsNullOrWhiteSpace(j.Region)) _region = j.Region;
#pragma warning restore MVVMTK0034
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

}
