using System.Diagnostics;
using System.Management;
using System.Runtime;
using System.Runtime.InteropServices;
using Shopee.Core.Infrastructure;

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

    // Profile của các session ĐANG SỐNG (đăng ký lúc phóng Brave, gỡ lúc đóng). Sweep CHỪA các dir này.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> ActiveProfiles =
        new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>Trần TỰ ĐỘNG (khi người dùng chưa đặt ngân sách): nửa số nhân + toàn bộ RAM.</summary>
    public static int AutoMaxWindows => WindowsForBudget(0, 0);

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
            EmptyWorkingSet(Process.GetCurrentProcess().Handle);
        }
        catch { }
    }

    // ─────────────────────────── 3) DỌN BRAVE MỒ CÔI ───────────────────────────

    /// <summary>Quét 1 lần lúc khởi động: giết MỌI Brave thuộc profile của app (lúc này chưa session nào
    /// sống → tất cả là rác sót sau lần chạy trước bị treo/crash). Trả số tiến trình đã giết.</summary>
    public static int StartupSweep() => SweepOrphans(Notice, killAll: true);

    /// <summary>Giết brave.exe có user-data-dir trong <see cref="ManagedRoot"/> mà KHÔNG thuộc session sống.
    /// <paramref name="killAll"/>=true → giết hết (dùng lúc khởi động). Chừa tiến trình vừa sinh &lt;60s
    /// phòng khi chưa kịp đăng ký.</summary>
    private static int SweepOrphans(Action<string>? log, bool killAll = false)
    {
        // AN TOÀN ĐA-INSTANCE: registry profile-sống là TRONG-tiến-trình → nếu có ShopeeSuite KHÁC đang
        // chạy, Brave của nó (cùng persistent-data) sẽ bị coi nhầm là mồ côi. Khi không phải instance duy
        // nhất → KHÔNG quét (để Job Object + governor lo). Trường hợp thường (1 app) vẫn được bảo vệ đủ.
        if (!IsSoleAppInstance())
            return 0;

        var killed = 0;
        foreach (var (pid, dir, started) in EnumerateOurBrave(log))
        {
            if (!killAll)
            {
                if (ActiveProfiles.ContainsKey(dir)) continue;
                if (started is { } t && (DateTime.Now - t) < TimeSpan.FromSeconds(60)) continue;
            }
            if (TryKillTree(pid)) killed++;
        }
        if (killed > 0)
            log?.Invoke($"🧹 Đã dọn {killed} tiến trình Brave mồ côi (sót sau treo/đóng bẩn).");
        return killed;
    }

    private static List<(int pid, string dir, DateTime? started)> EnumerateOurBrave(Action<string>? log)
    {
        var list = new List<(int, string, DateTime?)>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'brave.exe'");
            using var results = searcher.Get();
            foreach (var obj in results)
            {
                try
                {
                    var cl = obj["CommandLine"] as string;
                    if (string.IsNullOrEmpty(cl)) continue;
                    var dir = ExtractUserDataDir(cl);
                    if (dir is null) continue;
                    var nd = NormalizePath(dir);
                    if (!IsUnderManagedRoot(nd)) continue;   // KHÔNG phải Brave của app → bỏ qua
                    var pid = Convert.ToInt32(obj["ProcessId"]);
                    if (pid <= 0) continue;
                    DateTime? started = null;
                    try { started = Process.GetProcessById(pid).StartTime; } catch { }
                    list.Add((pid, nd, started));
                }
                catch { }
                finally { obj.Dispose(); }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Quét Brave (WMI) lỗi: {ex.Message}");
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

    private static bool IsUnderManagedRoot(string normalizedDir) =>
        normalizedDir.Equals(ManagedRoot, StringComparison.OrdinalIgnoreCase) ||
        normalizedDir.StartsWith(ManagedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

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

    private static bool TryKillTree(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (proc.HasExited) return false;
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(2000);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Trích giá trị cờ <c>--user-data-dir=</c> từ command-line (hỗ trợ có/không dấu nháy).</summary>
    private static string? ExtractUserDataDir(string commandLine)
    {
        const string flag = "--user-data-dir=";
        var idx = commandLine.IndexOf(flag, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var rest = commandLine[(idx + flag.Length)..];
        if (rest.Length == 0) return null;
        if (rest[0] == '"')
        {
            var end = rest.IndexOf('"', 1);
            return end < 0 ? rest[1..] : rest[1..end];
        }
        var space = rest.IndexOf(' ');
        return space < 0 ? rest : rest[..space];
    }

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path.Trim().Trim('"')).TrimEnd('\\', '/'); }
        catch { return path.Trim().Trim('"').TrimEnd('\\', '/'); }
    }

    // ── Bộ nhớ hệ thống (GlobalMemoryStatusEx) + trả working set (EmptyWorkingSet) ──
    private static ulong AvailablePhysicalBytes() => Mem().ullAvailPhys;
    private static ulong TotalPhysicalBytes() => Mem().ullTotalPhys;

    private static MEMORYSTATUSEX Mem()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        try { GlobalMemoryStatusEx(ref m); } catch { }
        return m;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);
}
