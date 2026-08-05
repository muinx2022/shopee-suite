using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shopee.Core.Accounts;
using Shopee.Core.BigSeller;
using Shopee.Core.Browser;
using Shopee.Core.Coordination;
using Shopee.Core.Infrastructure;
using Shopee.Core.Scrape;
using Shopee.Modules.MultiBrave;
using Shopee.Suite.Infrastructure;
using Shopee.Suite.Services;

namespace Shopee.Suite.Modules.Scrape;

/// <summary>
/// Module "Shopee Scrape". Tick chọn 1 hoặc NHIỀU tài khoản BigSeller (mỗi tk 1 shop ↔ sheet/workbook).
/// Hệ thống TỰ ĐỘNG dùng cả kho tài khoản Shopee (xoay vòng), chạy N process song song. Nhiều tk
/// BigSeller chạy SONG SONG: traffic bigseller.com đi qua proxy của instance Shopee (mỗi instance 1 IP)
/// nên phiên rải nhiều IP, không bị "nhiều token / 1 IP" → KHÔNG cần chạy lần lượt. Tk Shopee dính
/// captcha/proxy lỗi thì tự đổi tk khác.
/// <para>Chia làm 4 file cùng class (partial): file này = state + vòng đời phiên/job; <c>.AccountPool.cs</c> =
/// kho đóng khung tk Shopee; <c>.Session.cs</c> = RunSession/JobHandle; <c>.RunnerEvents.cs</c> = đấu event
/// runner ra lưới/log.</para>
/// </summary>
public sealed partial class ScrapeViewModel : ModuleViewModelBase
{
    public ObservableCollection<ScrapeTargetViewModel> ScrapeTargets { get; } = [];
    public ObservableCollection<ScrapeInstanceViewModel> Instances { get; } = [];
    public ObservableCollection<ErroredAccountRow> ErroredAccounts { get; } = [];

    [ObservableProperty] private string _videoDir = @"D:\videos";
    // Số acc Shopee đang bật (kho xoay vòng) — chỉ dùng nội bộ cho dòng Status, KHÔNG bind ra UI.
    private int _poolCount;

    /// <summary>Tk BigSeller đang click để xem/sửa config chi tiết (panel phải).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedTarget))]
    [NotifyCanExecuteChangedFor(nameof(ShowStatsCommand))]
    private ScrapeTargetViewModel? _selectedTarget;

    public bool HasSelectedTarget => SelectedTarget is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isBusy;

    public bool IsIdle => !IsBusy;

    // Phiên chạy hiện tại (sống khi IsBusy) — chứa pool tk Shopee dùng chung + registry job để
    // chạy/dừng RIÊNG từng tk giữa chừng. null = đang rảnh.
    private RunSession? _session;

    // Lý do job bị DỪNG vì lỗi hạ tầng TOÀN CỤC (key proxy chết), theo tk BigSeller — xem TakeJobFatal.
    // ConcurrentDictionary: ghi từ event runner (luồng nền), đọc từ vòng nhận việc của AssignmentWorker.
    private readonly ConcurrentDictionary<string, string> _jobFatal = new(StringComparer.Ordinal);

    // Bảng màu nền NHẠT phân biệt process theo tk BigSeller (mỗi job 1 màu, xoay vòng) — chạy nhiều tk dễ nhìn.
    private static readonly Brush[] JobPalette = BuildPalette();
    private static Brush[] BuildPalette()
    {
        string[] hex = { "#FFF6DA", "#E3F2FD", "#E8F5E9", "#FCE4EC", "#F3E5F5", "#FFF3E0", "#E0F7FA", "#F1F8E9" };
        var arr = new Brush[hex.Length];
        for (var i = 0; i < hex.Length; i++)
            arr[i] = AppBrushes.From(hex[i]);   // đã Freeze — dòng lưới dựng cả từ luồng nền
        return arr;
    }

    // Chiếu kho BigSeller → ScrapeTargets, giữ SelectedTarget theo Id (idiom Store.Changed→Reload gom vào
    // ObservableProjection). KHÔNG guard id-set: Reload còn tính lại PoolCount theo kho Shopee nên rebuild vô
    // điều kiện như cũ (bám cả AccountStore.Changed lẫn thay đổi shop trên acc sẵn có).
    private readonly ObservableProjection<BigSellerAccount, ScrapeTargetViewModel> _targets;

    public ScrapeViewModel() : base("workspace-scrape.log", "Shopee Scrape")
    {
        // Mỗi ScrapeTargetViewModel TỰ nạp config đã lưu (tick chọn + shop + số dòng/process) theo Account.Id
        // từ ScrapeTargetConfigStore → giữ nguyên lựa chọn người dùng qua reload + khởi động lại.
        _targets = new ObservableProjection<BigSellerAccount, ScrapeTargetViewModel>(
            ScrapeTargets, () => BigSellerStore.Shared.Accounts,
            a =>
            {
                var t = new ScrapeTargetViewModel(a);
                t.IsShopRunning = shop => IsShopScraping(t, shop);   // "đang scrape" theo job LIVE, không kẹt sau crash
                return t;
            },
            t => t.Account.Id, a => a.Id, () => SelectedTarget, v => SelectedTarget = v);
        Reload();
        AccountStore.Shared.Changed += OnStoresChanged;
        BigSellerStore.Shared.Changed += OnStoresChanged;
    }

    private void OnStoresChanged()
    {
        if (IsBusy) return;
        UiThread.Post(Reload);
    }

    [RelayCommand]
    private void Reload()
    {
        // Dựng lại ScrapeTargets từ kho BigSeller + giữ lựa chọn panel-chi-tiết (thuần UI) theo Id — projection lo.
        _targets.Rebuild();
        _poolCount = AccountStore.Shared.Accounts.Count(a => !a.Disabled);
        Status = $"{ScrapeTargets.Count} BigSeller · {_poolCount} acc Shopee (tự xoay vòng).";
    }

    /// <summary>v1.1 (màn gộp BigSeller): chạy/tiếp tục RIÊNG 1 tk BigSeller mà KHÔNG đụng tick của tk khác.
    /// Rảnh → mở phiên mới chỉ gồm tk này; đang chạy → thêm job tk này (resume) vào phiên hiện tại.
    /// TOTAL: tự nuốt + log mọi lỗi → an toàn để gọi fire-and-forget (caller không cần try/catch).</summary>
    public async Task RunSingleAsync(ScrapeTargetViewModel target, bool resume, bool silent = false,
        int? startRow = null, int? endRow = null, int? processes = null, int? frameSize = null)
    {
        try
        {
            // Override khoảng dòng + số cửa sổ + cỡ khung (Hub giao việc) — KHÔNG ghi đè cấu hình người dùng; runner đọc rồi tự xoá.
            target.PendingStartRow = startRow is int sr && sr > 0 ? sr : null;
            target.PendingEndRow = endRow is int er && er > 0 ? er : null;
            target.PendingMaxProcess = processes is int pp && pp > 0 ? pp : null;
            target.PendingFrameSize = frameSize is int fs && fs > 0 ? fs : null;
            if (IsBusy) { StartOneAccount(target, silent); return; }
            await StartAsync(resume, new[] { target }, silent);
        }
        catch (Exception ex) { LogAcc(target.Account.Id, target.Account.DisplayName, $"✖ Lỗi khởi động scrape: {ex.Message}"); }
    }

    /// <summary>v1.1 (màn gộp): DỪNG RIÊNG job của 1 tk BigSeller (các tk khác chạy tiếp). Không có job
    /// đang chạy thì bỏ qua. Giữ tiến độ cho lần Tiếp tục. TOTAL (xem RunSingleAsync).</summary>
    public async Task StopSingleAsync(ScrapeTargetViewModel target)
    {
        try { await StopOneAccount(target); }
        catch (Exception ex) { LogAcc(target.Account.Id, target.Account.DisplayName, $"✖ Lỗi dừng scrape: {ex.Message}"); }
    }

    private async Task StartAsync(bool resume, IReadOnlyList<ScrapeTargetViewModel>? only = null, bool silent = false)
    {
        // only != null (màn gộp v1.1): chạy RIÊNG danh sách được chỉ định, KHÔNG đụng tick của tk khác.
        var picked = (only ?? ScrapeTargets.Where(t => t.IsSelected)).ToList();
        if (picked.Count == 0) { Warn("Tick chọn ít nhất 1 tài khoản BigSeller.", silent); return; }

        var pool = AccountStore.Shared.Accounts.Where(a => !a.Disabled).ToList();
        if (pool.Count == 0) { Warn("Kho chưa có tài khoản Shopee (thêm ở mục Tài khoản & Proxy).", silent); return; }

        var sourceUserData = BrowserLauncher.DetectUserData(BrowserKind.Brave);
        if (sourceUserData is null)
        { Warn("Không tìm thấy User Data của Brave (profile Default). Hãy mở Brave ít nhất 1 lần.", silent); return; }

        // Validate từng đích (dùng config RIÊNG của từng tk). Đích lỗi bị bỏ qua, không chặn đích khác.
        var jobs = new List<ScrapeTargetViewModel>();
        var problems = new List<string>();
        foreach (var t in picked)
        {
            if (ValidateTarget(t, pool.Count, out var problem)) jobs.Add(t);
            else problems.Add(problem);
        }
        if (jobs.Count == 0) { Warn("Không có tài khoản hợp lệ để scrape.\n" + string.Join("\n", problems), silent); return; }

        // Kho tk Shopee = TẤT CẢ tk đang bật, DÙNG CHUNG cho mọi job BigSeller. Không pin tk vào BigSeller
        // nào nữa: mỗi khối mượn 1 tk nghỉ lâu nhất rồi trả về kho → các BigSeller chia sẻ + tk luân phiên nghỉ.
        var session = new RunSession { SourceUserData = sourceUserData };
        session.Available.AddRange(pool);
        // Seed bộ đếm vòng-LRU = mốc cao nhất đã lưu → cấp phát tiếp vòng, không nện lại tk đầu sau restart.
        session.LruTick = AccountStore.Shared.Accounts.Select(a => a.LastUsedTick).DefaultIfEmpty(0).Max();
        // State "đang chạy" + dọn dẹp ĐỐI XỨNG quanh MỘT try/finally: set _session/IsBusy/BeginRun là
        // việc ĐẦU TIÊN trong try, clear ở finally → dù setup (StartJob) HAY coordinator ném, IsBusy/_session/
        // usage-run KHÔNG kẹt (trước đây StartJob nằm NGOÀI try → throw ở đó làm IsBusy treo true vĩnh viễn).
        try
        {
            _session = session;
            IsBusy = true;
            ShopeeAccountUsage.Shared.BeginRun();   // bật theo dõi tình trạng tk (cột "Tình trạng")
            LogLines.Clear();
            Instances.Clear();
            ErroredAccounts.Clear();
            foreach (var p in problems) Log($"⚠ Bỏ qua {p}.");
            Log(resume
                ? $"⏯ Tiếp tục {jobs.Count} BigSeller — chỉ chạy phần dòng CÒN THIẾU. Kho {pool.Count} tk Shopee."
                : $"▶ Scrape {jobs.Count} BigSeller (RESET — chạy lại từ đầu). Kho {pool.Count} tk Shopee.");

            // Phóng job cho từng tk đã chọn (mỗi job = 1 token RIÊNG → dừng được lẻ giữa chừng).
            foreach (var t in jobs) StartJob(session, t, resume);

            // Coordinator: chờ tới khi registry rỗng. Cho phép thêm/bớt job ĐỘNG giữa chừng (StartOne/StopOne).
            while (true)
            {
                // Rỗng → CHỐT (chặn StartJob thêm) + trả [] ATOMIC dưới lock của registry → không job nào lọt sau khi thấy rỗng.
                var running = session.Jobs.SnapshotOrSeal(h => h.Task);
                if (running.Length == 0) break;
                // KHÔNG ConfigureAwait(false): giữ UI thread để finally set Status/IsBusy an toàn
                // (set ObservableProperty + NotifyCanExecuteChanged phải ở UI thread).
                await Task.WhenAny(running);
            }
        }
        catch (Exception ex)
        {
            // Lỗi BẤT NGỜ khi dựng/điều phối phiên → huỷ token để các job đã phóng dở tự dừng, rồi log
            // (KHÔNG ném ra ngoài: caller chạy fire-and-forget). Job tự bắt OCE trong RunOneJobAsync.
            session.MasterCts.Cancel();
            Log($"✖ Lỗi phiên scrape: {ex.Message}");
        }
        finally
        {
            Status = session.MasterCts.IsCancellationRequested ? "Đã dừng." : $"Hoàn tất {jobs.Count} tài khoản.";
            Log($"── {Status} ──");
            try { AccountStore.Shared.Save(); } catch { }   // lưu LastUsedTick (vòng-LRU) bền qua restart
            _session = null;                                 // null TRƯỚC khi Dispose để StartOne kịp bail
            session.MasterCts.Dispose();
            IsBusy = false;
            ShopeeAccountUsage.Shared.EndRun();             // hết lượt chạy → mọi tk về "Chưa dùng"
        }
    }

    private async Task RunOneJobAsync(RunSession s, JobHandle h, bool resume)
    {
        var target = h.Target;
        var account = target.Account;
        // Lượt chạy MỚI → xoá lý do "dừng vì lỗi hạ tầng" của lượt TRƯỚC (job chạy tay không ai lấy nên có thể
        // còn đọng) → việc Hub-giao lần này không bị kết luận bằng lý do cũ.
        _jobFatal.TryRemove(account.Id, out _);
        // Log gắn tk này → ghi vào CẢ buffer gộp lẫn buffer riêng của acc (tab log per-acc đợt sau bind vào).
        void LogA(string m) => LogAcc(account.Id, account.DisplayName, m);
        // Mỗi lượt chạy hiện log TƯƠI của acc → xoá phần XEM buffer riêng (file vẫn giữ đầy đủ).
        OnUi(() => AccountLogs.Get(account.Id, account.DisplayName).Clear());
        var shop = target.SelectedShop!;
        var sheet = shop.ShopeeDataSheet;
        // Override TẠM (Hub giao việc) cho khoảng dòng + số cửa sổ + cỡ khung — dùng-một-lần → đọc xong XOÁ HẾT ở
        // ĐÂY (đúng chỗ config được chốt cho job) để không lọt sang lượt chạy sau. null = dùng cấu hình người dùng.
        var maxProc = Math.Max(1, target.PendingMaxProcess ?? target.MaxProcess);   // = SỐ CỬA SỔ Brave song song (KHÔNG còn = số tk dùng)
        var startRow = Math.Max(1, target.PendingStartRow ?? target.StartRow);
        var endRowOverride = target.PendingEndRow;
        var frameSizeOverride = target.PendingFrameSize;
        target.PendingStartRow = null; target.PendingEndRow = null;
        target.PendingMaxProcess = null; target.PendingFrameSize = null;
        var rowsPer = Math.Max(1, target.RowsPerAccount);
        var seq = h.Seq;
        var ct = h.Cts.Token;

        ScrapeRunner? runner = null;
        var coordKey = new CoordKey(account.Id, shop.Id, sheet, CoordOp.Scrape);
        ILeaseHandle? lease = null;
        AccountLeaseScope? accScope = null;         // khóa tk Shopee xuyên máy: reserve→heartbeat→bù→nhả (gói)
        var accHub = CoordinationRuntime.Hub;       // null nếu chưa kết nối Hub (chạy như 1 máy)
        try
        {
            int totalRows;
            try
            {
                if (account.UsesHubData)
                {
                    // HUB-MODE: tổng dòng = Rows (tổng dền = MỌI dòng có thật) của sheet trên kho Hub — khớp
                    // TotalDataRows của Excel (chỉ-số-dồn tính trên mọi dòng, kể cả dòng thiếu link/tên) nên tiến độ
                    // scrape giữ nguyên khi acc excel→hub. Hub chưa kết nối/chưa sẵn sàng → ném → catch dưới ghi log
                    // rõ + bỏ qua (KHÔNG âm thầm về Excel).
                    var client = CoordinationRuntime.Client
                        ?? throw new InvalidOperationException("⛔ Tk ở chế độ kho Hub nhưng chưa kết nối Hub — kiểm tra Cài đặt → Hub.");
                    var sheets = await client.GetProductSheetsAsync(account.Id, ct).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("⛔ Hub chưa sẵn sàng (kho sản phẩm Postgres) — thử lại sau.");
                    totalRows = sheets.FirstOrDefault(x => string.Equals(x.Sheet, sheet, StringComparison.Ordinal))?.Rows ?? 0;
                }
                else
                {
                    totalRows = await Task.Run(() => ScrapeWorkbook.TotalDataRows(account.WorkbookPath, sheet), ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) { LogA($"[{account.DisplayName}] ✘ lỗi đọc {(account.UsesHubData ? "kho Hub" : "workbook")}: {ex.Message} — bỏ qua."); return; }
            // "Đến dòng" > 0 → DỪNG tại đó (cắt tổng số dòng cần chạy). Ưu tiên override TẠM (Hub giao).
            var endRow = Math.Max(0, endRowOverride ?? target.EndRow);
            if (endRow > 0 && endRow < totalRows) totalRows = endRow;
            if (totalRows < startRow) { LogA($"[{account.DisplayName}] sheet \"{sheet}\" chỉ có {totalRows} dòng (bắt đầu {startRow}) — bỏ qua."); return; }

            // RESET → xoá tiến độ cũ. Tính các khoảng cần chạy (reset = cả đoạn; resume = phần còn thiếu).
            if (!resume)
            {
                ScrapeProgressStore.Shared.Clear(account.Id, sheet);
                // XOÁ LOCAL CHƯA ĐỦ: ledger hub còn nguyên khoảng-dòng cũ → lượt Resume sau (hoặc mở lại app:
                // SyncIntoProgressAsync fold TOÀN BỘ ledger về tiến độ local) kéo lại tiến độ CŨ → resume tưởng
                // đã xong phần user vừa muốn cào lại từ đầu → BỎ SÓT dòng ("fold-poisoning"). Xoá luôn ledger hub
                // op scrape của shop này (status "idle" = server xoá bản ghi ledger + tiến độ dòng). Fire-and-forget,
                // có try/catch: offline/lỗi → thôi (local đã clear là đủ để chạy lại từ đầu).
                if (accHub is not null)
                    TaskExt.FireAndForget(accHub.SetLedgerStatusAsync(coordKey, LedgerStatus.Idle),
                        $"xoá ledger hub (idle) khi Reset · {account.DisplayName}/{sheet}");
            }
            // HAND-OFF XUYÊN MÁY: trước khi tính phần CÒN THIẾU, kéo ledger TƯƠI của shop này từ Hub → fold vào
            // tiến độ local. Nhờ đó máy TIẾP QUẢN (khi máy trước rớt net giữa chừng) chỉ scrape đúng phần còn
            // thiếu chung, KHÔNG làm lại phần máy kia đã đẩy lên Hub (trước đây chỉ fold 1 lần lúc mở app →
            // tiếp quản nóng bị scrape lại). Best-effort: offline/standalone → dùng tiến độ local như cũ.
            if (resume && accHub is not null)
                await accHub.FoldScrapeLedgerAsync(account.Id, sheet).ConfigureAwait(false);
            var segments = resume
                ? ScrapeProgressStore.Shared.RemainingSegments(account.Id, sheet, startRow, totalRows)
                : new List<(int from, int to)> { (startRow, totalRows) };
            if (segments.Count == 0)
            {
                LogA($"[{account.DisplayName}] ✓ Không còn dòng nào để chạy (đã xong tới {totalRows}). Thêm dòng mới rồi Tiếp tục.");
                return;
            }

            // KHOÁ VIỆC XUYÊN MÁY: giành quyền scrape shop này. Bị máy khác giữ / mất kết nối Hub → CHẶN.
            var attempt = await Coordination.Hub.AcquireAsync(coordKey, h.Force || CoordinationRuntime.ForceNextRun, ct).ConfigureAwait(false);
            if (!attempt.Granted)
            {
                LogA($"[{account.DisplayName}] ⛔ shop \"{shop.DisplayName}\" đang được máy \"{attempt.Result.BlockedByHostname}\" chạy (hoặc mất kết nối Hub) — bỏ qua. Bấm 'Chạy đè' nếu chắc máy kia đã dừng.");
                return;
            }
            lease = attempt.Handle;

            // ĐÓNG KHUNG: cấp một bộ tk Shopee CỐ ĐỊNH (FrameSize) cho job này, GỠ khỏi kho chung → các job
            // RỜI nhau. Resume giữ NGUYÊN khung cũ (đọc id đã lưu) để KHÔNG phơi tk MỚI lên BigSeller; Reset
            // cấp khung mới. Engine chỉ xoay vòng TRONG khung → BigSeller chỉ thấy ngần ấy thiết bị ổn định.
            var frameSize = Math.Max(1, frameSizeOverride ?? target.FrameSize);
            // AFFINITY tk↔máy: hỏi Hub tk nào "nhà" ở máy này (mine → ưu tiên, tái dùng profile trusted) và tk
            // nào đang thuộc máy KHÁC còn online (blocked → nhường, khỏi tranh trust). Chỉ khi có Hub; offline/
            // lỗi → rỗng → dựng khung như cũ. Lấy TRƯỚC ClaimFrame để đưa vào thứ tự ưu tiên.
            HashSet<string> mineIds = new(StringComparer.Ordinal), blockedIds = new(StringComparer.Ordinal);
            if (accHub is not null)
                (mineIds, blockedIds) = await accHub.GetAccountAffinityAsync().ConfigureAwait(false);
            IReadOnlyList<string>? preferIds = resume ? ScrapeProgressStore.Shared.GetFrame(account.Id, sheet) : null;
            var frame = s.ClaimFrame(frameSize, preferIds, mineIds, blockedIds);
            // Affinity thu hẹp khung (nhường tk máy khác còn online) → log rõ để user hiểu vì sao ít cửa sổ hơn.
            if (blockedIds.Count > 0 && frame.Count < frameSize)
                LogA($"[{account.DisplayName}] {blockedIds.Count} tk đang thuộc máy khác (còn online) → nhường, dùng tk của máy này/mồ côi; khung còn {frame.Count}/{frameSize}.");
            var frameIds = frame.Select(a => a.Id).ToList();
            // Gói account-lease: GIỮ giữ-chỗ cục bộ CẢ khung (ClaimFrame đã TryReserve) tới lúc Dispose nhả — kể cả
            // khi job dừng giữa chừng / Hub loại bớt tk (KHÔNG thu hẹp khung để tránh rò tk khỏi kho chung). Tạo
            // NGAY (kể cả offline) để mọi lối ra đều nhả giữ-chỗ cục bộ; reserve Hub + heartbeat + bù do scope lo.
            accScope = AccountLeaseScope.ForFrame(accHub, frameIds);
            if (frame.Count == 0) { LogA($"[{account.DisplayName}] kho tk Shopee đã cạn (mọi tk đang thuộc khung khác / Search đang giữ / bị tắt) — bỏ qua."); return; }

            // ACCOUNT-LEASE XUYÊN MÁY: tk nào đang được MÁY KHÁC dùng → loại khỏi khung (chống dùng trùng).
            if (accHub is not null)
            {
                var granted = await accScope.ReserveHubAsync(frameIds).ConfigureAwait(false);
                if (granted.Count < frameIds.Count)
                {
                    frame = frame.Where(a => granted.Contains(a.Id)).ToList();
                    LogA($"[{account.DisplayName}] {frameIds.Count - granted.Count} tk Shopee đang được máy khác dùng → loại, còn {frame.Count}.");
                }
                if (frame.Count == 0) { LogA($"[{account.DisplayName}] mọi tk trong khung đang được máy khác dùng — bỏ qua."); return; }
            }
            // AFFINITY: ghi "nhà" = máy này cho khung CUỐI (các tk máy này thực sự giữ, đã qua lease-grant) → lần
            // sau máy này ưu tiên chúng + máy khác tránh khi máy này còn online. Chỉ ghi tk đã lease-grant ⇒ không
            // ghi đè nhầm tk máy khác đang giữ. Best-effort (SetAccountHomeAsync tự nuốt lỗi).
            if (accHub is not null && frame.Count > 0)
                await accHub.SetAccountHomeAsync(frame.Select(a => a.Id)).ConfigureAwait(false);
            ScrapeProgressStore.Shared.SaveFrame(account.Id, sheet, frame.Select(a => a.Id));   // lưu khung để resume giữ nguyên
            var procs = Math.Max(1, Math.Min(maxProc, frame.Count));
            // Mỗi tk BigSeller (job) 1 màu nền → các process CÙNG tk BigSeller cùng màu, dễ nhìn khi chạy nhiều tk.
            var jobBrush = JobPalette[(seq - 1) % JobPalette.Length];
            OnUi(() =>
            {
                for (var i = 1; i <= procs; i++)
                    Instances.Add(new ScrapeInstanceViewModel($"{seq}:P{i}", $"[{account.DisplayName}] P{i}", jobBrush));
            });

            // Đưa thông báo dọn nền (quét Brave mồ côi…) ra log tab Scrape để người dùng thấy.
            Shopee.Core.Browser.BraveFleet.Notice = Log;
            var totalSeg = segments.Sum(x => x.to - x.from + 1);
            LogA($"── {(resume ? "⏯ Tiếp tục" : "▶")} BigSeller \"{account.DisplayName}\" · shop \"{shop.DisplayName}\" · {totalSeg} dòng cần chạy (tổng sheet {totalRows}) · {procs} cửa sổ · KHUNG {frame.Count} tk Shopee (xoay vòng trong khung) · trần tổng app {Shopee.Core.Browser.BraveFleet.MaxConcurrentWindows} cửa sổ ──");

            // Ghi nhận lượt chạy (chỉ tiến độ DÒNG — không đặt-chỗ tk nữa vì tk xoay vòng/tự trả về kho).
            if (resume) ScrapeProgressStore.Shared.BeginResume(account.Id, sheet, account.DisplayName, totalRows);
            else ScrapeProgressStore.Shared.BeginFresh(account.Id, sheet, account.DisplayName, totalRows);
            OnUi(target.RefreshProgress);

            runner = new ScrapeRunner(account.WorkbookPath, VideoDir, braveExe: null, s.SourceUserData, bigSellerAccountName: account.DisplayName,
                bigSellerAccountId: account.Id, useHubData: account.UsesHubData);   // hub-mode: engine nạp link từ kho Hub
            s.Jobs.TryUpdate(account.Id, j => j.Runner = runner);   // gán Runner DƯỚI lock (vs snapshot ở Stop)
            // Mỗi chunk xong → lưu tiến độ ngay (bền với dừng/treo).
            runner.RowsCompleted += (from, to) =>
            {
                ScrapeProgressStore.Shared.MarkCompleted(account.Id, sheet, from, to);
                Coordination.Hub.PublishProgress(coordKey, from, to);     // chia sẻ tiến độ lên Hub
                OnUi(target.RefreshProgress);
            };
            WireRunner(runner, seq, account);

            // BÙ TK THAY THẾ: khi captcha loại tk khỏi khung, pool xin 1 tk RẢNH từ kho chung (đã khóa lease
            // xuyên máy) để giữ đủ cỡ khung → job KHÔNG cạn khung phải chạy lại. Scope ghi nhận để NHẢ đúng ở
            // finally (giữ-chỗ cục bộ + lease Hub + heartbeat như tk khung ban đầu). Hết tk dư → pool giữ hành vi cũ.
            var pool = new SessionAccountPool(sheet, frame, accScope.AcquireReplacementAsync, msg => LogA($"[{account.DisplayName}] {msg}"));
            await runner.RunAutoAsync(pool, procs, segments, rowsPer, account.CookieFile, ct).ConfigureAwait(false);

            // Kết thúc: xong hết [startRow..total] → completed; còn dở → stopped (resume chạy nốt theo dòng).
            ScrapeProgressStore.Shared.FinishRun(account.Id, sheet, startRow, totalRows);
            var after = ScrapeProgressStore.Shared.Find(account.Id, sheet);
            LogA(string.Equals(after?.Status, LedgerStatus.Completed, StringComparison.OrdinalIgnoreCase)
                ? $"[{account.DisplayName}] ✔ Hoàn thành toàn bộ."
                : $"[{account.DisplayName}] ■ Chưa xong (xong tới dòng {after?.LastRowReached ?? 0}) — Tiếp tục để chạy nốt.");
        }
        catch (OperationCanceledException)
        {
            LogA($"[{account.DisplayName}] ■ đã dừng — giữ tiến độ cho lần Tiếp tục.");
            try { ScrapeProgressStore.Shared.FinishRun(account.Id, sheet, startRow, 0); } catch { }
        }
        catch (Exception ex) { LogA($"[{account.DisplayName}] ✘ lỗi: {ex.Message}"); }
        finally
        {
            // Hub: đẩy trạng thái hoàn thành lên ledger + nhả khoá việc + nhả account-lease xuyên máy.
            try
            {
                var fin = ScrapeProgressStore.Shared.Find(account.Id, sheet);
                Coordination.Hub.PublishCompletion(coordKey, fin?.Status ?? LedgerStatus.Stopped, fin?.LastRowReached ?? 0);
            }
            catch { }
            // Nhả account-lease (heartbeat → UnmarkHubLeased → ReleaseAccountsAsync Hub → ReleaseReservation CẢ
            // khung + tk bù, snapshot-under-lock chống rò) TRƯỚC, rồi nhả khoá VIỆC shop. Trước đây khoá việc nhả
            // xen giữa nhả-lease-Hub và nhả-giữ-chỗ-cục-bộ; 3 tài nguyên độc lập nên thứ tự này tương đương.
            if (accScope is not null) { try { await accScope.DisposeAsync().ConfigureAwait(false); } catch { } }
            if (lease is not null) { try { await lease.DisposeAsync().ConfigureAwait(false); } catch { } }
            s.Jobs.Remove(account.Id);
            // Dọn các dòng process của job này khỏi lưới (trước đây dòng cũ không bao giờ bị xoá).
            var prefix = seq + ":";
            OnUi(() => { for (var i = Instances.Count - 1; i >= 0; i--) if (Instances[i].Key.StartsWith(prefix, StringComparison.Ordinal)) Instances.RemoveAt(i); });
            OnUi(target.RefreshProgress);
            h.Cts.Dispose();
        }
    }

    /// <summary>Tạo + phóng 1 job cho tk BigSeller. Factory CHẠY DƯỚI lock của registry → gán Task xong mới
    /// vào sổ (coordinator không thấy Task rỗng) và Remove của body phải chờ → "add trước, remove sau".
    /// Trả false nếu phiên đang kết thúc (sổ đã chốt) hoặc tk đó đã có job đang chạy.</summary>
    private bool StartJob(RunSession s, ScrapeTargetViewModel target, bool resume, bool force = false)
    {
        var started = s.Jobs.TryAdd(target.Account.Id, () =>
        {
            var h = new JobHandle
            {
                Target = target,
                Seq = Interlocked.Increment(ref s.JobSeq),
                Cts = CancellationTokenSource.CreateLinkedTokenSource(s.MasterCts.Token),
                Force = force,
            };
            // KHÔNG truyền token vào Task.Run: nếu token đã huỷ lúc lên lịch, body (và finally dọn dẹp)
            // sẽ KHÔNG chạy → job kẹt trong registry → coordinator lặp vô hạn. Body tự kiểm token + bắt OCE.
            h.Task = Task.Run(() => RunOneJobAsync(s, h, resume));
            return h;
        });
        if (!started) return false;
        target.RefreshProgress();   // shop vừa chạy chuyển sang "đang scrape" ngay (chip phía trên ô chọn shop)
        return true;
    }

    /// <summary>true nếu đang có job LIVE cào đúng shop (sheet) này của tk BigSeller → chip hiện "đang scrape".</summary>
    private bool IsShopScraping(ScrapeTargetViewModel target, BigSellerShop shop)
    {
        var s = _session;
        if (s is null) return false;
        if (!s.Jobs.TryGet(target.Account.Id, out var h)) return false;
        return string.Equals(
            h.Target.SelectedShop?.ShopeeDataSheet, shop.ShopeeDataSheet, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Chạy RIÊNG 1 tk giữa lúc đang run (tick checkbox khi busy). Mid-run = RESUME.
    /// Trả true nếu đã phóng job.</summary>
    private bool StartOneAccount(ScrapeTargetViewModel target, bool silent = false)
    {
        var s = _session;
        if (s is null || s.MasterCts.IsCancellationRequested) return false;
        var poolCount = AccountStore.Shared.Accounts.Count(a => !a.Disabled);
        if (!ValidateTarget(target, poolCount, out var problem)) { Warn($"Không chạy được: {problem}", silent); return false; }
        // Kho tk Shopee dùng chung → acc thêm giữa chừng chỉ việc mượn từ kho như mọi job khác (không cần
        // đòi lại tk đặt-chỗ vì không còn pin tk vào BigSeller nào).
        if (StartJob(s, target, resume: true))
        {
            LogAcc(target.Account.Id, target.DisplayName, $"➕ [{target.DisplayName}] đã thêm vào lượt chạy (tiếp tục phần còn thiếu)…");
            return true;
        }
        LogAcc(target.Account.Id, target.DisplayName, $"[{target.DisplayName}] đang chạy rồi — bỏ qua.");
        return false;
    }

    /// <summary>Dừng RIÊNG 1 tk (untick khi busy): huỷ token + đóng Brave của RIÊNG runner đó; tk khác chạy tiếp.</summary>
    private async Task StopOneAccount(ScrapeTargetViewModel target)
    {
        var s = _session;
        if (s is null) return;
        if (!s.Jobs.TryGet(target.Account.Id, out var h)) return;
        h.Cts.Cancel();                                   // bẻ gãy chờ mượn tk / RunChunk
        var runner = h.Runner;
        if (runner is not null) { try { await runner.StopAllAsync(); } catch { } }
        // finally của job tự dọn: xoá khỏi Jobs, xoá dòng lưới, FinishRun(...,0)=giữ tiến độ cho Tiếp tục.
        // (tk Shopee đang mượn dở được worker trả về kho khi RunChunk bị huỷ.)
    }

    /// <summary>Click 1 dòng tiến trình → đưa cửa sổ Brave của process đó lên trước toàn bộ.
    /// Key lưới = "{seq}:P{slot}" → tìm job theo seq → runner.BringInstanceToFront("P{slot}").</summary>
    public void BringInstanceToFront(ScrapeInstanceViewModel inst)
    {
        var s = _session;
        if (s is null) return;
        var key = inst.Key;
        var idx = key.IndexOf(':');
        if (idx <= 0 || !int.TryParse(key[..idx], out var seq)) return;
        var slotKey = key[(idx + 1)..];   // "P{slot}"
        var h = s.Jobs.SnapshotSelect(j => j).FirstOrDefault(j => j.Seq == seq);
        h?.Runner?.BringInstanceToFront(slotKey);
    }

    /// <summary>Kiểm tra 1 đích Scrape có hợp lệ để chạy không (shop/cookie/sheet/workbook/đủ tk).</summary>
    private static bool ValidateTarget(ScrapeTargetViewModel t, int poolCount, out string problem)
    {
        var a = t.Account; var s = t.SelectedShop;
        if (s is null) { problem = $"{a.DisplayName}: chưa chọn shop"; return false; }
        if (!a.HasCookie) { problem = $"{a.DisplayName}: chưa có cookie BigSeller (đăng nhập ở mục BigSeller)"; return false; }
        if (string.IsNullOrWhiteSpace(s.ShopeeDataSheet)) { problem = $"{a.DisplayName}/{s.DisplayName}: shop chưa gán sheet"; return false; }
        if (string.IsNullOrWhiteSpace(a.WorkbookPath) || !File.Exists(a.WorkbookPath)) { problem = $"{a.DisplayName}: workbook không tồn tại"; return false; }
        if (Math.Max(1, t.MaxProcess) > poolCount) { problem = $"{a.DisplayName}: cần {t.MaxProcess} tk Shopee nhưng kho chỉ có {poolCount}"; return false; }
        problem = ""; return true;
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private async Task Stop()
    {
        var s = _session;
        if (s is null) return;
        s.MasterCts.Cancel();   // mọi token job (linked) huỷ theo
        Status = "Đang dừng…";
        // Đọc .Runner DƯỚI lock (qua select của SnapshotSelect) rồi lọc null + StopAll ngoài lock.
        var snapshot = s.Jobs.SnapshotSelect(h => h.Runner).Where(r => r is not null).Select(r => r!).ToList();
        foreach (var r in snapshot)
        {
            try { await r.StopAllAsync(); } catch { }
        }
        // KHÔNG null _session ở đây — coordinator finally lo (sau khi job drain xong).
    }

    /// <summary>Mở cửa sổ Thống kê của tk BigSeller đang chọn: tiến độ theo sheet, dòng đã xong, xoá tiến độ.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedTarget))]
    private async Task ShowStatsAsync()
    {
        var sel = SelectedTarget;
        if (sel is null) return;
        var vm = new ScrapeStatsViewModel(sel.Account.Id, sel.Account.DisplayName);
        await WindowHost.ShowDialogAsync(new ScrapeStatsWindow(vm));
        sel.RefreshProgress();   // có thể đã nhả tay / xoá tiến độ → cập nhật nhãn.
    }

    /// <summary>LẤY-RỒI-XOÁ lý do job của tk BigSeller này bị DỪNG vì lỗi hạ tầng TOÀN CỤC (key proxy chết);
    /// null = không có. Cho <c>AssignmentWorker</c> kết luận việc Hub-giao là 'failed' kèm lý do người-đọc-được
    /// thay vì "stopped" trống nghĩa. Lấy-rồi-xoá (như <c>TakeAssignmentOutcome</c> của Search) để lý do chỉ
    /// dùng cho ĐÚNG một lần kết luận.</summary>
    public string? TakeJobFatal(string bigSellerAccountId) =>
        _jobFatal.TryRemove(bigSellerAccountId, out var reason) ? reason : null;

    /// <summary>Tiền-kiểm điều kiện scrape 1 đích (kho tk Shopee, Brave, cấu hình) — KHÔNG mở dialog.
    /// Cho <c>AssignmentWorker</c> kiểm TRƯỚC khi chạy để khỏi modal + khỏi kẹt việc 'running'.</summary>
    public bool CanDispatchScrape(ScrapeTargetViewModel target, out string problem)
    {
        var pool = AccountStore.Shared.Accounts.Count(a => !a.Disabled);
        if (pool == 0) { problem = "kho tài khoản Shopee trống"; return false; }
        if (BrowserLauncher.DetectUserData(BrowserKind.Brave) is null) { problem = "không tìm thấy User Data Brave"; return false; }
        return ValidateTarget(target, pool, out problem);
    }
}
