namespace Shopee.Toolkit.Browser;

/// <summary>
/// Dựng tham số dòng lệnh cho Brave/Chromium dùng CHUNG cho mọi nơi app phóng trình duyệt — cả phía suite
/// (Core <c>BrowserLauncher</c>, Search BraveManager, MultiBrave scrape BraveProfileManager, BigSeller runner
/// Update/Import) lẫn phía orders (cầu nối Đơn hàng, POC "mở sạch"). Gộp các cờ TRÙNG LẶP về một chỗ (khối
/// cửa sổ nền, giới hạn cache, proxy, extension, remote-debugging-port) trong khi vẫn cho từng call-site thêm
/// cờ RIÊNG của nó qua <see cref="Add(string)"/>/<see cref="AddRange"/>. Builder chỉ nối cờ theo ĐÚNG thứ tự
/// gọi → mỗi call-site gọi các phương thức theo đúng thứ tự cờ gốc thì kết quả GIỐNG HỆT bản cũ (refactor
/// không đổi hành vi).
/// <para>
/// KHÁC BIỆT DUY NHẤT giữa hai phía là CÁCH GIAO args cho tiến trình, nên nó được tham số hoá bằng chế độ
/// khởi tạo chứ không phải hai lớp:
/// <list type="bullet">
/// <item><see cref="Create"/>/<see cref="Window"/> — chế độ CHUỖI: giá trị đường dẫn/URL được bọc ngoặc kép
/// vì kết quả <see cref="Build"/> đi vào <c>Process.Start(exe, argsString)</c> (phía suite).</item>
/// <item><see cref="CreateRaw"/>/<see cref="WindowRaw"/> — chế độ DANH SÁCH: KHÔNG bọc ngoặc vì kết quả
/// <see cref="BuildList"/> đi vào <c>ProcessStartInfo.ArgumentList</c> / <c>args</c> của Playwright, nơi mỗi
/// phần tử đã là một tham số riêng (bọc ngoặc ở đây sẽ lọt dấu " vào chính giá trị) (phía orders).</item>
/// </list>
/// </para>
/// </summary>
public sealed class BraveArgs
{
    private readonly List<string> _parts = new();
    private readonly bool _quotePaths;

    private BraveArgs(bool quotePaths) => _quotePaths = quotePaths;

    /// <summary>Bọc ngoặc kép giá trị đường dẫn/URL khi ở chế độ CHUỖI; giữ nguyên khi ở chế độ DANH SÁCH.</summary>
    private string Q(string value) => _quotePaths ? $"\"{value}\"" : value;

    /// <summary>Builder rỗng chế độ CHUỖI (không cờ nền) — dùng cho runner CDP tự lắp từng cờ theo thứ tự riêng.</summary>
    public static BraveArgs Create() => new(quotePaths: true);

    /// <summary>Builder rỗng chế độ DANH SÁCH (không cờ nền) — cho call-site truyền args dạng mảng.</summary>
    public static BraveArgs CreateRaw() => new(quotePaths: false);

    /// <summary>Khối cờ NỀN cho cửa sổ Brave "thường" (BrowserLauncher, Search, MultiBrave scrape), theo đúng
    /// thứ tự 6 cờ đầu của cả 3 nơi: user-data-dir → profile-directory=Default → new-window → no-first-run →
    /// no-default-browser-check → hide-crash-restore-bubble. KHÔNG kèm cache-limit (thêm qua <see cref="DiskCacheLimit"/>).</summary>
    public static BraveArgs Window(string userDataDir) => Create().WindowBlock(userDataDir);

    /// <summary>Như <see cref="Window"/> nhưng ở chế độ DANH SÁCH (không bọc ngoặc).</summary>
    public static BraveArgs WindowRaw(string userDataDir) => CreateRaw().WindowBlock(userDataDir);

    /// <summary>Nối khối 6 cờ nền cửa sổ vào builder ĐANG có (dùng khi call-site cần cờ khác đứng TRƯỚC khối này,
    /// vd cầu nối Đơn hàng đặt --remote-debugging-port lên đầu).</summary>
    public BraveArgs WindowBlock(string userDataDir)
    {
        _parts.Add($"--user-data-dir={Q(userDataDir)}");
        _parts.Add("--profile-directory=Default");
        _parts.Add("--new-window");
        _parts.Add("--no-first-run");
        _parts.Add("--no-default-browser-check");
        _parts.Add("--hide-crash-restore-bubble");
        return this;
    }

    public BraveArgs UserDataDir(string dir) { _parts.Add($"--user-data-dir={Q(dir)}"); return this; }
    public BraveArgs NoFirstRun() { _parts.Add("--no-first-run"); return this; }
    public BraveArgs NoDefaultBrowserCheck() { _parts.Add("--no-default-browser-check"); return this; }
    public BraveArgs RemoteDebuggingPort(int port) { _parts.Add($"--remote-debugging-port={port}"); return this; }
    public BraveArgs WindowSize(int width, int height) { _parts.Add($"--window-size={width},{height}"); return this; }
    public BraveArgs DisableGpu() { _parts.Add("--disable-gpu"); return this; }

    /// <summary>
    /// Cờ dòng lệnh chặn cache phình khi chạy — NGUỒN DUY NHẤT (<c>Shopee.Core.Browser.BraveCachePolicy.DiskLimitArgs</c>
    /// trỏ về đây). Thêm vào MỌI lệnh phóng Brave của app:
    ///  - disk-cache-size: trần 50 MB cho Default\Cache (mặc định Chromium tự cap ~320 MB/profile).
    ///  - media-cache-size: trần 32 MB cho cache media.
    ///  - disable-gpu-shader-disk-cache: bỏ GrShaderCache/ShaderCache trên đĩa.
    ///  - disable-component-update: chặn tải component (~75 MB/profile: Widevine, danh sách…) → không cần cho scrape.
    /// </summary>
    public static readonly IReadOnlyList<string> DiskCacheLimitFlags = new[]
    {
        "--disk-cache-size=52428800",
        "--media-cache-size=33554432",
        "--disable-gpu-shader-disk-cache",
        "--disable-component-update",
    };

    /// <summary>Nối các cờ giới hạn cache đĩa (<see cref="DiskCacheLimitFlags"/>) — bắt buộc cho mọi profile bền
    /// để cache không phình.</summary>
    public BraveArgs DiskCacheLimit() { _parts.AddRange(DiskCacheLimitFlags); return this; }

    /// <summary>Thêm <c>--proxy-server=…</c> nếu <paramref name="proxy"/> không rỗng (no-op nếu rỗng, khớp mọi caller).</summary>
    public BraveArgs ProxyServer(string? proxy)
    {
        if (!string.IsNullOrWhiteSpace(proxy))
            _parts.Add($"--proxy-server={proxy}");
        return this;
    }

    /// <summary>Thêm <c>--load-extension=…</c> (đường dẫn 1 extension hoặc chuỗi nhiều path ngăn bởi dấu phẩy).</summary>
    public BraveArgs LoadExtension(string path) { _parts.Add($"--load-extension={Q(path)}"); return this; }

    /// <summary>Thêm URL mở đầu (positional) — thường gọi cuối cùng.</summary>
    public BraveArgs StartUrl(string url) { _parts.Add(Q(url)); return this; }

    /// <summary>Thêm 1 cờ RIÊNG của call-site (không có phương thức chuyên biệt).</summary>
    public BraveArgs Add(string flag) { _parts.Add(flag); return this; }

    /// <summary>Thêm nhiều cờ RIÊNG của call-site.</summary>
    public BraveArgs AddRange(IEnumerable<string> flags) { _parts.AddRange(flags); return this; }

    /// <summary>Kết quả dạng CHUỖI (nối bằng dấu cách) cho <c>Process.Start(exe, argsString)</c>.</summary>
    public string Build() => string.Join(" ", _parts);

    /// <summary>Kết quả dạng DANH SÁCH cho <c>ProcessStartInfo.ArgumentList</c> / <c>args</c> của Playwright.
    /// Trả bản CHỈ ĐỌC để caller không sửa ngược vào builder.</summary>
    public IReadOnlyList<string> BuildList() => _parts.AsReadOnly();
}
