using System.Diagnostics;
using System.Runtime;
using Shopee.Core.Infrastructure;
using Shopee.Core.Platform;

namespace Shopee.Core.Browser;

/// <summary>
/// Quản trị tài nguyên cho TOÀN BỘ Brave do app phóng (chủ yếu luồng Scrape chạy dài qua đêm). Gộp 3
/// lớp bảo vệ chống "đơ máy" (xem chẩn đoán 25/06: thủ phạm là BÙNG SỐ TIẾN TRÌNH Brave làm nghẽn
/// CPU/handle/WMI, không phải hết RAM vật lý):
///
///  1) PHANH SỐ CỬA SỔ (mềm, trong app): <see cref="AcquireWindowSlotAsync"/> giới hạn tổng cửa sổ Brave
///     chạy ĐỒNG THỜI trên MỌI job (mỗi cửa sổ ≈ 5 tiến trình con → nhiều job × MaxProcess dồn lại là
///     thứ nhấn chết máy). Gate dùng CHUNG cho mọi ScrapeRunner nên tổng cửa sổ không vượt trần dù chạy
///     nhiều BigSeller cùng lúc. Kèm chờ khi RAM trống thấp.
///
///  2) DỌN ĐỊNH KỲ (luồng nền, KHÔNG phụ thuộc UI): <see cref="StartMaintenance"/> dùng Timer threadpool
///     nên UI có treo thì việc dọn vẫn chạy. Mỗi nhịp: GC nén heap + trả working set app về OS + quét
///     giết Brave mồ côi.
///
///  3) DỌN BRAVE MỒ CÔI: <see cref="SweepOrphans"/> giết brave.exe có --user-data-dir nằm trong thư mục
///     profile của app NHƯNG không thuộc session nào còn sống (sót sau crash/treo). <see cref="StartupSweep"/>
///     chạy 1 lần lúc khởi động để dọn rác của lần chạy trước.
///
/// LƯU Ý "app chết thì ai dọn": lớp 1–3 chỉ chạy khi app còn sống. Trần CỨNG khi app treo/chết do
/// <see cref="BraveJobObject"/> (KILL_ON_JOB_CLOSE + ACTIVE_PROCESS_LIMIT) lo — OS tự ép, không cần code app.
///
/// AN TOÀN: chỉ đụng Brave có user-data-dir nằm trong persistent-data của app → KHÔNG bao giờ chạm Brave
/// cá nhân (nằm ở %LocalAppData%\BraveSoftware) hay Brave của app khác.
/// </summary>
public static class BraveFleet
{
    // Thư mục gốc chứa MỌI profile Brave do app tạo (persistent-data). Brave có --user-data-dir nằm
    // trong đây = "của app"; ngoài đây = Brave cá nhân/khác → tuyệt đối không đụng.
    private static readonly string ManagedRoot = NormalizePath(SuitePaths.ModuleDir("persistent-data"));

    // Root phụ (vd %APPDATA%\XuLyDonShopee\profiles của module Đơn hàng) — đăng ký lúc boot qua
    // AddManagedRoot. Sweep chỉ đụng browser có --user-data-dir nằm dưới các root này.
    // GIÁ TRỊ = phạm vi quét: 0 = quét CẢ định kỳ lẫn lúc khởi động; 1 = CHỈ quét lúc khởi động.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> ExtraManagedRoots =
        new(StringComparer.OrdinalIgnoreCase);

    private const byte QuetDinhKy = 0;
    private const byte ChiQuetLucKhoiDong = 1;

    // Profile của các session ĐANG SỐNG (đăng ký lúc phóng Brave, gỡ lúc đóng). Sweep CHỪA các dir này.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> ActiveProfiles =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tiến trình vừa sinh dưới ngưỡng này thì CHỪA ở nhịp định kỳ — nó có thể đang trong khoảng
    /// giữa "đã phóng" và "kịp đăng ký profile sống".</summary>
    private static readonly TimeSpan TuoiToiThieuCoiLaMoCoi = TimeSpan.FromSeconds(60);

    /// <summary>Thêm thư mục gốc profile do module khác quản lý (vd Đơn hàng). Path được chuẩn hoá;
    /// gọi trước <see cref="StartupSweep"/> để lần quét đầu phủ luôn root này. An toàn gọi nhiều lần
    /// (lần gọi sau ghi đè phạm vi quét của lần trước).
    /// <para><paramref name="chiQuetLucKhoiDong"/> = true → root này CHỈ bị quét ở <see cref="StartupSweep"/>,
    /// nhịp dọn định kỳ TUYỆT ĐỐI không đụng. Dành cho module KHÔNG đăng ký hồ sơ đang chạy qua
    /// <see cref="RegisterActiveProfile"/> (module Đơn hàng): với nó mọi trình duyệt ĐANG LÀM VIỆC đều trông
    /// y như mồ côi, để nhịp định kỳ quét là tự giết trình duyệt của chính mình.</para></summary>
    public static void AddManagedRoot(string path, bool chiQuetLucKhoiDong = false)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var nd = NormalizePath(path);
        if (nd.Length == 0) return;
        ExtraManagedRoots[nd] = chiQuetLucKhoiDong ? ChiQuetLucKhoiDong : QuetDinhKy;
    }

    /// <summary>Kênh thông báo (vd dòng log của tab Scrape) cho việc dọn nền. Best-effort, có thể null.</summary>
    public static Action<string>? Notice { get; set; }

    // ─────────────────────────── 1) PHANH SỐ CỬA SỔ ───────────────────────────

    private static readonly object _gateLock = new();
    private static SemaphoreSlim? _windowGate;
    private static int _maxWindows = WindowsForBudget(0, 0);

    /// <summary>Trần tổng cửa sổ Brave chạy đồng thời (mọi job cộng lại). Mặc định suy từ RAM. Đặt
    /// TRƯỚC khi bắt đầu run (đổi giữa chừng chỉ áp cho lần tạo gate kế).</summary>
    public static int MaxConcurrentWindows
    {
        get { lock (_gateLock) return _maxWindows; }
        set
        {
            lock (_gateLock)
            {
                _maxWindows = Math.Clamp(value, 1, 64);
                _windowGate = null; // tạo lại theo trần mới ở lần Acquire kế
            }
        }
    }

    /// <summary>Số nhân CPU logic của máy (để hiển thị + tính trần tự động).</summary>
    public static int CpuCores => System.Environment.ProcessorCount;

    /// <summary>Tổng RAM vật lý của máy (GB, làm tròn) — để hiển thị.</summary>
    public static int TotalRamGb => (int)Math.Round(TotalPhysicalBytes() / (1024.0 * 1024 * 1024));

    /// <summary>Tính trần cửa sổ từ "ngân sách" người dùng cho phép: <paramref name="usableCpu"/> nhân CPU
    /// (mỗi cửa sổ ~1 nhân) và <paramref name="usableRamGb"/> GB RAM (mỗi cửa sổ ~2GB). Giá trị 0 = MẶC ĐỊNH
    /// (CPU: nửa số nhân để máy còn mượt; RAM: toàn bộ). Đo thực 25/06: máy 12 nhân chạy ~6 cửa sổ thì mượt,
    /// 9 thì ì → mặc định nửa số nhân.</summary>
    public static int WindowsForBudget(int usableCpu, int usableRamGb)
    {
        var cpu = usableCpu > 0 ? usableCpu : Math.Max(2, CpuCores / 2);
        var ram = usableRamGb > 0 ? usableRamGb : TotalRamGb;
        return Math.Clamp(Math.Min(cpu, ram / 2), 1, 64);
    }

    // RAM trống tối thiểu trước khi cho mở thêm cửa sổ — dưới mức này thì CHỜ (chống dồn tới đơ máy).
    private const ulong MinFreeBytesToLaunch = 1500UL * 1024 * 1024; // ~1.5 GB

    // ĐĨA trống tối thiểu (ổ chứa profile) trước khi cho mở thêm cửa sổ — dưới mức này thì CHỜ. Chống việc
    // ghi cache/DB tới khi ổ đầy 0 byte làm hỏng profile → mất phiên → captcha. Quan trọng với máy client ít ổ.
    private const long MinFreeDiskBytesToLaunch = DiskSpaceGuard.DefaultMinFreeBytes; // 5 GB

    private static SemaphoreSlim Gate()
    {
        lock (_gateLock)
            return _windowGate ??= new SemaphoreSlim(_maxWindows, _maxWindows);
    }

    /// <summary>Xin 1 suất mở cửa sổ Brave: chờ tới khi (a) còn slot trong trần, và (b) RAM trống đủ.
    /// Ném <see cref="OperationCanceledException"/> nếu bị hủy. Phải gọi <see cref="ReleaseWindowSlot"/>
    /// đúng 1 lần khi đã chiếm được suất (return bình thường).</summary>
    public static async Task AcquireWindowSlotAsync(Action<string>? log, CancellationToken ct)
    {
        var gate = Gate();
        if (gate.CurrentCount == 0)
            log?.Invoke($"⏳ Đã đạt trần {MaxConcurrentWindows} cửa sổ Brave (toàn app) — chờ slot trống…");

        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Có slot rồi nhưng RAM trống thấp → chờ thêm cho hệ thống hồi (vẫn giữ slot để không bùng cửa sổ).
            var warned = false;
            while (AvailablePhysicalBytes() < MinFreeBytesToLaunch)
            {
                ct.ThrowIfCancellationRequested();
                if (!warned) { log?.Invoke("⏳ RAM trống thấp — hoãn mở cửa sổ Brave mới tới khi hồi…"); warned = true; }
                await Task.Delay(5000, ct).ConfigureAwait(false);
            }

            // Ổ chứa profile sắp đầy → hoãn mở cửa sổ mới (giữ slot) tới khi có chỗ. Thà kẹt-cảnh-báo còn hơn
            // ghi tới khi đầy 0 byte làm hỏng DB profile. FreeBytesFor < 0 = không đọc được → fail-open, chạy tiếp.
            var diskWarned = false;
            for (var free = DiskSpaceGuard.FreeBytesFor(ManagedRoot);
                 free >= 0 && free < MinFreeDiskBytesToLaunch;
                 free = DiskSpaceGuard.FreeBytesFor(ManagedRoot))
            {
                ct.ThrowIfCancellationRequested();
                if (!diskWarned)
                {
                    log?.Invoke($"⚠️ Ổ đĩa còn trống {DiskSpaceGuard.ToGb(free)} (< {DiskSpaceGuard.ToGb(MinFreeDiskBytesToLaunch)}) — HOÃN mở cửa sổ Brave. Hãy dọn bớt đĩa (cache/video/profile cũ).");
                    diskWarned = true;
                }
                await Task.Delay(10000, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            gate.Release();   // hủy giữa lúc chờ RAM → trả slot, không rò
            throw;
        }
    }

    /// <summary>Trả lại suất mở cửa sổ (gọi đúng 1 lần ứng với mỗi <see cref="AcquireWindowSlotAsync"/> thành công).</summary>
    public static void ReleaseWindowSlot()
    {
        try { Gate().Release(); } catch { /* SemaphoreFullException khi gate vừa bị tạo lại — bỏ qua */ }
    }

    // ─────────────────────────── ĐĂNG KÝ PROFILE SỐNG ───────────────────────────

    /// <summary>Đánh dấu profile đang có Brave SỐNG (gọi khi phóng Brave) → trình dọn mồ côi sẽ chừa ra.</summary>
    public static void RegisterActiveProfile(string profileDir)
    {
        var k = NormalizePath(profileDir);
        if (k.Length > 0) ActiveProfiles[k] = 1;
    }

    /// <summary>Gỡ đánh dấu (gọi khi đóng session) → Brave còn sót của profile này thành mồ côi, bị dọn.</summary>
    public static void UnregisterActiveProfile(string profileDir)
    {
        var k = NormalizePath(profileDir);
        if (k.Length > 0) ActiveProfiles.TryRemove(k, out _);
    }

    // ─────────────────────────── 2) DỌN ĐỊNH KỲ (LUỒNG NỀN) ───────────────────────────

    private static Timer? _maintenanceTimer;
    private static int _maintenanceBusy;

    /// <summary>Bật vòng dọn nền (idempotent). Chạy trên Timer threadpool nên KHÔNG phụ thuộc UI: UI treo
    /// thì việc dọn vẫn chạy. Mỗi nhịp: GC + trả working set app + quét Brave mồ côi.</summary>
    public static void StartMaintenance(int intervalMinutes = 4)
    {
        lock (_gateLock)
        {
            if (_maintenanceTimer is not null) return;
            var period = TimeSpan.FromMinutes(Math.Clamp(intervalMinutes, 1, 30));
            _maintenanceTimer = new Timer(_ => RunMaintenance(), null, period, period);
        }
    }

    private static void RunMaintenance()
    {
        if (Interlocked.CompareExchange(ref _maintenanceBusy, 1, 0) != 0) return; // bỏ nhịp nếu nhịp trước chưa xong
        try
        {
            TrimAppWorkingSet();
            SweepOrphans(Notice);
        }
        catch { }
        finally { Interlocked.Exchange(ref _maintenanceBusy, 0); }
    }

    /// <summary>GC nén heap (kể cả LOH) rồi trả working set của tiến trình app về OS. An toàn: chỉ đụng
    /// tiến trình app, không đụng Brave (trim Brave đang cào dễ phản tác dụng vì fault-back).</summary>
    public static void TrimAppWorkingSet()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect();
            PlatformServices.WorkingSet.TrimCurrentProcess();
        }
        catch { }
    }

    // ─────────────────────────── 3) DỌN BRAVE MỒ CÔI ───────────────────────────

    /// <summary>Quét 1 lần lúc khởi động: giết MỌI Brave thuộc profile của app (lúc này chưa session nào
    /// sống → tất cả là rác sót sau lần chạy trước bị treo/crash). Trả số tiến trình đã giết.</summary>
    public static int StartupSweep() => SweepOrphans(Notice, killAll: true);

    /// <summary>Giết browser của app mà lượt quét này coi là mồ côi — luật ở <see cref="LaMoCoiCanGiet"/>.
    /// <paramref name="killAll"/>=true → lượt quét KHỞI ĐỘNG (giết hết mọi root).</summary>
    private static int SweepOrphans(Action<string>? log, bool killAll = false)
    {
        // AN TOÀN ĐA-INSTANCE: registry profile-sống là TRONG-tiến-trình → nếu có ShopeeSuite KHÁC đang
        // chạy, Brave của nó (cùng persistent-data) sẽ bị coi nhầm là mồ côi. Khi không phải instance duy
        // nhất → KHÔNG quét (để Job Object + governor lo). Trường hợp thường (1 app) vẫn được bảo vệ đủ.
        if (!IsSoleAppInstance())
            return 0;

        var rootDinhKy = RootQuetDinhKy();
        var rootKhoiDong = RootChiQuetLucKhoiDong();
        var dangHoatDong = ActiveProfiles.Keys;

        var killed = 0;
        var bayGio = DateTime.Now;
        foreach (var (pid, dir, started) in EnumerateOurBrave(log))
        {
            var tuoi = started is { } t ? bayGio - t : (TimeSpan?)null;
            if (!LaMoCoiCanGiet(dir, tuoi, killAll, rootDinhKy, rootKhoiDong, dangHoatDong)) continue;
            if (BraveProcessReaper.TryKillTree(pid)) killed++;
        }
        if (killed > 0)
            log?.Invoke($"🧹 Đã dọn {killed} tiến trình Brave mồ côi (sót sau treo/đóng bẩn).");
        return killed;
    }

    private static readonly string[] ManagedBrowsers = ["brave.exe", "chrome.exe", "msedge.exe"];

    private static List<(int pid, string dir, DateTime? started)> EnumerateOurBrave(Action<string>? log)
    {
        var list = new List<(int, string, DateTime?)>();
        foreach (var p in PlatformServices.ProcessFinder.Enumerate(ManagedBrowsers, log))
        {
            var dir = BraveProcessReaper.ExtractUserDataDir(p.CommandLine);
            if (dir is null) continue;
            var nd = NormalizePath(dir);
            if (!IsUnderManagedRoot(nd)) continue;   // KHÔNG phải browser của app → bỏ qua
            if (p.Pid <= 0) continue;
            DateTime? started = null;
            try { started = Process.GetProcessById(p.Pid).StartTime; } catch { }
            list.Add((p.Pid, nd, started));
        }
        return list;
    }

    /// <summary>Đây có phải ShopeeSuite DUY NHẤT đang chạy không (kể cả chính tiến trình này)? Dùng để
    /// không quét-giết nhầm Brave của instance ShopeeSuite khác (vốn dùng chung persistent-data), và để
    /// <see cref="Shopee.Core.Infrastructure.StartupJanitor"/> chỉ dọn đĩa khi không có instance khác.</summary>
    public static bool IsSoleAppInstance()
    {
        try
        {
            var procs = Process.GetProcessesByName("ShopeeSuite");
            try { return procs.Length <= 1; }
            finally { foreach (var p in procs) p.Dispose(); }
        }
        catch { return true; }   // không đếm được → coi như duy nhất (giữ hành vi dọn ở máy thường)
    }

    private static bool IsUnderManagedRoot(string normalizedDir)
    {
        if (IsUnderRoot(normalizedDir, ManagedRoot)) return true;
        foreach (var root in ExtraManagedRoots.Keys)
        {
            if (IsUnderRoot(normalizedDir, root)) return true;
        }
        return false;
    }

    /// <summary>Các root bị quét ở CẢ nhịp định kỳ lẫn lúc khởi động (persistent-data của suite + root phụ
    /// đăng ký ở chế độ mặc định).</summary>
    private static List<string> RootQuetDinhKy()
    {
        var list = new List<string> { ManagedRoot };
        foreach (var kv in ExtraManagedRoots)
        {
            if (kv.Value == QuetDinhKy) list.Add(kv.Key);
        }
        return list;
    }

    /// <summary>Các root CHỈ bị quét lúc khởi động (xem <see cref="AddManagedRoot"/>).</summary>
    private static List<string> RootChiQuetLucKhoiDong()
    {
        var list = new List<string>();
        foreach (var kv in ExtraManagedRoots)
        {
            if (kv.Value == ChiQuetLucKhoiDong) list.Add(kv.Key);
        }
        return list;
    }

    /// <summary>
    /// LUẬT THUẦN: một trình duyệt có <c>--user-data-dir</c> = <paramref name="normalizedDir"/> có phải MỒ CÔI
    /// CẦN GIẾT trong lượt quét này không. Tách khỏi <see cref="SweepOrphans"/> (vốn phải đụng WMI + trạng thái
    /// tĩnh) để test thẳng được ma trận ca.
    /// <para>Thứ tự xét, từ chắc chắn nhất xuống:</para>
    /// <list type="number">
    /// <item>Không nằm dưới root nào của app → KHÔNG đụng (Brave cá nhân / app khác).</item>
    /// <item>Lượt quét KHỞI ĐỘNG → giết hết: lúc đó chưa session nào của app sống, tất cả là rác lần trước.</item>
    /// <item>Chỉ nằm dưới root "chỉ quét lúc khởi động" → nhịp định kỳ KHÔNG đụng. Đây là lưới chặn đúng cái
    ///       lỗ đã có: module Đơn hàng đăng ký root nhưng không đăng ký hồ sơ đang chạy, nên trình duyệt đang
    ///       làm việc của nó trông y hệt mồ côi.</item>
    /// <item>Hồ sơ ĐANG HOẠT ĐỘNG (đã <see cref="RegisterActiveProfile"/>) → chừa.</item>
    /// <item>Tiến trình non hơn <see cref="TuoiToiThieuCoiLaMoCoi"/> → chừa (chưa kịp đăng ký).</item>
    /// </list>
    /// </summary>
    internal static bool LaMoCoiCanGiet(
        string normalizedDir,
        TimeSpan? tuoi,
        bool quetLucKhoiDong,
        IEnumerable<string> rootQuetDinhKy,
        IEnumerable<string> rootChiQuetLucKhoiDong,
        IEnumerable<string> hoSoDangHoatDong)
    {
        if (string.IsNullOrWhiteSpace(normalizedDir)) return false;

        var duoiRootDinhKy = NamDuoiMotRoot(normalizedDir, rootQuetDinhKy);
        var duoiRootKhoiDong = NamDuoiMotRoot(normalizedDir, rootChiQuetLucKhoiDong);
        if (!duoiRootDinhKy && !duoiRootKhoiDong) return false;

        if (quetLucKhoiDong) return true;

        if (!duoiRootDinhKy) return false;

        foreach (var hoSo in hoSoDangHoatDong)
        {
            if (string.Equals(hoSo, normalizedDir, StringComparison.OrdinalIgnoreCase)) return false;
        }

        return tuoi is not { } t || t >= TuoiToiThieuCoiLaMoCoi;
    }

    private static bool NamDuoiMotRoot(string normalizedDir, IEnumerable<string> roots)
    {
        foreach (var root in roots)
        {
            if (!string.IsNullOrEmpty(root) && IsUnderRoot(normalizedDir, root)) return true;
        }
        return false;
    }

    private static bool IsUnderRoot(string normalizedDir, string root) =>
        normalizedDir.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        normalizedDir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    // ─────────────────────────── CẤU HÌNH TRẦN CỨNG (JOB OBJECT) ───────────────────────────

    /// <summary>Đặt trần CỨNG cho Job Object (OS tự ép, kể cả khi app treo/chết). Phải gọi TRƯỚC lần
    /// phóng Brave đầu tiên (vd lúc khởi động app). Chặn theo SỐ TIẾN TRÌNH (đúng thủ phạm đã chẩn đoán);
    /// KHÔNG đặt trần RAM-commit vì Brave hay commit ảo cao → dễ false-trip làm crash tab giữa chừng.</summary>
    public static void ConfigureJobLimits()
    {
        // Mỗi cửa sổ ≈ 5–8 tiến trình. Để trần RỘNG (×16 + đệm) → chỉ chặn khi BÙNG runaway thật
        // (vd orphan dồn sau treo), không cản hoạt động bình thường.
        var procLimit = Math.Clamp(MaxConcurrentWindows * 16 + 64, 64, 4096);
        BraveJobObject.ConfigureLimits(procLimit, 0);
    }

    // ─────────────────────────── TIỆN ÍCH ───────────────────────────

    // NormalizePath ở đây CỐ Ý khác BraveProcessReaper: dùng Path.GetFullPath để so khớp ManagedRoot theo
    // đường dẫn tuyệt đối (registry profile-sống + IsUnderManagedRoot). Reaper chỉ so đúng chuỗi --user-data-dir
    // nên KHÔNG chia sẻ hàm này (khác hành vi).
    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path.Trim().Trim('"')).TrimEnd('\\', '/'); }
        catch { return path.Trim().Trim('"').TrimEnd('\\', '/'); }
    }

    // ── Bộ nhớ hệ thống (qua PlatformServices.Memory) ──
    private static ulong AvailablePhysicalBytes() => PlatformServices.Memory.AvailablePhysicalBytes();
    private static ulong TotalPhysicalBytes() => PlatformServices.Memory.TotalPhysicalBytes();
}
