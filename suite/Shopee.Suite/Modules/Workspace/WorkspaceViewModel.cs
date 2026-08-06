using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shopee.Core.BigSeller;
using Shopee.Core.Coordination;
using Shopee.Core.Progress;
using Shopee.Core.Scrape;
using Shopee.Suite.Modules.BigSeller;
using Shopee.Suite.Modules.Data;
using Shopee.Suite.Modules.Scrape;
using Shopee.Suite.Modules.UpdateProduct;
using Shopee.Suite.Services;

namespace Shopee.Suite.Modules.Workspace;

/// <summary>
/// MÀN GỘP v1.1 — gộp BigSeller (cấu hình) + Scrape + Update vào MỘT chỗ, xoay quanh từng tk BigSeller:
/// chọn 1 tk → thấy ngay có shop nào, shop nào đã/đang/chưa scrape, rồi Scrape / Import / Update / Tên SP
/// NGAY tại chỗ cho từng shop — hết phải nhảy qua lại 3 màn.
///
/// KHÔNG viết lại engine: tái dùng nguyên 3 ViewModel cũ (Shell truyền vào, dùng CHUNG với 3 màn cũ) nên
/// state luôn đồng bộ, không xung đột. Đây chỉ là lớp UI gắn kết + uỷ lệnh xuống 3 VM đó.
/// </summary>
public sealed partial class WorkspaceViewModel : ObservableObject
{
    public BigSellerViewModel BigSeller { get; }
    public ScrapeViewModel Scrape { get; }
    public UpdateProductViewModel Update { get; }

    public ObservableCollection<WorkspaceAccountViewModel> Accounts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private WorkspaceAccountViewModel? _selectedAccount;

    public bool HasSelection => SelectedAccount is not null;

    /// <summary>Tab "Dữ liệu": lưới dữ liệu của tài khoản đang chọn (fixed-acct). Tạo 1 LẦN; đổi tài khoản qua
    /// <see cref="DataViewModel.Rescope"/>. KHÔNG EnsureLoaded lúc tạo — DataView tự gọi khi tab được mở (lazy).</summary>
    public DataViewModel AccountData { get; } = new((string?)null);

    /// <summary>Tab "Thống kê": số liệu per-shop × per-op của tài khoản đang chọn (đọc sổ hoàn thành trên Hub,
    /// giống tab Thống kê của Hub). Tạo 1 LẦN; đổi tài khoản qua <see cref="WorkspaceStatsViewModel.Rescope"/>.</summary>
    public WorkspaceStatsViewModel Stats { get; } = new();

    /// <summary>Có bất kỳ việc nào đang chạy? scrape · update batch · update inline theo shop.
    /// Dùng bật/tắt nút "Dừng tất cả" (không có gì chạy → nút mờ, không bấm được).</summary>
    public bool AnyRunning => Scrape.IsBusy || Update.IsRunning || Update.HasActiveWsJob;

    [ObservableProperty] private string _status = "";

    /// <summary>Thư mục video DÙNG CHUNG (máy này) — gộp 2 ô cũ (Scrape.VideoDir + Update.VideoFolder) về 1 ô.
    /// Đọc từ Update.VideoFolder (nguồn sự thật ĐÃ LƯU qua UpdateProductUiStore) trước, fallback Scrape.VideoDir
    /// (chỉ là ObservableProperty mặc định, KHÔNG persist); ghi → set CẢ HAI để 2 luồng dùng chung.</summary>
    public string VideoDir
    {
        get => !string.IsNullOrWhiteSpace(Update.VideoFolder) ? Update.VideoFolder : Scrape.VideoDir;
        set
        {
            Scrape.VideoDir = value;
            Update.VideoFolder = value;   // OnVideoFolderChanged tự lưu UpdateProductUiStore
            OnPropertyChanged();
        }
    }

    /// <summary>Shell cấp: điều hướng sidebar sang ViewModel khác (mở tab BigSeller để đăng nhập/cấu hình).</summary>
    public Action<object>? RequestNavigate { get; set; }

    public WorkspaceViewModel(BigSellerViewModel bigSeller, ScrapeViewModel scrape, UpdateProductViewModel update)
    {
        BigSeller = bigSeller;
        Scrape = scrape;
        Update = update;

        // Seed lại Scrape.VideoDir từ Update.VideoFolder sau restart: Scrape.VideoDir KHÔNG persist (luôn về
        // mặc định "D:\videos" lúc khởi động), còn Update.VideoFolder là nguồn sự thật ĐÃ LƯU (UpdateProductUiStore)
        // → thiếu bước này thì engine SCRAPE chạy nhầm thư mục mặc định dù user đã chọn thư mục khác.
        if (!string.IsNullOrWhiteSpace(Update.VideoFolder) && Update.VideoFolder != Scrape.VideoDir)
            Scrape.VideoDir = Update.VideoFolder;

        Rebuild();
        // Handler này đăng ký SAU 3 sub-VM (Shell tạo chúng trước) → khi store đổi, chúng reload trước,
        // rồi tới đây zip lại danh sách hợp nhất.
        BigSellerStore.Shared.Changed += OnStoreChanged;

        // Nút "Dừng tất cả" bật/tắt theo trạng thái chạy: IsBusy (scrape) + IsRunning (update batch) bắn
        // PropertyChanged; update inline per-shop (HasActiveWsJob) đổi → báo qua JobsChanged. Các VM này sống
        // suốt vòng đời app (như Workspace) nên không cần gỡ handler.
        Scrape.PropertyChanged += OnRunStateChanged;
        Update.PropertyChanged += OnRunStateChanged;
        Update.JobsChanged += OnRunJobsChanged;

        // Nút "⏯ Tiếp tục việc dở": đếm lại số việc CHẠY TAY còn dở khi tiến độ 2 store đổi (BeginRun/FinishRun/
        // Clear đều bắn Changed) — các store bắn từ luồng runner nên marshal về UI thread. Đếm lần đầu do
        // Rebuild() phía trên đã gọi RecomputeResumePending().
        ScrapeProgressStore.Shared.Changed += OnProgressStoresChanged;
        OpProgressStore.Shared.Changed += OnProgressStoresChanged;

        // Hub hủy/bỏ-hẳn 1 việc → client phải BỎ khỏi "việc dở" + Clear tiến độ local. Fleet cập nhật theo nhịp
        // poll 12s của Hub (bắn Changed) → hook cùng event fleet như WorkspaceAccountViewModel để RecomputeResume-
        // Pending chạy lại kịp thời (không phải đợi 2 store đổi). VM này sống suốt vòng đời app → KHÔNG cần gỡ handler.
        Coordination.Hub.Changed += OnFleetChanged;
    }

    private void OnFleetChanged() => UiThread.Post(RecomputeResumePending);

    private void OnProgressStoresChanged()
    {
        // _recomputing = true khi RecomputeResumePending đang Clear tiến độ Hub-hủy: Clear bắn Changed → vào đây,
        // mà UiThread.Post chạy ĐỒNG BỘ khi đang ở UI thread → sẽ tái nhập RecomputeResumePending. Bỏ qua để tránh đệ quy.
        if (_recomputing) return;
        UiThread.Post(RecomputeResumePending);
    }

    private void OnStoreChanged()
    {
        // Đang chạy: các sub-VM giữ nguyên list (Scrape bỏ qua reload khi busy) → khỏi rebuild để không
        // churn tham chiếu job đang chạy. Bao gồm cả update inline per-shop (HasActiveWsJob, không set IsRunning).
        if (AnyRunning) return;
        UiThread.Post(Rebuild);
    }

    private void OnRunStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ScrapeViewModel.IsBusy) or nameof(UpdateProductViewModel.IsRunning))
            NotifyAnyRunning();
    }

    private void OnRunJobsChanged() => NotifyAnyRunning();

    private void NotifyAnyRunning() => UiThread.Post(() =>
    {
        OnPropertyChanged(nameof(AnyRunning));
        StopAllCommand.NotifyCanExecuteChanged();
        // Việc chuyển running→stopped (user bấm Dừng) làm 1 mục thành "còn dở" — đếm lại luôn tại đây (ngoài
        // đường store Changed) để nút "⏯ Tiếp tục việc dở" hiện ngay khi vừa dừng.
        RecomputeResumePending();
    });

    private void Rebuild()
    {
        var prevId = SelectedAccount?.Account.Id;
        foreach (var a in Accounts) a.Detach();
        Accounts.Clear();

        // Zip 3 list theo Account.Id (cùng nguồn BigSellerStore nên khớp tập tk).
        foreach (var bs in BigSeller.Items)
        {
            var scrape = Scrape.ScrapeTargets.FirstOrDefault(t => t.Account.Id == bs.Model.Id);
            var update = Update.RunTargets.FirstOrDefault(t => t.Account.Id == bs.Model.Id);
            if (scrape is null || update is null) continue;   // sub-VM chưa kịp đồng bộ → bỏ qua vòng này
            // Buffer log RIÊNG acc (theo Account.Id) — registry cache-khi-cần, bền qua rebuild → tab log per-acc bind vào.
            var scrapeLog = Scrape.AccountLogs.Get(bs.Model.Id, bs.Model.DisplayName);
            var updateLog = Update.AccountLogs.Get(bs.Model.Id, bs.Model.DisplayName);
            Accounts.Add(new WorkspaceAccountViewModel(bs, scrape, update, Update, scrapeLog, updateLog));
        }

        // Dải chip lọc log dựng TRƯỚC khi gán SelectedAccount: OnSelectedAccountChanged sẽ trỏ chip theo acc mới.
        RebuildLogChips();
        SelectedAccount = Accounts.FirstOrDefault(a => a.Account.Id == prevId) ?? Accounts.FirstOrDefault();
        SyncLogChipsToAccount();   // Accounts rỗng / acc không đổi → gán ở trên không bắn Changed, tự đồng bộ ở đây
        Status = $"{Accounts.Count} tài khoản BigSeller.";
        RecomputeResumePending();   // list acc đổi → map lại các mục "còn dở" theo acc/shop hiện có
    }

    // ── Dải chip LỌC LOG (H3.1) ────────────────────────────────────────────────
    // Hạ tầng log per-acc (AccountLogRegistry) đã ghi sẵn từ lâu; đợt này bind ra UI: mỗi tab log có một dải
    // chip "Tất cả" + mỗi tk một chip. HÀNH VI MẶC ĐỊNH GIỮ NGUYÊN: chip đang chọn BÁM theo tài khoản đang
    // chọn ở cột trái (đúng thứ 2 tab log vẫn hiển thị trước đây); người dùng bấm "Tất cả" thì chế độ đó DÍNH
    // qua các lần đổi tài khoản (chỉ bấm chip khác mới thoát).

    /// <summary>Chip lọc log của tab "Theo dõi Scrape" ("Tất cả" + mỗi tk 1 chip).</summary>
    public ObservableCollection<LogFilterChipViewModel> ScrapeLogChips { get; } = [];
    /// <summary>Chip lọc log của tab "Theo dõi Update".</summary>
    public ObservableCollection<LogFilterChipViewModel> UpdateLogChips { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScrapeLogHeader))]
    private LogFilterChipViewModel? _selectedScrapeLogChip;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateLogHeader))]
    private LogFilterChipViewModel? _selectedUpdateLogChip;

    // Chỉ cú bấm THẬT của người dùng mới đổi chế độ. Guard _rebuildingLogChips là BẮT BUỘC: lúc dựng lại dải
    // chip, ListBox thấy ItemsSource bị Clear nên tự ghi ngược SelectedItem = null → nếu không chặn thì chế độ
    // "Tất cả" bị xoá sạch ở MỌI lần kho BigSeller bắn Changed (rất thường xuyên — Save mỗi phím cũng bắn).
    // Điều kiện thứ hai (SelectedAccount is not null): CHƯA có tài khoản nào thì PickChip buộc phải trả chip
    // "Tất cả" (không có chip acc để trỏ tới) — ghi lại thành _showAll sẽ khoá chế độ "Tất cả" vĩnh viễn, nên
    // thêm tài khoản đầu tiên xong dải chip không bám tk như mặc định đã hứa.
    partial void OnSelectedScrapeLogChipChanged(LogFilterChipViewModel? value)
    { if (!_rebuildingLogChips && SelectedAccount is not null) _scrapeLogShowAll = value?.IsAll ?? false; }
    partial void OnSelectedUpdateLogChipChanged(LogFilterChipViewModel? value)
    { if (!_rebuildingLogChips && SelectedAccount is not null) _updateLogShowAll = value?.IsAll ?? false; }

    // Người dùng đang ở chế độ "Tất cả" (gộp) — giữ qua các lần đổi tài khoản / dựng lại dải chip.
    private bool _scrapeLogShowAll;
    private bool _updateLogShowAll;
    private bool _rebuildingLogChips;

    public string ScrapeLogHeader => LogHeader("Nhật ký Scrape", SelectedScrapeLogChip);
    public string UpdateLogHeader => LogHeader("Nhật ký Update", SelectedUpdateLogChip);

    private static string LogHeader(string prefix, LogFilterChipViewModel? chip) => chip is null
        ? $"{prefix} (chưa có tài khoản)"
        : chip.IsAll
            ? $"{prefix} — mọi tài khoản (500 dòng cuối · gộp)"
            : $"{prefix} — {chip.Title} (500 dòng cuối · riêng acc này)";

    private void RebuildLogChips()
    {
        _rebuildingLogChips = true;
        try
        {
            foreach (var c in ScrapeLogChips) c.Dispose();
            foreach (var c in UpdateLogChips) c.Dispose();
            ScrapeLogChips.Clear();
            UpdateLogChips.Clear();

            ScrapeLogChips.Add(new LogFilterChipViewModel("", "Tất cả", Scrape.LogLines));
            UpdateLogChips.Add(new LogFilterChipViewModel("", "Tất cả", Update.LogLines));
            foreach (var a in Accounts)
            {
                ScrapeLogChips.Add(new LogFilterChipViewModel(a.Account.Id, a.DisplayName, a.ScrapeLog));
                UpdateLogChips.Add(new LogFilterChipViewModel(a.Account.Id, a.DisplayName, a.UpdateLog));
            }
        }
        finally { _rebuildingLogChips = false; }
    }

    /// <summary>Trỏ chip đang chọn về tài khoản đang chọn — TRỪ khi người dùng đang ở chế độ "Tất cả" (chip đó
    /// dính). Gọi sau mỗi lần dựng lại dải chip hoặc đổi tài khoản.</summary>
    private void SyncLogChipsToAccount()
    {
        var id = SelectedAccount?.Account.Id ?? "";
        SelectedScrapeLogChip = PickChip(ScrapeLogChips, _scrapeLogShowAll, id);
        SelectedUpdateLogChip = PickChip(UpdateLogChips, _updateLogShowAll, id);
    }

    private static LogFilterChipViewModel? PickChip(
        ObservableCollection<LogFilterChipViewModel> chips, bool showAll, string accountId)
    {
        var all = chips.FirstOrDefault(c => c.IsAll);
        if (showAll) return all;
        return chips.FirstOrDefault(c => c.AccountId == accountId && accountId.Length > 0) ?? all;
    }

    partial void OnSelectedAccountChanged(WorkspaceAccountViewModel? value)
    {
        // Đồng bộ cho Thống kê/Map chạy đúng tk đang xem. KHÔNG set BigSeller.Selected ở đây — nếu set, mỗi
        // lần kho BigSeller bắn Changed (vd sửa shop ở tab Cấu hình) sẽ kéo màn Cấu hình nhảy về tk Workspace
        // đang chọn. BigSeller.Selected chỉ set khi bấm "Đăng nhập / cấu hình" (GoToBigSellerConfig).
        Scrape.SelectedTarget = value?.ScrapeTarget;
        Update.SelectedTarget = value?.UpdateTarget;
        SyncLogChipsToAccount();   // dải chip log bám theo tk đang chọn (trừ khi đang ở chế độ "Tất cả")
        value?.RefreshAll();
        AccountData.Rescope(value?.Account.Id);   // tab "Dữ liệu" theo tài khoản đang chọn
        Stats.Rescope(value?.Account);            // tab "Thống kê" theo tài khoản đang chọn
    }

    // ── Tài khoản ────────────────────────────────────────────────────────────
    // (Thêm/xoá/đăng nhập tài khoản BigSeller đều nằm ở tab "BigSeller" — không lặp lại ở đây.)
    [RelayCommand] private void Reload()
    {
        Scrape.ReloadCommand.Execute(null);
        Update.ReloadCommand.Execute(null);
        Rebuild();
    }

    /// <summary>Chọn thư mục video dùng chung (ghi cả Scrape.VideoDir lẫn Update.VideoFolder qua setter VideoDir).</summary>
    [RelayCommand]
    private async Task BrowseVideoDir()
    {
        var dir = await FilePicker.PickFolderAsync("Chọn thư mục video");
        if (dir is not null) VideoDir = dir;
    }

    // ── Đăng nhập / cấu hình: KHÔNG làm tại đây (tránh trùng 2 nơi) — chuyển sang tab BigSeller ──
    [RelayCommand]
    private void GoToBigSellerConfig()
    {
        if (SelectedAccount is not null) BigSeller.Selected = SelectedAccount.Bs;   // mở đúng tk bên tab kia
        RequestNavigate?.Invoke(BigSeller);
    }

    // ── Mở FILE log của CHIP đang chọn trên dải lọc (buffer UI chỉ giữ 500 dòng cuối; file có đủ). Bám chip
    //    chứ không bám tk cột trái: nếu không, đang xem log tk B mà nút lại mở file của tk A đang chọn bên trái.
    //    Chip "Tất cả" → chính là file log gộp (đúng file nút "Log gộp" mở).
    [RelayCommand] private void OpenScrapeAccLog() { if (SelectedScrapeLogChip is { } c) ShellOpener.RevealFile(c.Buffer.FilePath); }
    [RelayCommand] private void OpenUpdateAccLog() { if (SelectedUpdateLogChip is { } c) ShellOpener.RevealFile(c.Buffer.FilePath); }

    // ── Chọn shop (1 shop/account) ───────────────────────────────────────────
    [RelayCommand]
    private void PickShop(WorkspaceShopViewModel? shop)
    {
        if (shop is null) return;
        shop.Parent.ScrapeTarget.SelectedShop = shop.Shop;
        shop.Parent.UpdateTarget.SelectedShop = shop.Shop;
        foreach (var s in shop.Parent.Shops) s.RefreshStatus();
    }

    // ── Scrape theo shop ─────────────────────────────────────────────────────
    // QUAN TRỌNG: các lệnh chạy là VOID + FIRE-AND-FORGET (không await tác vụ). Nếu await, lần bấm đầu
    // (account rảnh) gọi RunSingleAsync→StartAsync chạy COORDINATOR tới hết phiên mới trả về → AsyncRelayCommand
    // (mặc định KHÔNG cho chạy chồng) sẽ tự khoá, nuốt mọi lần bấm sau → account khác "không thực hiện được".
    // AN TOÀN fire-and-forget là BẤT BIẾN của callee, không phải may rủi: các entry RunSingleAsync /
    // StopSingleAsync / Run*SingleAsync là TOTAL — tự bọc try/catch toàn thân, KHÔNG ném ra ngoài và KHÔNG
    // rò state (IsBusy/_wsJobs gỡ trong finally dù setup ném). Đổi callee thì phải giữ bất biến này.
    [RelayCommand] private void ScrapeShop(WorkspaceShopViewModel? shop) => RunScrapeShop(shop, resume: false);
    [RelayCommand] private void ResumeShop(WorkspaceShopViewModel? shop) => RunScrapeShop(shop, resume: true);

    private void RunScrapeShop(WorkspaceShopViewModel? shop, bool resume)
    {
        if (shop is null) return;
        shop.Parent.ScrapeTarget.SelectedShop = shop.Shop;
        _ = Scrape.RunSingleAsync(shop.Parent.ScrapeTarget, resume);
    }

    /// <summary>Dừng RIÊNG scrape của tk chứa shop này (1 shop/account) — tk khác chạy tiếp.</summary>
    [RelayCommand]
    private void StopShop(WorkspaceShopViewModel? shop)
    {
        if (shop is null) return;
        _ = Scrape.StopSingleAsync(shop.Parent.ScrapeTarget);
    }

    // ── Update theo shop (Import / Update / Tên SP) — cũng fire-and-forget (lý do như trên) ──
    [RelayCommand] private void ImportShop(WorkspaceShopViewModel? shop) => RunUpdateShop(shop, t => Update.RunImportSingleAsync(t));
    [RelayCommand] private void UpdateShop(WorkspaceShopViewModel? shop) => RunUpdateShop(shop, t => Update.RunUpdateSingleAsync(t));
    [RelayCommand] private void RewriteShop(WorkspaceShopViewModel? shop) => RunUpdateShop(shop, t => Update.RunNameRewriteSingleAsync(t));

    private void RunUpdateShop(WorkspaceShopViewModel? shop, Func<UpdateRunTargetViewModel, Task> run)
    {
        if (shop is null) return;
        shop.Parent.UpdateTarget.SelectedShop = shop.Shop;
        _ = run(shop.Parent.UpdateTarget);
    }

    /// <summary>Dừng RIÊNG workflow update (import/update/tên SP) của tk chứa shop này — nút ■ inline.</summary>
    [RelayCommand]
    private void StopUpdateShop(WorkspaceShopViewModel? shop)
    {
        if (shop is null) return;
        Update.StopSingle(shop.Parent.Account.Id);
    }

    // ── Nút TOGGLE (1 nút/op): chưa chạy → CHẠY; đang chạy op đó trên máy này → DỪNG. Khoá do Can*/Enabled lo. ──
    [RelayCommand]
    private void ScrapeToggle(WorkspaceShopViewModel? shop)
    {
        if (shop is null) return;
        if (shop.IsScraping) _ = Scrape.StopSingleAsync(shop.Parent.ScrapeTarget);
        else RunScrapeShop(shop, resume: true);   // bấm chạy = tiếp tục dòng còn thiếu
    }

    [RelayCommand] private void ImportToggle(WorkspaceShopViewModel? shop) => ToggleUpdateOp(shop, shop?.IsImporting ?? false, t => Update.RunImportSingleAsync(t));
    [RelayCommand] private void UpdateToggle(WorkspaceShopViewModel? shop) => ToggleUpdateOp(shop, shop?.IsUpdatingShop ?? false, t => Update.RunUpdateSingleAsync(t));
    [RelayCommand] private void RewriteToggle(WorkspaceShopViewModel? shop) => ToggleUpdateOp(shop, shop?.IsRewriting ?? false, t => Update.RunNameRewriteSingleAsync(t));

    private void ToggleUpdateOp(WorkspaceShopViewModel? shop, bool runningThisOp, Func<UpdateRunTargetViewModel, Task> run)
    {
        if (shop is null) return;
        if (runningThisOp) { Update.StopSingle(shop.Parent.Account.Id); return; }
        shop.Parent.UpdateTarget.SelectedShop = shop.Shop;
        _ = run(shop.Parent.UpdateTarget);
    }

    // (Map field ↔ cột Excel làm bên tab "Cấu hình BigSeller" khi cài đặt shop — không lặp ở workspace.)

    // ── Dừng ──────────────────────────────────────────────────────────────────

    /// <summary>Nút "■ Dừng việc shop này" (góc phải hàng tab): dừng scrape + update đang chạy của acc đang chọn.</summary>
    [RelayCommand]
    private void StopSelectedWork()
    {
        if (SelectedAccount is not { } a) return;
        if (a.IsScraping) _ = Scrape.StopSingleAsync(a.ScrapeTarget);
        Update.StopSingle(a.Account.Id);   // không có job thì tự bỏ qua
    }

    /// <summary>Dừng TẤT CẢ scrape + update đang chạy. Chỉ bật khi <see cref="AnyRunning"/> = true.</summary>
    [RelayCommand(CanExecute = nameof(AnyRunning))]
    private void StopAll()
    {
        Update.StopAllSingle();
        if (Scrape.StopCommand.CanExecute(null)) Scrape.StopCommand.Execute(null);
    }

    // ── Tiếp tục việc CHẠY TAY còn dở (sau khi mở lại app / bị dừng giữa chừng) ─────────────────────────
    // Chỉ dành cho việc user tự bấm chạy tại Workspace. Việc HUB giao có đường resume riêng (AssignmentWorker
    // ResumeMineAsync + nút ▶ trên Fleet) → LOẠI khỏi đây để tránh chạy đôi.

    /// <summary>1 việc chạy-tay còn dở: op (scrape/import/update) + tk gộp + shop cụ thể (để set SelectedShop
    /// rồi phóng đúng entry-point silent).</summary>
    private sealed record ResumeItem(string Op, WorkspaceAccountViewModel Acct, BigSellerShop Shop);

    private readonly List<ResumeItem> _resumePending = [];

    /// <summary>Danh sách chi tiết từng việc dở để banner LIỆT KÊ (op · tk · shop · tiến độ · giờ chạy cuối).
    /// Dựng lại song song <see cref="_resumePending"/> mỗi lần <see cref="RecomputeResumePending"/>.</summary>
    public ObservableCollection<ResumePendingRow> ResumeRows { get; } = [];

    /// <summary>Đang trong vòng Clear tiến độ Hub-hủy của RecomputeResumePending → chặn Changed tái nhập hàm
    /// (xem <see cref="OnProgressStoresChanged"/>).</summary>
    private bool _recomputing;

    /// <summary>Số việc chạy-tay còn dở mà máy này tiếp tục được (đã loại việc hub quản + việc đang chạy thật).</summary>
    public int ResumePendingCount => _resumePending.Count;
    public bool HasResumePending => _resumePending.Count > 0;
    public string ResumeButtonText => $"⏯ Tiếp tục việc dở ({_resumePending.Count})";

    /// <summary>Tooltip liệt kê từng mục dở (op · shop · acc, mỗi mục 1 dòng).</summary>
    public string ResumeTooltip => _resumePending.Count == 0
        ? ""
        : "Chạy tiếp các việc còn dở:\n" +
          string.Join("\n", _resumePending.Select(r => $"• {OpLabel(r.Op)} · {r.Shop.DisplayName} ({r.Acct.DisplayName})"));

    private static string OpLabel(string op) => op switch
    {
        AssignmentOps.Scrape => "Scrape", AssignmentOps.Import => "Import", AssignmentOps.Update => "Update", _ => op,
    };

    /// <summary>Tiến độ scrape thành chữ: "đã cào {tới}/{tổng} dòng" (Tổng=0 chưa biết → "đã cào tới dòng {tới}").</summary>
    private static string ScrapeProgressText(ScrapeProgress p) =>
        p.TotalRowsAtLastRun > 0
            ? $"đã cào {p.LastRowReached}/{p.TotalRowsAtLastRun} dòng"
            : $"đã cào tới dòng {p.LastRowReached}";

    /// <summary>Giờ chạy cuối dạng ngắn "HH:mm dd/MM"; null → "".</summary>
    private static string FormatLastRun(DateTimeOffset? at) =>
        at is { } t ? t.ToLocalTime().ToString("HH:mm dd/MM", CultureInfo.InvariantCulture) : "";

    /// <summary>true nếu Hub ĐANG quản việc (acc,shop,op) này: assignment còn SỐNG (queued|running — hub sắp/đang
    /// chạy) hoặc nằm trong danh sách gián đoạn (có nút ▶ resume riêng trên Fleet) → KHÔNG mời tiếp-tục-tay để
    /// tránh chạy đôi. Bản đã KẾT THÚC (done/failed/canceled còn trong snapshot 2h) KHÔNG tính — việc hub đã xong/
    /// đã huỷ hẳn thì việc chạy-tay dở của user vẫn phải tiếp tục được.</summary>
    private static bool HubManages(string accId, string shopId, string op)
    {
        var fleet = CoordinationRuntime.Hub?.CurrentFleet;
        if (fleet is null) return false;
        bool Match(Assignment a) =>
            string.Equals(a.BigsellerId, accId, StringComparison.Ordinal) &&
            string.Equals(a.ShopId, shopId, StringComparison.Ordinal) &&
            string.Equals(a.Op, op, StringComparison.Ordinal);
        return fleet.Assignments.Any(a => Match(a) && a.Status is AssignmentStatus.Queued or AssignmentStatus.Running)
               || fleet.Interrupted.Any(Match);
    }

    /// <summary>true nếu Hub đã HỦY HẲN việc (acc,shop,op): trong <c>fleet.Assignments</c> có bản khớp
    /// <c>Status=="canceled"</c> HOẶC <c>Dismissed</c>, và KHÔNG còn bản nào khớp đang <c>queued</c>/<c>running</c>
    /// (không phải vừa giao lại). Gọi SAU <see cref="HubManages"/>: bản canceled CHƯA bị bỏ-hẳn vẫn nằm trong
    /// <c>fleet.Interrupted</c> ⇒ HubManages đã bắt trước → tiến độ local được GIỮ cho đường resume của Hub; tới đây
    /// chỉ còn bản Hub đã bỏ hẳn (dismissed / rớt khỏi Interrupted) → client tự xoá. Offline (fleet null/cũ, CHƯA
    /// từng thấy canceled) → false → KHÔNG xoá nhầm (chỉ xoá khi THẤY RÕ Hub đã hủy).</summary>
    private static bool HubCanceled(string accId, string shopId, string op)
    {
        var fleet = CoordinationRuntime.Hub?.CurrentFleet;
        if (fleet is null) return false;
        bool Match(Assignment a) =>
            string.Equals(a.BigsellerId, accId, StringComparison.Ordinal) &&
            string.Equals(a.ShopId, shopId, StringComparison.Ordinal) &&
            string.Equals(a.Op, op, StringComparison.Ordinal);
        var matches = fleet.Assignments.Where(Match).ToList();
        if (matches.Count == 0) return false;
        if (matches.Any(a => a.Status is AssignmentStatus.Queued or AssignmentStatus.Running)) return false;   // vừa giao lại → chưa hủy hẳn
        return matches.Any(a => a.Status == AssignmentStatus.Canceled || a.Dismissed);
    }

    /// <summary>Đếm lại các việc chạy-tay còn dở: scrape (ScrapeProgressStore) + import/update (OpProgressStore)
    /// có status ∈ {running (kẹt do crash), stopped (dừng dở)}, map về acc/shop hiện có, loại việc hub quản +
    /// việc đang chạy thật lúc này. Gọi trên UI thread (đọc Accounts + set observable).</summary>
    private void RecomputeResumePending()
    {
        _resumePending.Clear();
        var rows = new List<ResumePendingRow>();
        // Gom các mục Hub ĐÃ HỦY để Clear SAU vòng quét (tránh sửa store giữa iterator; guard _recomputing chặn
        // Changed do Clear bắn tái nhập hàm này — xem OnProgressStoresChanged).
        var clearScrape = new List<(string acc, string sheet)>();
        var clearOp = new List<(string acc, string sheet, string op)>();

        // Scrape: mỗi (acc, sheet) có tiến độ running/stopped → ứng viên tiếp tục dòng còn thiếu.
        foreach (var p in ScrapeProgressStore.Shared.All())
        {
            if (p.Status is not (LedgerStatus.Running or LedgerStatus.Stopped)) continue;
            var acct = Accounts.FirstOrDefault(a => a.Account.Id == p.AccountId);
            var shop = acct?.Account.Shops.FirstOrDefault(s =>
                string.Equals(s.ShopeeDataSheet ?? "", p.Sheet ?? "", StringComparison.OrdinalIgnoreCase));
            if (acct is null || shop is null) continue;                               // acc/shop đã xoá → bỏ
            if (HubManages(p.AccountId, shop.Id, AssignmentOps.Scrape)) continue;     // hub đang/sắp xử / để dành resume → bỏ (GIỮ tiến độ)
            if (HubCanceled(p.AccountId, shop.Id, AssignmentOps.Scrape))              // hub đã hủy hẳn → xoá tiến độ + bỏ
            {
                clearScrape.Add((p.AccountId, p.Sheet ?? ""));
                continue;
            }
            if (acct.ScrapeTarget.IsShopRunning?.Invoke(shop) ?? false) continue;     // đang scrape thật → bỏ
            _resumePending.Add(new ResumeItem(AssignmentOps.Scrape, acct, shop));
            rows.Add(new ResumePendingRow(OpLabel(AssignmentOps.Scrape), acct.DisplayName, shop.DisplayName,
                ScrapeProgressText(p), FormatLastRun(p.LastRunAt)));
        }

        // Import/Update: tiến độ per-SP running/stopped → ứng viên.
        foreach (var (accId, sheet, op, status) in OpProgressStore.Shared.Snapshot())
        {
            if (op is not (AssignmentOps.Import or AssignmentOps.Update)) continue;
            if (status is not (LedgerStatus.Running or LedgerStatus.Stopped)) continue;
            var acct = Accounts.FirstOrDefault(a => a.Account.Id == accId);
            var shop = acct?.Account.Shops.FirstOrDefault(s =>
                string.Equals(s.ShopeeDataSheet ?? "", sheet ?? "", StringComparison.OrdinalIgnoreCase));
            if (acct is null || shop is null) continue;
            if (HubManages(accId, shop.Id, op)) continue;
            if (HubCanceled(accId, shop.Id, op))
            {
                clearOp.Add((accId, sheet ?? "", op));
                continue;
            }
            if (Update.IsUpdateRunning(accId)) continue;   // acc đang chạy 1 workflow update thật → bỏ
            _resumePending.Add(new ResumeItem(op, acct, shop));
            var doneCount = OpProgressStore.Shared.GetDone(accId, sheet ?? "", op).Count;
            rows.Add(new ResumePendingRow(OpLabel(op), acct.DisplayName, shop.DisplayName,
                $"đã xong {doneCount} SP", ""));
        }

        ResumeRows.Clear();
        foreach (var r in rows) ResumeRows.Add(r);

        OnPropertyChanged(nameof(ResumePendingCount));
        OnPropertyChanged(nameof(HasResumePending));
        OnPropertyChanged(nameof(ResumeButtonText));
        OnPropertyChanged(nameof(ResumeTooltip));
        ResumePendingWorkCommand.NotifyCanExecuteChanged();
        DiscardPendingWorkCommand.NotifyCanExecuteChanged();

        // Clear tiến độ local các việc Hub đã hủy — SAU khi đã dựng danh sách + raise (các mục này đã bị loại khỏi
        // _resumePending/rows ở trên nên Clear không đổi kết quả, chỉ dọn để lần sau khỏi trồi lên). _recomputing
        // chặn vòng Changed→tái nhập.
        if (clearScrape.Count > 0 || clearOp.Count > 0)
        {
            _recomputing = true;
            try
            {
                foreach (var (acc, sheet) in clearScrape) ScrapeProgressStore.Shared.Clear(acc, sheet);
                foreach (var (acc, sheet, op) in clearOp) OpProgressStore.Shared.Clear(acc, sheet, op);
            }
            finally { _recomputing = false; }
        }
    }

    /// <summary>Nút "⏯ Tiếp tục việc dở (N)": chạy lại TẤT CẢ mục còn dở qua đúng entry-point silent (fire-and-
    /// forget như AssignmentWorker.LaunchCore) — KHÔNG truyền override dòng/process → dùng cấu hình client.</summary>
    [RelayCommand(CanExecute = nameof(HasResumePending))]
    private void ResumePendingWork()
    {
        // Chụp danh sách trước: launch khiến 2 store bắn Changed → RecomputeResumePending làm rỗng _resumePending.
        foreach (var item in _resumePending.ToList())
        {
            switch (item.Op)
            {
                case AssignmentOps.Scrape:
                    item.Acct.ScrapeTarget.SelectedShop = item.Shop;
                    _ = Scrape.RunSingleAsync(item.Acct.ScrapeTarget, resume: true, silent: true);
                    break;
                case AssignmentOps.Import:
                    item.Acct.UpdateTarget.SelectedShop = item.Shop;
                    _ = Update.RunImportSingleAsync(item.Acct.UpdateTarget, silent: true);
                    break;
                case AssignmentOps.Update:
                    item.Acct.UpdateTarget.SelectedShop = item.Shop;
                    _ = Update.RunUpdateSingleAsync(item.Acct.UpdateTarget, silent: true);
                    break;
            }
        }
        RecomputeResumePending();
    }

    /// <summary>Nút "Hủy": bỏ TẤT CẢ việc chạy-tay còn dở khỏi hàng chờ — xoá tiến độ dở ở 2 store để
    /// RecomputeResumePending không còn nhặt (status hết running/stopped). KHÔNG xoá dữ liệu SP đã lưu.</summary>
    [RelayCommand(CanExecute = nameof(HasResumePending))]
    private async Task DiscardPendingWork()
    {
        var n = _resumePending.Count;
        if (!await Dialogs.ConfirmAsync(
                $"Hủy {n} việc còn dở? Các việc này sẽ KHÔNG tự chạy tiếp nữa (tiến độ dở bị xoá; dữ liệu sản phẩm đã lưu vẫn còn).",
                "Hủy việc dở", DialogIcon.Warning))
            return;
        foreach (var item in _resumePending.ToList())   // ToList: Clear bắn Changed → recompute làm rỗng _resumePending
        {
            var acc = item.Acct.Account.Id;
            var sheet = item.Shop.ShopeeDataSheet ?? "";
            if (item.Op == AssignmentOps.Scrape) ScrapeProgressStore.Shared.Clear(acc, sheet);
            else                     OpProgressStore.Shared.Clear(acc, sheet, item.Op);
        }
        RecomputeResumePending();
    }
}

/// <summary>1 dòng hiển thị việc chạy-tay còn dở trong banner Workspace: nhãn op + tài khoản + shop + tiến độ
/// (scrape: dòng đã cào; import/update: số SP đã xong) + giờ chạy cuối. Chỉ để HIỂN THỊ — dựng lại mỗi lần
/// RecomputeResumePending; logic Tiếp tục/Hủy vẫn theo ResumeItem (giữ tham chiếu acc/shop để phóng đúng việc).</summary>
public sealed record ResumePendingRow(
    string OpLabel, string AccountName, string ShopName, string ProgressText, string LastRunText);
