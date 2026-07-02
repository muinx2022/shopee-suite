using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shopee.Core.Accounts;
using Shopee.Core.BigSeller;
using Shopee.Core.Browser;
using Shopee.Core.Coordination;
using Shopee.Core.Infrastructure;
using Shopee.Core.Proxy;
using Shopee.Core.Scrape;
using Shopee.Modules.MultiBrave;
using Shopee.Suite.Infrastructure;

namespace Shopee.Suite.Modules.Scrape;

/// <summary>
/// Module "Shopee Scrape". Tick chọn 1 hoặc NHIỀU tài khoản BigSeller (mỗi tk 1 shop ↔ sheet/workbook).
/// Hệ thống TỰ ĐỘNG dùng cả kho tài khoản Shopee (xoay vòng), chạy N process song song. Nhiều tk
/// BigSeller chạy SONG SONG: traffic bigseller.com đi qua proxy của instance Shopee (mỗi instance 1 IP)
/// nên phiên rải nhiều IP, không bị "nhiều token / 1 IP" → KHÔNG cần chạy lần lượt. Tk Shopee dính
/// captcha/proxy lỗi thì tự đổi tk khác.
/// </summary>
public sealed partial class ScrapeViewModel : ObservableObject
{
    public ObservableCollection<ScrapeTargetViewModel> ScrapeTargets { get; } = [];
    public ObservableCollection<ScrapeInstanceViewModel> Instances { get; } = [];
    public ObservableCollection<ErroredAccountRow> ErroredAccounts { get; } = [];
    public ObservableCollection<string> LogLines { get; } = [];

    [ObservableProperty] private string _videoDir = @"D:\videos";
    [ObservableProperty] private string _status = "Sẵn sàng.";
    [ObservableProperty] private int _poolCount;

    /// <summary>Tk BigSeller đang click để xem/sửa config chi tiết (panel phải).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedTarget))]
    [NotifyCanExecuteChangedFor(nameof(ShowStatsCommand))]
    private ScrapeTargetViewModel? _selectedTarget;

    public bool HasSelectedTarget => SelectedTarget is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(ResumeCommand), nameof(StopCommand))]
    private bool _isBusy;

    public bool IsIdle => !IsBusy;

    // Phiên chạy hiện tại (sống khi IsBusy) — chứa pool tk Shopee dùng chung + registry job để
    // chạy/dừng RIÊNG từng tk giữa chừng. null = đang rảnh.
    private RunSession? _session;

    // Bảng màu nền NHẠT phân biệt process theo tk BigSeller (mỗi job 1 màu, xoay vòng) — chạy nhiều tk dễ nhìn.
    private static readonly Brush[] JobPalette = BuildPalette();
    private static Brush[] BuildPalette()
    {
        string[] hex = { "#FFF6DA", "#E3F2FD", "#E8F5E9", "#FCE4EC", "#F3E5F5", "#FFF3E0", "#E0F7FA", "#F1F8E9" };
        var arr = new Brush[hex.Length];
        for (var i = 0; i < hex.Length; i++)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex[i])!);
            b.Freeze();
            arr[i] = b;
        }
        return arr;
    }

    public ScrapeViewModel()
    {
        Reload();
        AccountStore.Shared.Changed += OnStoresChanged;
        BigSellerStore.Shared.Changed += OnStoresChanged;
    }

    private void OnStoresChanged()
    {
        if (IsBusy) return;
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) Reload();
        else d.BeginInvoke(Reload);
    }

    [RelayCommand]
    private void Reload()
    {
        // Tk đang xem ở panel chi tiết — chỉ là trạng thái UI (không lưu), giữ lại qua reload cho mượt.
        var prevDetailId = SelectedTarget?.Account.Id;

        ScrapeTargets.Clear();
        // Mỗi ScrapeTargetViewModel TỰ nạp config đã lưu (tick chọn + shop + số dòng/process) theo
        // Account.Id từ ScrapeTargetConfigStore → giữ nguyên lựa chọn người dùng qua reload + khởi động lại.
        foreach (var a in BigSellerStore.Shared.Accounts)
        {
            var t = new ScrapeTargetViewModel(a);
            t.IsShopRunning = shop => IsShopScraping(t, shop);   // "đang scrape" theo job LIVE, không kẹt sau crash
            ScrapeTargets.Add(t);
        }
        SelectedTarget = ScrapeTargets.FirstOrDefault(t => t.Account.Id == prevDetailId) ?? ScrapeTargets.FirstOrDefault();
        PoolCount = AccountStore.Shared.Accounts.Count(a => !a.Disabled);
        Status = $"{ScrapeTargets.Count} BigSeller · {PoolCount} acc Shopee (tự xoay vòng).";
    }

    [RelayCommand]
    private void SelectAllTargets() { foreach (var t in ScrapeTargets) t.IsSelected = true; }

    [RelayCommand]
    private void UnselectAllTargets() { foreach (var t in ScrapeTargets) t.IsSelected = false; }

    /// <summary>Chạy = RESET: xoá tiến độ đã lưu, chạy lại từ "Từ dòng".</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task Run() => StartAsync(resume: false);

    /// <summary>Tiếp tục = RESUME: chỉ chạy các dòng CÒN THIẾU theo tiến độ đã lưu.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task Resume() => StartAsync(resume: true);

    /// <summary>v1.1 (màn gộp BigSeller): chạy/tiếp tục RIÊNG 1 tk BigSeller mà KHÔNG đụng tick của tk khác.
    /// Rảnh → mở phiên mới chỉ gồm tk này; đang chạy → thêm job tk này (resume) vào phiên hiện tại.
    /// TOTAL: tự nuốt + log mọi lỗi → an toàn để gọi fire-and-forget (caller không cần try/catch).</summary>
    public async Task RunSingleAsync(ScrapeTargetViewModel target, bool resume, bool silent = false,
        int? startRow = null, int? endRow = null)
    {
        try
        {
            // Override khoảng dòng (Hub giao việc) — KHÔNG ghi đè cấu hình người dùng; runner đọc rồi tự xoá.
            target.PendingStartRow = startRow is int sr && sr > 0 ? sr : null;
            target.PendingEndRow = endRow is int er && er > 0 ? er : null;
            if (IsBusy) { StartOneAccount(target, silent); return; }
            await StartAsync(resume, new[] { target }, silent);
        }
        catch (Exception ex) { Log($"✖ Lỗi khởi động scrape: {ex.Message}"); }
    }

    /// <summary>v1.1 (màn gộp): DỪNG RIÊNG job của 1 tk BigSeller (các tk khác chạy tiếp). Không có job
    /// đang chạy thì bỏ qua. Giữ tiến độ cho lần Tiếp tục. TOTAL (xem RunSingleAsync).</summary>
    public async Task StopSingleAsync(ScrapeTargetViewModel target)
    {
        try { await StopOneAccount(target); }
        catch (Exception ex) { Log($"✖ Lỗi dừng scrape: {ex.Message}"); }
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
        var session = new RunSession { SourceUserData = sourceUserData, Resume = resume };
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

    // KHO ĐÓNG KHUNG cho 1 job BigSeller: nhận MỘT khung tk Shopee CỐ ĐỊNH (cấp lúc start, đã gỡ khỏi kho
    // chung nên các job RỜI nhau), CHỈ xoay vòng TRONG khung → BigSeller chỉ thấy ngần ấy thiết bị ổn định,
    // tái dùng profile bền (import 1 lần) → KHÔNG churn → không bị đá phiên. Captcha → LOẠI khỏi khung,
    // KHÔNG bù tk mới; hết tk trong khung → BorrowAsync trả null → worker dừng → "hết tk → dừng job".
    private sealed class SessionAccountPool : IScrapeAccountPool
    {
        private readonly string _sheet;
        private readonly object _lock = new();
        private readonly List<ShopeeAccount> _frame;
        private readonly HashSet<string> _borrowed = new(StringComparer.Ordinal);
        private readonly HashSet<string> _dropped = new(StringComparer.Ordinal);   // captcha → loại khỏi khung
        private readonly Dictionary<string, DateTimeOffset> _cooldown = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _fail = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _captcha = new(StringComparer.Ordinal);   // số lần captcha (2 → loại)
        private long _lru;
        private const int ShortCdSec = 15, LongCdSec = 90, SetAsideAfter = 2;

        // BÙ TK THAY THẾ: khi captcha loại tk khỏi khung, giữ cỡ khung bằng cách xin 1 tk RẢNH từ kho chung
        // (khóa lease xuyên máy) → job không cạn khung phải chạy lại. null = không bù (giữ hành vi cũ).
        private readonly int _targetSize;
        private readonly Func<IReadOnlyCollection<string>, CancellationToken, Task<ShopeeAccount?>>? _acquireReplacement;
        private readonly Action<string>? _log;
        private readonly SemaphoreSlim _topUpGate = new(1, 1);
        private long _noReplUntilTick;   // Environment.TickCount64: backoff sau khi kho hết tk dư (khỏi dội Hub)

        public SessionAccountPool(string sheet, IEnumerable<ShopeeAccount> frame,
            Func<IReadOnlyCollection<string>, CancellationToken, Task<ShopeeAccount?>>? acquireReplacement = null,
            Action<string>? log = null)
        {
            _sheet = sheet;
            _frame = frame.ToList();
            _lru = _frame.Select(a => a.LastUsedTick).DefaultIfEmpty(0).Max();
            _targetSize = _frame.Count;   // cỡ khung ban đầu (đã trừ tk máy khác giữ) — mốc để bù cho đủ
            _acquireReplacement = acquireReplacement;
            _log = log;
        }

        public async Task<ScrapeAccountSpec?> BorrowAsync(CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int usable, borrowedCount;
                lock (_lock)
                {
                    var now = DateTimeOffset.UtcNow;
                    var pick = _frame
                        .Where(a => !a.Disabled && !_dropped.Contains(a.Id) && !_borrowed.Contains(a.Id)
                                    && (!_cooldown.TryGetValue(a.Id, out var until) || until <= now))
                        .OrderBy(a => a.LastUsedTick)        // nghỉ lâu nhất trong khung trước → luân phiên nghỉ
                        .FirstOrDefault();
                    if (pick is not null)
                    {
                        _borrowed.Add(pick.Id);
                        _cooldown.Remove(pick.Id);
                        ShopeeAccountUsage.Shared.MarkInUse(pick.Id);
                        return ToSpec(pick, _sheet);
                    }
                    usable = _frame.Count(a => !a.Disabled && !_dropped.Contains(a.Id));
                    borrowedCount = _borrowed.Count;
                }
                // Khung THIẾU so với cỡ ban đầu (captcha loại tk) → xin 1 tk THAY THẾ từ kho (khóa lease xuyên máy).
                // Có backoff sau khi kho hết tk dư để khỏi dội Hub liên tục.
                if (usable < _targetSize && Environment.TickCount64 >= Interlocked.Read(ref _noReplUntilTick))
                {
                    if (await TryTopUpAsync(ct).ConfigureAwait(false)) continue;   // đã bù → vòng lại mượn tk mới
                    Interlocked.Exchange(ref _noReplUntilTick, Environment.TickCount64 + 20_000);   // hết tk dư → nghỉ 20s
                }
                // Không có tk dùng được NGAY + không bù được: hết hẳn (mọi tk Disabled/loại) + không ai đang mượn
                // → null → worker dừng (hết tk → dừng job). Còn tk đang mượn/cooldown → chờ.
                if (usable == 0 && borrowedCount == 0) return null;
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }

        /// <summary>Xin 1 tk THAY THẾ từ kho chung (đã khóa lease) và thêm vào khung để giữ đủ cỡ. true = đã bù
        /// (hoặc khung đã đủ do worker khác vừa bù); false = kho hết tk dư. Nối tiếp 1-tại-1-thời-điểm.</summary>
        private async Task<bool> TryTopUpAsync(CancellationToken ct)
        {
            if (_acquireReplacement is null) return false;
            await _topUpGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                IReadOnlyCollection<string> frameIds;
                lock (_lock)
                {
                    if (_frame.Count(a => !a.Disabled && !_dropped.Contains(a.Id)) >= _targetSize)
                        return true;   // worker khác vừa bù đủ → coi như thành công, vòng lại mượn
                    frameIds = _frame.Select(a => a.Id).ToArray();
                }
                var repl = await _acquireReplacement(frameIds, ct).ConfigureAwait(false);
                if (repl is null) return false;
                lock (_lock) _frame.Add(repl);
                _log?.Invoke($"🔁 bù 1 tk thay thế \"{repl.DisplayName}\" vào khung (giữ đủ cỡ, khỏi cạn phải chạy lại).");
                return true;
            }
            finally { _topUpGate.Release(); }
        }

        public void Release(ScrapeAccountSpec spec)
        {
            lock (_lock)
            {
                if (Find(spec.Id) is { } a) a.LastUsedTick = ++_lru;
                _fail.Remove(spec.Id);
                _cooldown.Remove(spec.Id);
                _borrowed.Remove(spec.Id);
            }
            ShopeeAccountUsage.Shared.MarkReleased(spec.Id);
        }

        public AccountCooldown Cooldown(ScrapeAccountSpec spec)
        {
            int secs; bool setAside;
            lock (_lock)
            {
                var n = _fail[spec.Id] = _fail.GetValueOrDefault(spec.Id) + 1;
                setAside = n >= SetAsideAfter;
                secs = setAside ? LongCdSec : ShortCdSec;
                _cooldown[spec.Id] = DateTimeOffset.UtcNow.AddSeconds(secs);
                if (Find(spec.Id) is { } a) a.LastUsedTick = ++_lru;
                _borrowed.Remove(spec.Id);
            }
            ShopeeAccountUsage.Shared.MarkReleased(spec.Id);
            return new AccountCooldown(secs, setAside);
        }

        public bool CaptchaGrace(ScrapeAccountSpec spec)
        {
            // Captcha → CHỜ 3' (giải tay) rồi mới loại. Lần đầu: cho nghỉ 3' (vẫn trong khung) → thử lại
            // sau, kịp giải tay. Còn captcha lần nữa → LOẠI khỏi khung (trả true) + đánh dấu lỗi. Không bù tk.
            lock (_lock)
            {
                var n = _captcha[spec.Id] = _captcha.GetValueOrDefault(spec.Id) + 1;
                _borrowed.Remove(spec.Id);
                if (n >= 2)
                {
                    _dropped.Add(spec.Id);
                    _cooldown.Remove(spec.Id);
                    ShopeeAccountUsage.Shared.MarkReleased(spec.Id);
                    return true;
                }
                _cooldown[spec.Id] = DateTimeOffset.UtcNow.AddMinutes(3);   // chờ 3' rồi thử lại (giải tay)
            }
            ShopeeAccountUsage.Shared.MarkReleased(spec.Id);
            return false;
        }

        public void Quarantine(ScrapeAccountSpec spec)
        {
            // Captcha → LOẠI tk khỏi khung (KHÔNG bù tk mới). Hết tk → BorrowAsync null → dừng job.
            lock (_lock)
            {
                _dropped.Add(spec.Id);
                _borrowed.Remove(spec.Id);
                _cooldown.Remove(spec.Id);
            }
            ShopeeAccountUsage.Shared.MarkReleased(spec.Id);
        }

        private ShopeeAccount? Find(string id) => _frame.FirstOrDefault(a => a.Id == id);
    }

    private async Task RunOneJobAsync(RunSession s, JobHandle h, bool resume)
    {
        var target = h.Target;
        var account = target.Account;
        var shop = target.SelectedShop!;
        var sheet = shop.ShopeeDataSheet;
        var maxProc = Math.Max(1, target.MaxProcess);   // = SỐ CỬA SỔ Brave song song (KHÔNG còn = số tk dùng)
        // Khoảng dòng: ưu tiên override TẠM (Hub giao việc), dùng-một-lần → đọc xong xoá để không lọt sang lượt sau.
        var startRow = Math.Max(1, target.PendingStartRow ?? target.StartRow);
        var endRowOverride = target.PendingEndRow;
        target.PendingStartRow = null; target.PendingEndRow = null;
        var rowsPer = Math.Max(1, target.RowsPerAccount);
        var seq = h.Seq;
        var ct = h.Cts.Token;

        ScrapeRunner? runner = null;
        var claimedFrameIds = new List<string>();   // CẢ khung đã giữ chỗ cục bộ → nhả TOÀN BỘ ở finally
        var hubReservedIds = new List<string>();    // tk được Hub cấp lease (khung ban đầu + tk bù) → nhả/heartbeat trên Hub
        var reservedLock = new object();            // 2 list trên bị pool BÙ TK thêm vào từ luồng khác → khóa khi đọc/ghi
        var coordKey = new CoordKey(account.Id, shop.Id, sheet, CoordOp.Scrape);
        ILeaseHandle? lease = null;
        Timer? accHeartbeat = null;                 // nhịp account-lease nền (chống hết hạn giữa chunk dài)
        var accHub = CoordinationRuntime.Hub;       // null nếu chưa kết nối Hub (chạy như 1 máy)
        try
        {
            int totalRows;
            try { totalRows = await Task.Run(() => ScrapeWorkbook.TotalDataRows(account.WorkbookPath, sheet), ct).ConfigureAwait(false); }
            catch (Exception ex) { Log($"[{account.DisplayName}] ✘ lỗi đọc workbook: {ex.Message} — bỏ qua."); return; }
            // "Đến dòng" > 0 → DỪNG tại đó (cắt tổng số dòng cần chạy). Ưu tiên override TẠM (Hub giao).
            var endRow = Math.Max(0, endRowOverride ?? target.EndRow);
            if (endRow > 0 && endRow < totalRows) totalRows = endRow;
            if (totalRows < startRow) { Log($"[{account.DisplayName}] sheet \"{sheet}\" chỉ có {totalRows} dòng (bắt đầu {startRow}) — bỏ qua."); return; }

            // RESET → xoá tiến độ cũ. Tính các khoảng cần chạy (reset = cả đoạn; resume = phần còn thiếu).
            if (!resume) ScrapeProgressStore.Shared.Clear(account.Id, sheet);
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
                Log($"[{account.DisplayName}] ✓ Không còn dòng nào để chạy (đã xong tới {totalRows}). Thêm dòng mới rồi Tiếp tục.");
                return;
            }

            // KHOÁ VIỆC XUYÊN MÁY: giành quyền scrape shop này. Bị máy khác giữ / mất kết nối Hub → CHẶN.
            var attempt = await Coordination.Hub.AcquireAsync(coordKey, h.Force || CoordinationRuntime.ForceNextRun, ct).ConfigureAwait(false);
            if (!attempt.Granted)
            {
                Log($"[{account.DisplayName}] ⛔ shop \"{shop.DisplayName}\" đang được máy \"{attempt.Result.BlockedByHostname}\" chạy (hoặc mất kết nối Hub) — bỏ qua. Bấm 'Chạy đè' nếu chắc máy kia đã dừng.");
                return;
            }
            lease = attempt.Handle;

            // ĐÓNG KHUNG: cấp một bộ tk Shopee CỐ ĐỊNH (FrameSize) cho job này, GỠ khỏi kho chung → các job
            // RỜI nhau. Resume giữ NGUYÊN khung cũ (đọc id đã lưu) để KHÔNG phơi tk MỚI lên BigSeller; Reset
            // cấp khung mới. Engine chỉ xoay vòng TRONG khung → BigSeller chỉ thấy ngần ấy thiết bị ổn định.
            var frameSize = Math.Max(1, target.FrameSize);
            IReadOnlyList<string>? preferIds = resume ? ScrapeProgressStore.Shared.GetFrame(account.Id, sheet) : null;
            var frame = s.ClaimFrame(frameSize, preferIds);
            claimedFrameIds = frame.Select(a => a.Id).ToList();   // ghi nhận để nhả giữ-chỗ ở finally (kể cả khi job dừng giữa chừng)
            if (frame.Count == 0) { Log($"[{account.DisplayName}] kho tk Shopee đã cạn (mọi tk đang thuộc khung khác / Search đang giữ / bị tắt) — bỏ qua."); return; }

            // ACCOUNT-LEASE XUYÊN MÁY: tk nào đang được MÁY KHÁC dùng → loại khỏi khung (chống dùng trùng).
            if (accHub is not null)
            {
                var granted = await accHub.ReserveAccountsAsync(claimedFrameIds).ConfigureAwait(false);
                lock (reservedLock) hubReservedIds.AddRange(claimedFrameIds.Where(granted.Contains));   // tập con Hub cấp → nhả/heartbeat trên Hub
                ShopeeAccountUsage.Shared.MarkHubLeased(granted);   // dấu per-máy → module khác (Search) không cướp lease các tk này
                if (granted.Count < claimedFrameIds.Count)
                {
                    frame = frame.Where(a => granted.Contains(a.Id)).ToList();
                    Log($"[{account.DisplayName}] {claimedFrameIds.Count - granted.Count} tk Shopee đang được máy khác dùng → loại, còn {frame.Count}.");
                    // KHÔNG thu hẹp claimedFrameIds: finally vẫn nhả-cục-bộ CẢ khung (tránh rò rỉ tk khỏi kho chung).
                }
                if (frame.Count == 0) { Log($"[{account.DisplayName}] mọi tk trong khung đang được máy khác dùng — bỏ qua."); return; }

                // Heartbeat account-lease theo TIMER nền (không lệ thuộc tốc độ scrape) → khỏi hết hạn 5' giữa chunk
                // dài. Snapshot dưới khóa vì tk BÙ có thể được thêm vào hubReservedIds trong lúc chạy.
                accHeartbeat = new Timer(_ =>
                {
                    List<string> snap; lock (reservedLock) snap = hubReservedIds.ToList();
                    if (snap.Count > 0) { try { _ = accHub.HeartbeatAccountsAsync(snap); } catch { } }
                }, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
            }
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
            Log($"── {(resume ? "⏯ Tiếp tục" : "▶")} BigSeller \"{account.DisplayName}\" · shop \"{shop.DisplayName}\" · {totalSeg} dòng cần chạy (tổng sheet {totalRows}) · {procs} cửa sổ · KHUNG {frame.Count} tk Shopee (xoay vòng trong khung) · trần tổng app {Shopee.Core.Browser.BraveFleet.MaxConcurrentWindows} cửa sổ ──");

            // Ghi nhận lượt chạy (chỉ tiến độ DÒNG — không đặt-chỗ tk nữa vì tk xoay vòng/tự trả về kho).
            if (resume) ScrapeProgressStore.Shared.BeginResume(account.Id, sheet, account.DisplayName, totalRows);
            else ScrapeProgressStore.Shared.BeginFresh(account.Id, sheet, account.DisplayName, totalRows);
            OnUi(target.RefreshProgress);

            runner = new ScrapeRunner(account.WorkbookPath, VideoDir, braveExe: null, s.SourceUserData, bigSellerAccountName: account.DisplayName,
                bigSellerKiotKey: account.KiotProxyKey, bigSellerRegion: account.Region, bigSellerProxyType: account.ProxyType);
            s.Jobs.TryUpdate(account.Id, j => j.Runner = runner);   // gán Runner DƯỚI lock (vs snapshot ở Stop)
            // Mỗi chunk xong → lưu tiến độ ngay (bền với dừng/treo).
            runner.RowsCompleted += (from, to) =>
            {
                ScrapeProgressStore.Shared.MarkCompleted(account.Id, sheet, from, to);
                Coordination.Hub.PublishProgress(coordKey, from, to);     // chia sẻ tiến độ lên Hub
                OnUi(target.RefreshProgress);
            };
            WireRunner(runner, seq, account.DisplayName);

            // BÙ TK THAY THẾ: khi captcha loại tk khỏi khung, pool xin 1 tk RẢNH từ kho chung (đã khóa lease
            // xuyên máy) để giữ đủ cỡ khung → job KHÔNG cạn khung phải chạy lại. Ghi nhận để NHẢ đúng ở finally
            // (giữ-chỗ cục bộ + lease Hub + heartbeat như tk khung ban đầu). Hết tk dư → pool giữ hành vi cũ.
            async Task<ShopeeAccount?> AcquireReplacementAsync(IReadOnlyCollection<string> excludeIds, CancellationToken rct)
            {
                var repl = await Shopee.Core.Accounts.AccountReplenisher.TryAcquireSpareAsync(excludeIds, accHub, rct).ConfigureAwait(false);
                if (repl is null) return null;
                lock (reservedLock)
                {
                    claimedFrameIds.Add(repl.Id);                          // nhả giữ-chỗ cục bộ ở finally
                    if (accHub is not null) hubReservedIds.Add(repl.Id);   // heartbeat + nhả lease Hub ở finally
                }
                return repl;
            }

            var pool = new SessionAccountPool(sheet, frame, AcquireReplacementAsync, msg => Log($"[{account.DisplayName}] {msg}"));
            await runner.RunAutoAsync(pool, procs, segments, rowsPer, account.CookieFile, ct).ConfigureAwait(false);

            // Kết thúc: xong hết [startRow..total] → completed; còn dở → stopped (resume chạy nốt theo dòng).
            ScrapeProgressStore.Shared.FinishRun(account.Id, sheet, startRow, totalRows);
            var after = ScrapeProgressStore.Shared.Find(account.Id, sheet);
            Log(string.Equals(after?.Status, "completed", StringComparison.OrdinalIgnoreCase)
                ? $"[{account.DisplayName}] ✔ Hoàn thành toàn bộ."
                : $"[{account.DisplayName}] ■ Chưa xong (xong tới dòng {after?.LastRowReached ?? 0}) — Tiếp tục để chạy nốt.");
        }
        catch (OperationCanceledException)
        {
            Log($"[{account.DisplayName}] ■ đã dừng — giữ tiến độ cho lần Tiếp tục.");
            try { ScrapeProgressStore.Shared.FinishRun(account.Id, sheet, startRow, 0); } catch { }
        }
        catch (Exception ex) { Log($"[{account.DisplayName}] ✘ lỗi: {ex.Message}"); }
        finally
        {
            // Hub: đẩy trạng thái hoàn thành lên ledger + nhả khoá việc + nhả account-lease xuyên máy.
            try
            {
                var fin = ScrapeProgressStore.Shared.Find(account.Id, sheet);
                Coordination.Hub.PublishCompletion(coordKey, fin?.Status ?? "stopped", fin?.LastRowReached ?? 0);
            }
            catch { }
            if (accHeartbeat is not null) { try { await accHeartbeat.DisposeAsync().ConfigureAwait(false); } catch { } }
            // Snapshot dưới khóa (khung ban đầu + tk BÙ) → nhả ĐÚNG toàn bộ tk đã giữ, không rò.
            List<string> hubToRelease, localToRelease;
            lock (reservedLock) { hubToRelease = hubReservedIds.ToList(); localToRelease = claimedFrameIds.ToList(); }
            ShopeeAccountUsage.Shared.UnmarkHubLeased(hubToRelease);   // gỡ dấu per-máy TRƯỚC/CÙNG khi nhả lease Hub
            if (accHub is not null) { try { await accHub.ReleaseAccountsAsync(hubToRelease).ConfigureAwait(false); } catch { } }
            if (lease is not null) { try { await lease.DisposeAsync().ConfigureAwait(false); } catch { } }

            // Nhả giữ-chỗ CẢ KHUNG (+ tk bù) → Search (và job Scrape khác) lại mượn được các tk này.
            ShopeeAccountUsage.Shared.ReleaseReservation(localToRelease);
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
            Log($"➕ [{target.DisplayName}] đã thêm vào lượt chạy (tiếp tục phần còn thiếu)…");
            return true;
        }
        Log($"[{target.DisplayName}] đang chạy rồi — bỏ qua.");
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

    /// <summary>Click checkbox khi ĐANG run: hỏi xác nhận rồi chạy/dừng RIÊNG tk đó. Gọi từ code-behind.</summary>
    public async Task ToggleAccountDuringRun(ScrapeTargetViewModel target)
    {
        var s = _session;
        if (s is null) return;
        var running = s.Jobs.Contains(target.Account.Id);

        if (running)
        {
            if (Dialogs.Show($"Xác nhận HỦY chạy \"{target.DisplayName}\"?\nCác tài khoản khác vẫn chạy bình thường.",
                    "Hủy chạy tài khoản", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            await StopOneAccount(target);
            target.IsSelected = false;
        }
        else
        {
            if (Dialogs.Show($"Xác nhận CHẠY \"{target.DisplayName}\" (tiếp tục phần dòng còn thiếu)?",
                    "Chạy tài khoản", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            if (StartOneAccount(target)) target.IsSelected = true;
        }
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
    private void ShowStats()
    {
        if (SelectedTarget is null) return;
        var vm = new ScrapeStatsViewModel(SelectedTarget.Account.Id, SelectedTarget.Account.DisplayName);
        var win = new ScrapeStatsWindow(vm) { Owner = Application.Current?.MainWindow };
        win.ShowDialog();
        SelectedTarget.RefreshProgress();   // có thể đã nhả tay / xoá tiến độ → cập nhật nhãn.
    }

    /// <summary>Proxy lấy XOAY VÒNG từ kho KiotProxy dùng chung (ghi đè proxy gắn sẵn của acc); kho rỗng →
    /// giữ proxy của acc (fallback).</summary>
    private static ScrapeAccountSpec ToSpec(ShopeeAccount a, string sheet)
    {
        var pooled = KiotProxyPoolStore.Shared.ProxyForAccount(a.Id);
        var kiot = pooled?.KiotKey ?? a.KiotProxyKey;
        var manual = pooled?.Manual ?? a.ManualProxy;
        return new(a.Id, a.DisplayName, a.ShopeeAccountLogin, a.OpenWithShopeeAccount,
            kiot, a.Region, a.ProxyType, manual, a.RequireProxy, sheet, 0, 0,
            ResolveShopeeProfileDir(a));
    }

    /// <summary>Thư mục profile (Edge) đã đăng nhập Shopee của tk — engine import session từ đây sang
    /// Brave để khỏi login form. ProfileRelativePath có thể tuyệt đối (do tab Kiểm tra tài khoản lưu)
    /// hoặc tương đối "profiles/{Id}".</summary>
    private static string ResolveShopeeProfileDir(ShopeeAccount a)
    {
        var rel = a.ProfileRelativePath;
        // TRỐNG hoặc TUYỆT ĐỐI → LUÔN dùng gốc profile CỤC BỘ theo Id. KHÔNG tin path tuyệt đối lưu sẵn: nó có
        // thể là path của MÁY KHÁC (acc đến từ Hub lưu "C:\Users\<user máy Hub>\…") → client tạo profile dưới
        // C:\Users\<máy khác>\ → "Access denied". Chỉ path TƯƠNG ĐỐI mới ghép với gốc cục bộ (giống mọi máy).
        if (string.IsNullOrWhiteSpace(rel) || Path.IsPathRooted(rel))
            return Path.Combine(SuitePaths.ModuleDir("shared"), "profiles", a.Id);
        return Path.Combine(SuitePaths.ModuleDir("shared"), rel.Replace('/', Path.DirectorySeparatorChar));
    }

    private void WireRunner(ScrapeRunner runner, int seq, string bigSellerName)
    {
        string K(string key) => $"{seq}:{key}";   // namespace key theo job để nhiều BigSeller chạy đồng thời không đụng lưới
        runner.InstanceLog += (key, line) => OnUi(() =>
        {
            var inst = Instances.FirstOrDefault(x => x.Key == K(key));
            LogLines.Add($"[{bigSellerName}][{inst?.Label ?? key}] {line}");
        });
        runner.InstanceStatus += (key, st) => OnUi(() =>
        {
            var inst = Instances.FirstOrDefault(x => x.Key == K(key));
            if (inst is not null) inst.Status = st;
        });
        runner.SlotAssigned += (key, account, range) => OnUi(() =>
        {
            var inst = Instances.FirstOrDefault(x => x.Key == K(key));
            if (inst is not null) { inst.AccountName = account; inst.RangeText = range; }
        });
        runner.AccountErrored += (id, label, reason, captchaUrl) => OnUi(() =>
        {
            var now = DateTime.Now.ToString("HH:mm:ss");
            var row = ErroredAccounts.FirstOrDefault(x => x.Id == id);
            if (row is null) ErroredAccounts.Insert(0, new ErroredAccountRow(id, label, reason, now));
            else { row.Reason = reason; row.Time = now; }
            LogLines.Add($"⚠ Tk lỗi: {label} — {reason}");
            // Cột "Tình trạng" → "⚠ Captcha" cho tk vừa dính captcha/lỗi trong lượt chạy này.
            ShopeeAccountUsage.Shared.MarkCaptcha(id);
            FlagAccountErrored(id, $"Dính captcha/lỗi (Scrape) — {DateTime.Now:dd/MM HH:mm}: {reason}", captchaUrl);
        });
        runner.BigSellerNeedLogin += reason => OnUi(() =>
        {
            // Tk BigSeller mất phiên ("log in first") → job tk này đã bị dừng. Báo rõ để user đăng nhập lại.
            LogLines.Add($"⛔ [{bigSellerName}] BigSeller mất đăng nhập: {reason} — đã DỪNG job tk này. Hãy ĐĂNG NHẬP LẠI BigSeller rồi chạy lại.");
        });
    }

    // Đánh dấu BỀN account dính captcha/lỗi: Disabled (tự bỏ qua lượt sau) + LastError → gom ở mục
    // "Tài khoản & Proxy" (bộ lọc "Bị lỗi / captcha") để xử lý sau rồi "Bật lại".
    private static void FlagAccountErrored(string id, string reason, string? captchaUrl = null)
    {
        var acc = AccountStore.Shared.Accounts.FirstOrDefault(a => a.Id == id);
        if (acc is null) return;
        var alreadyFlagged = acc.Disabled;
        acc.Disabled = true;
        acc.LastError = reason;
        // Lưu URL captcha để "Kiểm tra tk lỗi" mở đúng trang đó (thay vì auto-login).
        if (!string.IsNullOrWhiteSpace(captchaUrl)) acc.CaptchaUrl = captchaUrl;
        if (!alreadyFlagged || !string.IsNullOrWhiteSpace(captchaUrl)) AccountStore.Shared.Save();
        // CLIENT: báo Hub acc này dính captcha (Hub xem ở panel + operator quyết giữ/xóa). Hub/standalone: là bản chính, khỏi báo.
        if (CoordinationRuntime.Active && !HubServerConfigStore.Shared.Current.Enabled)
            _ = CoordinationRuntime.Hub?.ReportErroredAccountAsync(id, reason, captchaUrl, "captcha");
    }

    private void Log(string text) => OnUi(() => LogLines.Add(text));

    private static void OnUi(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a();
        else d.BeginInvoke(a);
    }

    private void Warn(string msg, bool silent = false)
    {
        Status = msg;
        if (silent) Log("⚠ " + msg);   // đường push-dispatch: KHÔNG mở modal (tránh treo UI), chỉ ghi log
        else Dialogs.Show(msg, "Shopee Scrape", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Tiền-kiểm điều kiện scrape 1 đích (kho tk Shopee, Brave, cấu hình) — KHÔNG mở dialog.
    /// Cho <c>AssignmentWorker</c> kiểm TRƯỚC khi chạy để khỏi modal + khỏi kẹt việc 'running'.</summary>
    public bool CanDispatchScrape(ScrapeTargetViewModel target, out string problem)
    {
        var pool = AccountStore.Shared.Accounts.Count(a => !a.Disabled);
        if (pool == 0) { problem = "kho tài khoản Shopee trống"; return false; }
        if (BrowserLauncher.DetectUserData(BrowserKind.Brave) is null) { problem = "không tìm thấy User Data Brave"; return false; }
        return ValidateTarget(target, pool, out problem);
    }

    // ── Phiên chạy + handle từng job (để chạy/dừng RIÊNG từng tk khi đang run) ──
    private sealed class RunSession
    {
        public required string SourceUserData;
        public readonly List<ShopeeAccount> Available = [];        // kho tk Shopee CÒN LẠI (reserve) — đã trừ các khung
        public readonly object AllocLock = new();
        public readonly CancellationTokenSource MasterCts = new(); // huỷ TOÀN phiên (Stop)
        // Sổ job theo tk (key = BigSeller Account.Id). Tự lo lock + dedup + chốt (Finalizing cũ = _sealed nội bộ).
        public readonly PerAccountJobRegistry<JobHandle> Jobs = new();
        public int JobSeq;          // tăng dần — namespace key lưới UI (thay jobIndex cũ)
        public bool Resume;         // chế độ phiên
        public long LruTick;        // bộ đếm vòng-LRU cấp phát tk Shopee (seed cho ClaimFrame)

        // ── Cấp KHUNG tk Shopee cho 1 job BigSeller (đóng khung): lấy (và GỠ khỏi kho chung) tối đa n tk —
        // ưu tiên id đã lưu (resume giữ khung cũ), rồi bù bằng tk nghỉ lâu nhất. Khung các job RỜI nhau
        // (mỗi tk chỉ thuộc 1 khung) → mỗi tk BigSeller chỉ phơi ngần ấy thiết bị. Mỗi tk Shopee có
        // profile bền RIÊNG nên tái dùng trong khung = import BigSeller 1 lần rồi giữ token sống. ──
        public List<ShopeeAccount> ClaimFrame(int n, IReadOnlyList<string>? preferIds)
        {
            lock (AllocLock)
            {
                var frame = new List<ShopeeAccount>();
                // CHỈ lấy tk GIÀNH ĐƯỢC quyền (TryReserve) → KHÔNG đụng tk module khác (Search) đang giữ →
                // 2 module không bao giờ mở cùng 1 tk Shopee. Khung được NHẢ khi job kết thúc (RunOneJobAsync finally).
                if (preferIds is not null)
                    foreach (var id in preferIds)
                    {
                        // Né tk module khác đang giữ lease Hub trên máy này (IsHubLeased) — khỏi cướp lease chéo-module.
                        var a = Available.FirstOrDefault(x => x.Id == id && !x.Disabled && !ShopeeAccountUsage.Shared.IsHubLeased(x.Id));
                        if (a is not null && ShopeeAccountUsage.Shared.TryReserve(a.Id)) { frame.Add(a); Available.Remove(a); }
                    }
                // Lấp đủ số: chọn NGẪU NHIÊN trong kho (tk còn bật + chưa module khác giữ chỗ/giữ lease Hub) cho đủ n.
                while (frame.Count < n)
                {
                    var candidates = Available.Where(x => !x.Disabled
                        && !ShopeeAccountUsage.Shared.IsReserved(x.Id)
                        && !ShopeeAccountUsage.Shared.IsHubLeased(x.Id)).ToList();
                    if (candidates.Count == 0) break;
                    var a = candidates[Random.Shared.Next(candidates.Count)];
                    // Module khác vừa giành mất giữa lúc lọc → bỏ tk này khỏi kho, thử tk khác (không kẹt vì kho co dần).
                    if (!ShopeeAccountUsage.Shared.TryReserve(a.Id)) { Available.Remove(a); continue; }
                    frame.Add(a); Available.Remove(a);
                }
                return frame;
            }
        }
    }

    private sealed class JobHandle
    {
        public required ScrapeTargetViewModel Target;
        public required int Seq;
        public required CancellationTokenSource Cts;   // linked tới MasterCts → dừng RIÊNG 1 tk
        public ScrapeRunner? Runner;
        public Task Task = Task.CompletedTask;
        public bool Force;   // Chạy đè: bỏ qua khoá hub của máy khác
    }
}
