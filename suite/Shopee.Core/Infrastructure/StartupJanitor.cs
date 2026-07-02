using Shopee.Core.Accounts;
using Shopee.Core.Browser;

namespace Shopee.Core.Infrastructure;

/// <summary>
/// Dọn dẹp ĐĨA lúc khởi động app — chạy TỰ ĐỘNG trên mọi máy client (không cần dọn tay). Giải quyết việc
/// ổ C đầy rất nhanh khi chạy Scrape nhiều Brave: profile Brave của app là BỀN (giữ cookie chống captcha)
/// và không bao giờ bị xoá → cache phình + profile mồ côi tích tụ vô hạn.
///
/// Ba nhóm việc, đều BEST-EFFORT (mọi lỗi bị nuốt, không chặn khởi động):
///  1) DỌN CACHE mọi profile Brave của app (persistent-data, shared, check-account, bigseller) — xoá
///     Default\Cache, Safe Browsing, component_crx_cache, GPU/shader cache… GIỮ cookie/đăng nhập.
///  2) XOÁ PROFILE MỒ CÔI: thư mục theo account.Id không còn trong kho tài khoản (đã xoá/nhập lại → Id mới),
///     và các bản clone lane "-p{n}" của BigSeller (tạm thời, seed cookie mỗi lần chạy).
///  3) XOÁ RÁC DI SẢN: %AppData%\ShopeeStatApp\profiles (app cũ, không còn dùng) + file tạm ssck_*.db rò ở %TEMP%.
///
/// AN TOÀN ĐA-INSTANCE: chỉ chạy khi đây là ShopeeSuite DUY NHẤT trên máy (các instance khác có thể đang
/// mở Brave dùng chung persistent-data → không đụng cache/profile của chúng giữa chừng). Chạy trên luồng
/// nền nên KHÔNG chặn UI khởi động dù phải duyệt hàng chục GB.
/// </summary>
public static class StartupJanitor
{
    /// <summary>Kênh log tuỳ chọn (best-effort).</summary>
    public static Action<string>? Notice { get; set; }

    // ── Ngưỡng an toàn (chống xoá nhầm dữ liệu vừa tạo) ──
    // Profile mồ côi (Id không còn trong kho): chỉ xoá nếu KHÔNG dùng > ngưỡng này. Để RỘNG (14 ngày) vì có
    // luồng "gỡ tạm acc khỏi Hub rồi thêm lại cùng Id" (BackupService mirror) — cửa sổ ngắn sẽ xoá nhầm
    // profile của acc chỉ tạm vắng, làm mất cookie login khi acc quay lại.
    private static readonly TimeSpan OrphanMinAge = TimeSpan.FromDays(14);
    // Rác di sản ShopeeStatApp\profiles: app hiện KHÔNG ghi vào đó → quá 7 ngày coi như bỏ hẳn.
    private static readonly TimeSpan LegacyStaleAge = TimeSpan.FromDays(7);
    // File tạm ssck_*.db / mbr-*.ldb ở %TEMP%: quá 1 ngày là rò từ phiên trước.
    private static readonly TimeSpan TempJunkAge = TimeSpan.FromDays(1);

    private static int _started;
    private static int _runBusy;
    private static Timer? _periodicTimer;

    /// <summary>Chạy NGAY 1 lần trên luồng nền, RỒI LẶP LẠI mỗi <paramref name="periodicHours"/> giờ. Idempotent
    /// (gọi nhiều lần chỉ có tác dụng lần đầu), không ném. Lặp định kỳ là để máy client scrape qua NHIỀU ĐÊM
    /// không tắt app vẫn được dọn cache/profile mồ côi giữa chừng — trước đây chỉ dọn lúc khởi động nên ổ vẫn
    /// phình dần trong suốt phiên chạy dài.</summary>
    public static void RunInBackground(double periodicHours = 8)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            return;
        ThreadPool.QueueUserWorkItem(_ => RunGuarded());
        var period = TimeSpan.FromHours(Math.Clamp(periodicHours, 1, 48));
        _periodicTimer = new Timer(_ => RunGuarded(), null, period, period);
    }

    // Bọc Run() với chốt chống CHỒNG NHỊP: một lần dọn có thể duyệt hàng chục GB, lâu hơn chu kỳ timer → bỏ
    // nhịp nếu nhịp trước chưa xong (cùng cách RunMaintenance của BraveFleet). Nuốt lỗi, không chặn.
    private static void RunGuarded()
    {
        if (Interlocked.CompareExchange(ref _runBusy, 1, 0) != 0) return;
        try { Run(); }
        catch (Exception ex) { Notice?.Invoke("Dọn dẹp lỗi: " + ex.Message); }
        finally { Interlocked.Exchange(ref _runBusy, 0); }
    }

    /// <summary>Thực thi dọn dẹp (đồng bộ). Public để test/gọi tay được.</summary>
    public static void Run()
    {
        // Đa-instance: nhường việc dọn cho instance duy nhất để không xoá cache/profile mà instance khác
        // (dùng chung persistent-data) đang mở dở.
        if (!BraveFleet.IsSoleAppInstance())
        {
            Notice?.Invoke("Bỏ qua dọn dẹp khởi động: có ShopeeSuite khác đang chạy.");
            return;
        }

        var live = new HashSet<string>(
            AccountStore.Shared.Accounts.Select(a => a.Id).Where(id => !string.IsNullOrWhiteSpace(id)),
            StringComparer.OrdinalIgnoreCase);

        // CHỐT AN TOÀN: chỉ xoá profile "mồ côi" theo Id khi kho tài khoản đọc được (khác rỗng). AccountStore.Load
        // NUỐT mọi lỗi và để danh sách RỖNG khi accounts.json tạm không đọc được (AV khoá, đọc lỗi, chưa ghi
        // xong…). Nếu vẫn xoá theo "không nằm trong live" khi live rỗng → xoá SẠCH mọi profile → mất toàn bộ
        // cookie login → Shopee bắn captcha hàng loạt. Rỗng → chỉ dọn cache (an toàn), KHÔNG xoá profile.
        var deleteOrphans = live.Count > 0;
        if (!deleteOrphans)
            Notice?.Invoke("Kho tài khoản rỗng/không đọc được → chỉ dọn cache, KHÔNG xoá profile mồ côi.");

        long freed = 0;
        var persistent = SuitePaths.ModuleDir("persistent-data");
        var shared = SuitePaths.ModuleDir("shared");

        // 1) Profile SCRAPE (persistent-data\profiles) — key = account.Id. Dọn cache tất cả + xoá mồ côi.
        freed += SweepAccountProfiles(Path.Combine(persistent, "profiles"), live, deleteOrphans);
        // 2) Profile NGUỒN COOKIE (shared\profiles) — key = account.Id. Chỉ dùng để đọc cookie nên cache là
        //    rác 100%; xoá cache + xoá mồ côi.
        freed += SweepAccountProfiles(Path.Combine(shared, "profiles"), live, deleteOrphans);
        // 3) Profile BigSeller (persistent-data\bigseller-profiles) — dọn cache + xoá clone lane "-p{n}".
        freed += SweepBigSellerProfiles(Path.Combine(persistent, "bigseller-profiles"));
        // 4) Profile login BigSeller (bigseller-login) — chỉ dọn cache (kho tk BigSeller riêng, không xoá base).
        freed += PruneAllProfileCaches(Path.Combine(SuitePaths.Root, "bigseller-login"));
        // 5) Profile check-account (theo username) — CHỈ dọn cache, KHÔNG xoá cả profile (xem lý do ở hàm).
        freed += PruneCheckAccountProfiles(Path.Combine(SuitePaths.Root, "check-account", "profiles"));
        // 6) Rác di sản app cũ ShopeeStatApp\profiles (KHÔNG đụng tasks.db/settings.json).
        freed += SweepLegacyStatProfiles();
        // 7) File tạm rò ở %TEMP%.
        freed += SweepTempJunk();

        if (freed > 0)
            Notice?.Invoke($"🧹 Dọn dẹp khởi động: giải phóng ~{freed / (1024 * 1024)} MB trên đĩa.");
    }

    // ── Profile theo account.Id (persistent-data\profiles, shared\profiles) ──
    private static long SweepAccountProfiles(string root, HashSet<string> liveIds, bool deleteOrphans)
    {
        if (!Directory.Exists(root)) return 0;
        long freed = 0;
        foreach (var dir in SafeEnumerateDirectories(root))
        {
            var id = Path.GetFileName(dir);
            if (liveIds.Contains(id))
            {
                freed += BraveCachePolicy.PruneProfileCache(dir);
            }
            else if (deleteOrphans && Age(dir) > OrphanMinAge)
            {
                // Id không còn trong kho + đủ cũ → mồ côi (tk đã xoá hoặc nhập lại với Id mới). Xoá cả profile.
                freed += BraveCachePolicy.DeleteDirBestEffort(dir);
            }
            else
            {
                // Không xoá mồ côi (kho rỗng/không chắc, hoặc profile còn mới) → vẫn dọn cache cho an toàn.
                freed += BraveCachePolicy.PruneProfileCache(dir);
            }
        }
        return freed;
    }

    // ── Profile BigSeller: xoá clone lane "-p{n}", dọn cache các base ──
    private static long SweepBigSellerProfiles(string root)
    {
        if (!Directory.Exists(root)) return 0;
        long freed = 0;
        foreach (var dir in SafeEnumerateDirectories(root))
        {
            var name = Path.GetFileName(dir);
            if (IsLaneClone(name))
                freed += BraveCachePolicy.DeleteDirBestEffort(dir);   // lane phụ: seed cookie mỗi lần chạy, không cần giữ
            else
                freed += BraveCachePolicy.PruneProfileCache(dir);
        }
        return freed;
    }

    /// <summary>Tên profile clone lane có hậu tố "-p&lt;số&gt;" (vd "&lt;id&gt;-p1"). Xem UpdateProductRunner.</summary>
    private static bool IsLaneClone(string name)
    {
        var idx = name.LastIndexOf("-p", StringComparison.Ordinal);
        if (idx < 0 || idx + 2 >= name.Length) return false;
        for (var i = idx + 2; i < name.Length; i++)
            if (!char.IsDigit(name[i])) return false;
        return true;
    }

    // ── check-account\profiles (theo username): CHỈ dọn cache, KHÔNG xoá cả profile ──
    // Profile ở đây là BẢN LOGIN DUY NHẤT của tk đã check OK, cho tới khi user bấm "Lưu vào kho chung"
    // (SaveToShared copy cookie sang shared RỒI mới xoá nguồn). Nếu xoá theo tuổi ở đây sẽ mất cookie của tk
    // OK CHƯA kịp lưu → khi lưu thấy thiếu profile → phải check lại → login mới → captcha (đúng thứ cần tránh).
    // Bản đã lưu thì nguồn đã bị SaveToShared xoá ngay nên không tích tụ; phần còn lại prune cache là đủ nhẹ.
    private static long PruneCheckAccountProfiles(string root)
    {
        if (!Directory.Exists(root)) return 0;
        long freed = 0;
        foreach (var dir in SafeEnumerateDirectories(root))
            freed += BraveCachePolicy.PruneProfileCache(dir);
        return freed;
    }

    private static long PruneAllProfileCaches(string root)
    {
        if (!Directory.Exists(root)) return 0;
        long freed = 0;
        foreach (var dir in SafeEnumerateDirectories(root))
            freed += BraveCachePolicy.PruneProfileCache(dir);
        return freed;
    }

    // ── Rác di sản %AppData%\ShopeeStatApp\profiles (app cũ standalone; suite hiện KHÔNG ghi vào đây) ──
    private static long SweepLegacyStatProfiles()
    {
        var root = Path.Combine(SuitePaths.ShopeeStatDataDir, "profiles");
        if (!Directory.Exists(root)) return 0;
        long freed = 0;
        foreach (var dir in SafeEnumerateDirectories(root))
        {
            if (Age(dir) > LegacyStaleAge)
                freed += BraveCachePolicy.DeleteDirBestEffort(dir);
        }
        return freed;
    }

    // ── File tạm rò: ssck_*.db (copy cookie DB) + mbr-*.ldb ở %TEMP% ──
    private static long SweepTempJunk()
    {
        long freed = 0;
        string tmp;
        try { tmp = Path.GetTempPath(); } catch { return 0; }
        foreach (var pattern in new[] { "ssck_*.db", "mbr-*.ldb" })
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(tmp, pattern); } catch { continue; }
            foreach (var f in files)
            {
                try
                {
                    if (DateTime.Now - File.GetLastWriteTime(f) < TempJunkAge) continue;
                    var len = new FileInfo(f).Length;
                    File.Delete(f);
                    freed += len;
                }
                catch { }
            }
        }
        return freed;
    }

    // ── Tiện ích ──

    /// <summary>"Tuổi" của 1 profile = thời gian kể từ lần ghi GẦN NHẤT trong {thư mục, Local State, Default}.
    /// Dùng LastWriteTime của các mốc thay vì quét đệ quy (rẻ hơn, đủ tin cậy để phán "đang dùng hay bỏ").</summary>
    private static TimeSpan Age(string dir)
    {
        var newest = SafeLastWrite(dir);
        foreach (var probe in new[] { Path.Combine(dir, "Local State"), Path.Combine(dir, "Default") })
        {
            var t = SafeLastWrite(probe);
            if (t > newest) newest = t;
        }
        return DateTime.Now - newest;
    }

    private static DateTime SafeLastWrite(string path)
    {
        try
        {
            if (File.Exists(path)) return File.GetLastWriteTime(path);
            if (Directory.Exists(path)) return Directory.GetLastWriteTime(path);
        }
        catch { }
        return DateTime.MinValue;
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root)
    {
        try { return Directory.EnumerateDirectories(root).ToList(); }
        catch { return Array.Empty<string>(); }
    }
}
