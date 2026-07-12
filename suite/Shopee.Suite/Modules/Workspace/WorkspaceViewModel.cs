using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shopee.Core.BigSeller;
using Shopee.Core.Coordination;
using Shopee.Core.Progress;
using Shopee.Core.Scrape;
using Shopee.Suite.Modules.BigSeller;
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
    }

    private void OnProgressStoresChanged() => UiThread.Post(RecomputeResumePending);

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

        SelectedAccount = Accounts.FirstOrDefault(a => a.Account.Id == prevId) ?? Accounts.FirstOrDefault();
        Status = $"{Accounts.Count} tài khoản BigSeller.";
        RecomputeResumePending();   // list acc đổi → map lại các mục "còn dở" theo acc/shop hiện có
    }

    partial void OnSelectedAccountChanged(WorkspaceAccountViewModel? value)
    {
        // Đồng bộ cho Thống kê/Map chạy đúng tk đang xem. KHÔNG set BigSeller.Selected ở đây — nếu set, mỗi
        // lần kho BigSeller bắn Changed (vd sửa shop ở tab Cấu hình) sẽ kéo màn Cấu hình nhảy về tk Workspace
        // đang chọn. BigSeller.Selected chỉ set khi bấm "Đăng nhập / cấu hình" (GoToBigSellerConfig).
        Scrape.SelectedTarget = value?.ScrapeTarget;
        Update.SelectedTarget = value?.UpdateTarget;
        value?.RefreshAll();
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

    // ── Mở FILE log RIÊNG của acc đang chọn (buffer UI chỉ giữ 500 dòng cuối; file có đủ) ──
    [RelayCommand] private void OpenScrapeAccLog() { if (SelectedAccount is { } a) ShellOpener.RevealFile(a.ScrapeLog.FilePath); }
    [RelayCommand] private void OpenUpdateAccLog() { if (SelectedAccount is { } a) ShellOpener.RevealFile(a.UpdateLog.FilePath); }

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
        "scrape" => "Scrape", "import" => "Import", "update" => "Update", _ => op,
    };

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
        return fleet.Assignments.Any(a => Match(a) && a.Status is "queued" or "running") || fleet.Interrupted.Any(Match);
    }

    /// <summary>Đếm lại các việc chạy-tay còn dở: scrape (ScrapeProgressStore) + import/update (OpProgressStore)
    /// có status ∈ {running (kẹt do crash), stopped (dừng dở)}, map về acc/shop hiện có, loại việc hub quản +
    /// việc đang chạy thật lúc này. Gọi trên UI thread (đọc Accounts + set observable).</summary>
    private void RecomputeResumePending()
    {
        _resumePending.Clear();

        // Scrape: mỗi (acc, sheet) có tiến độ running/stopped → ứng viên tiếp tục dòng còn thiếu.
        foreach (var p in ScrapeProgressStore.Shared.All())
        {
            if (p.Status is not ("running" or "stopped")) continue;
            var acct = Accounts.FirstOrDefault(a => a.Account.Id == p.AccountId);
            var shop = acct?.Account.Shops.FirstOrDefault(s =>
                string.Equals(s.ShopeeDataSheet ?? "", p.Sheet ?? "", StringComparison.OrdinalIgnoreCase));
            if (acct is null || shop is null) continue;                               // acc/shop đã xoá → bỏ
            if (HubManages(p.AccountId, shop.Id, "scrape")) continue;                 // hub quản → bỏ
            if (acct.ScrapeTarget.IsShopRunning?.Invoke(shop) ?? false) continue;     // đang scrape thật → bỏ
            _resumePending.Add(new ResumeItem("scrape", acct, shop));
        }

        // Import/Update: tiến độ per-SP running/stopped → ứng viên.
        foreach (var (accId, sheet, op, status) in OpProgressStore.Shared.Snapshot())
        {
            if (op is not ("import" or "update")) continue;
            if (status is not ("running" or "stopped")) continue;
            var acct = Accounts.FirstOrDefault(a => a.Account.Id == accId);
            var shop = acct?.Account.Shops.FirstOrDefault(s =>
                string.Equals(s.ShopeeDataSheet ?? "", sheet ?? "", StringComparison.OrdinalIgnoreCase));
            if (acct is null || shop is null) continue;
            if (HubManages(accId, shop.Id, op)) continue;
            if (Update.IsUpdateRunning(accId)) continue;   // acc đang chạy 1 workflow update thật → bỏ
            _resumePending.Add(new ResumeItem(op, acct, shop));
        }

        OnPropertyChanged(nameof(ResumePendingCount));
        OnPropertyChanged(nameof(HasResumePending));
        OnPropertyChanged(nameof(ResumeButtonText));
        OnPropertyChanged(nameof(ResumeTooltip));
        ResumePendingWorkCommand.NotifyCanExecuteChanged();
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
                case "scrape":
                    item.Acct.ScrapeTarget.SelectedShop = item.Shop;
                    _ = Scrape.RunSingleAsync(item.Acct.ScrapeTarget, resume: true, silent: true);
                    break;
                case "import":
                    item.Acct.UpdateTarget.SelectedShop = item.Shop;
                    _ = Update.RunImportSingleAsync(item.Acct.UpdateTarget, silent: true);
                    break;
                case "update":
                    item.Acct.UpdateTarget.SelectedShop = item.Shop;
                    _ = Update.RunUpdateSingleAsync(item.Acct.UpdateTarget, silent: true);
                    break;
            }
        }
        RecomputeResumePending();
    }
}
