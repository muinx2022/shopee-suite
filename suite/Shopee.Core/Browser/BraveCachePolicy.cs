using System.IO;
using System.Text.Json.Nodes;

namespace Shopee.Core.Browser;

/// <summary>Các hạng mục dọn profile Brave TRƯỚC khi phóng — chọn tổ hợp qua
/// <see cref="BraveCachePolicy.PrepareProfileForLaunch"/>. Cờ để mỗi call-site chỉ làm đúng phần nó cần
/// (Search chỉ cache; Import chỉ session; BrowserLauncher session + mark-clean; scrape cả ba).</summary>
[Flags]
public enum ProfileLaunchPrep
{
    None = 0,

    /// <summary>Xoá trạng thái phiên/tab (thư mục Sessions + các file Current/Last Session/Tabs) ở profile gốc
    /// và các profile con (Default, "Profile N") → mở lại KHÔNG khôi phục tab cũ. Chỉ đụng dữ liệu tái tạo được,
    /// KHÔNG đụng cookie/đăng nhập.</summary>
    ClearSessionRestore = 1,

    /// <summary>Xoá thư mục cache runtime tái tạo được trong Default (Service Worker, Code Cache, Cache, GPUCache)
    /// → Brave đăng ký lại service worker/extension sạch. KHÔNG đụng cookie/đăng nhập.</summary>
    ClearRuntimeCache = 2,

    /// <summary>Đặt exit_type="Normal" + exited_cleanly=true trong Default\Preferences → Brave coi lần trước
    /// thoát sạch, không hỏi/khôi phục.</summary>
    MarkCleanShutdown = 4,
}

/// <summary>
/// Chính sách CACHE dùng chung cho MỌI Brave do app phóng (Scrape, Check tài khoản, BigSeller login/update).
/// Gộp về một nơi để 3 việc luôn khớp: (1) cờ dòng lệnh giới hạn cache khi khởi chạy, (2) danh sách thư mục
/// cache TÁI TẠO ĐƯỢC bên trong 1 profile, (3) hàm dọn các thư mục đó.
///
/// LÝ DO tồn tại: profile Brave của app là BỀN (giữ cookie login chống captcha) và KHÔNG bao giờ bị xoá.
/// Nếu không chặn, mỗi profile phình cache không giới hạn — đo 02/07/2026: ~27 GB / 186 profile scrape là
/// cache thuần (Default\Cache, Safe Browsing, component_crx_cache, GPU/shader cache). Với 24 cửa sổ chạy
/// song song, cache mới ghi vài GB/giờ → ổ C đầy rất nhanh. Cache KHÔNG chứa cookie/đăng nhập nên xoá an
/// toàn: giữ nguyên Default\Network\Cookies + Local State → không mất phiên, không tăng captcha.
/// </summary>
public static class BraveCachePolicy
{
    /// <summary>
    /// Cờ dòng lệnh chặn cache phình khi chạy. Thêm vào MỌI lệnh phóng Brave của app.
    ///  - disk-cache-size: trần 50 MB cho Default\Cache (mặc định Chromium tự cap ~320 MB/profile).
    ///  - media-cache-size: trần 32 MB cho cache media.
    ///  - disable-gpu-shader-disk-cache: bỏ GrShaderCache/ShaderCache trên đĩa.
    ///  - disable-component-update: chặn tải component (~75 MB/profile: Widevine, danh sách…) → không cần cho scrape.
    /// </summary>
    public static readonly IReadOnlyList<string> DiskLimitArgs = new[]
    {
        "--disk-cache-size=52428800",
        "--media-cache-size=33554432",
        "--disable-gpu-shader-disk-cache",
        "--disable-component-update",
    };

    /// <summary>Các cờ trên nối bằng dấu cách — tiện nhét vào chuỗi args dựng sẵn.</summary>
    public static string DiskLimitArgString => string.Join(" ", DiskLimitArgs);

    /// <summary>
    /// Thư mục cache TÁI TẠO ĐƯỢC bên trong 1 profile Brave (đường dẫn tương đối so với gốc user-data-dir).
    /// Xoá an toàn: Chromium tự tạo lại, KHÔNG chứa cookie/đăng nhập. GIỮ LẠI: Local State, Default\Network\Cookies,
    /// Default\Preferences, Default\Login Data, Local/Session Storage…
    /// </summary>
    public static readonly IReadOnlyList<string> RegenerableCacheRelPaths = new[]
    {
        @"Default\Cache",
        @"Default\Code Cache",
        @"Default\GPUCache",
        @"Default\Service Worker",
        @"Default\DawnCache",
        @"Default\DawnGraphiteCache",
        @"Default\DawnWebGPUCache",
        "GrShaderCache",
        "ShaderCache",
        "GraphiteDawnCache",
        "component_crx_cache",
        "extensions_crx_cache",
        "Safe Browsing",
    };

    /// <summary>Xoá mọi thư mục cache tái tạo được trong 1 profile. Trả về SỐ BYTE ước tính đã giải phóng.
    /// Best-effort: mọi lỗi bị nuốt, không ném.</summary>
    public static long PruneProfileCache(string profileRoot)
    {
        if (string.IsNullOrWhiteSpace(profileRoot) || !Directory.Exists(profileRoot))
            return 0;

        long freed = 0;
        foreach (var rel in RegenerableCacheRelPaths)
            freed += DeleteDirBestEffort(Path.Combine(profileRoot, rel));
        return freed;
    }

    /// <summary>Xoá đệ quy 1 thư mục (best-effort, có bỏ cờ read-only rồi thử lại). Trả về byte đã giải phóng.</summary>
    public static long DeleteDirBestEffort(string dir)
    {
        if (!Directory.Exists(dir))
            return 0;

        long size = MeasureDir(dir);
        try
        {
            Directory.Delete(dir, recursive: true);
            return size;
        }
        catch
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(dir, recursive: true);
                return size;
            }
            catch { return 0; }
        }
    }

    /// <summary>Tổng kích thước file trong 1 thư mục (best-effort).</summary>
    public static long MeasureDir(string dir)
    {
        long size = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { size += new FileInfo(f).Length; } catch { }
            }
        }
        catch { }
        return size;
    }

    // ── Dọn profile trước khi phóng (gộp 4-5 bản riêng rẽ ở BrowserLauncher/Search/Import/scrape) ──

    /// <summary>File phiên/tab ở cấp thư mục profile — xoá để mở lại KHÔNG khôi phục tab cũ.</summary>
    private static readonly string[] SessionFileNames = { "Current Session", "Current Tabs", "Last Session", "Last Tabs" };

    /// <summary>Thư mục cache runtime tái tạo được trong Default — xoá để service worker/extension khởi động sạch.</summary>
    private static readonly string[] RuntimeCacheDirNames = { "Service Worker", "Code Cache", "Cache", "GPUCache" };

    /// <summary>
    /// Dọn profile Brave TRƯỚC khi phóng theo tổ hợp <paramref name="prep"/> — hợp nhất các bản dọn riêng rẽ
    /// trước đây (BrowserLauncher session+mark, Search cache, Import session, scrape cả ba). Best-effort:
    /// mọi lỗi bị nuốt, không ném. No-op nếu profile chưa tồn tại.
    /// </summary>
    public static void PrepareProfileForLaunch(string profileRoot, ProfileLaunchPrep prep)
    {
        if (string.IsNullOrWhiteSpace(profileRoot))
            return;
        var root = new DirectoryInfo(Path.GetFullPath(profileRoot));
        if (!root.Exists)
            return;

        if (prep.HasFlag(ProfileLaunchPrep.ClearRuntimeCache))
            ClearRuntimeCache(root.FullName);
        if (prep.HasFlag(ProfileLaunchPrep.ClearSessionRestore))
            ClearSessionRestore(root);
        if (prep.HasFlag(ProfileLaunchPrep.MarkCleanShutdown))
            MarkCleanShutdown(root.FullName);
    }

    private static void ClearRuntimeCache(string profileRoot)
    {
        var def = Path.Combine(profileRoot, "Default");
        foreach (var name in RuntimeCacheDirNames)
            DeleteDirBestEffort(Path.Combine(def, name));
    }

    private static void ClearSessionRestore(DirectoryInfo profileRoot)
    {
        // Quét profile gốc + các profile con (Default, "Profile N"): xoá file phiên/tab + trọn thư mục Sessions.
        var dirs = new List<DirectoryInfo> { profileRoot };
        try { dirs.AddRange(profileRoot.GetDirectories("Profile *")); } catch { }
        var def = new DirectoryInfo(Path.Combine(profileRoot.FullName, "Default"));
        if (def.Exists)
            dirs.Add(def);

        foreach (var dir in dirs)
        {
            foreach (var name in SessionFileNames)
            {
                try
                {
                    var p = Path.Combine(dir.FullName, name);
                    if (File.Exists(p)) File.Delete(p);
                }
                catch { }
            }
            DeleteDirBestEffort(Path.Combine(dir.FullName, "Sessions"));
        }
    }

    private static void MarkCleanShutdown(string profileRoot)
    {
        var prefPath = Path.Combine(profileRoot, "Default", "Preferences");
        if (!File.Exists(prefPath))
            return;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(prefPath)) as JsonObject;
            if (root is null)
                return;
            var profile = root["profile"] as JsonObject;
            if (profile is null)
            {
                profile = new JsonObject();
                root["profile"] = profile;
            }
            profile["exit_type"] = "Normal";
            profile["exited_cleanly"] = true;
            // UTF-8 KHÔNG BOM (File.WriteAllText mặc định) — an toàn cho JSON reader của Chromium.
            File.WriteAllText(prefPath, root.ToJsonString());
        }
        catch { }
    }
}
