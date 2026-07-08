namespace Shopee.Core.Browser;

/// <summary>
/// Dựng chuỗi tham số dòng lệnh cho Brave/Chromium dùng CHUNG cho mọi nơi app phóng Brave
/// (Core <see cref="BrowserLauncher"/>, Search BraveManager, MultiBrave scrape BraveProfileManager,
/// BigSeller runner Update/Import). Gộp các cờ TRÙNG LẶP về một chỗ (khối cửa sổ nền, giới hạn cache,
/// proxy, extension, remote-debugging-port) trong khi vẫn cho từng call-site thêm cờ RIÊNG của nó qua
/// <see cref="Add(string)"/>/<see cref="AddRange"/>. Builder chỉ nối cờ theo ĐÚNG thứ tự gọi → mỗi call-site
/// gọi các phương thức theo đúng thứ tự cờ gốc thì chuỗi kết quả GIỐNG HỆT bản cũ (refactor không đổi hành vi).
/// </summary>
public sealed class BraveArgsBuilder
{
    private readonly List<string> _parts = new();

    private BraveArgsBuilder() { }

    /// <summary>Builder rỗng (không cờ nền) — dùng cho runner CDP tự lắp từng cờ theo thứ tự riêng.</summary>
    public static BraveArgsBuilder Create() => new();

    /// <summary>Khối cờ NỀN cho cửa sổ Brave "thường" (BrowserLauncher, Search, MultiBrave scrape), theo đúng
    /// thứ tự 6 cờ đầu của cả 3 nơi: user-data-dir → profile-directory=Default → new-window → no-first-run →
    /// no-default-browser-check → hide-crash-restore-bubble. KHÔNG kèm cache-limit (thêm qua <see cref="DiskCacheLimit"/>).</summary>
    public static BraveArgsBuilder Window(string userDataDir)
    {
        var b = new BraveArgsBuilder();
        b._parts.Add($"--user-data-dir=\"{userDataDir}\"");
        b._parts.Add("--profile-directory=Default");
        b._parts.Add("--new-window");
        b._parts.Add("--no-first-run");
        b._parts.Add("--no-default-browser-check");
        b._parts.Add("--hide-crash-restore-bubble");
        return b;
    }

    public BraveArgsBuilder UserDataDir(string dir) { _parts.Add($"--user-data-dir=\"{dir}\""); return this; }
    public BraveArgsBuilder NoFirstRun() { _parts.Add("--no-first-run"); return this; }
    public BraveArgsBuilder NoDefaultBrowserCheck() { _parts.Add("--no-default-browser-check"); return this; }
    public BraveArgsBuilder RemoteDebuggingPort(int port) { _parts.Add($"--remote-debugging-port={port}"); return this; }
    public BraveArgsBuilder WindowSize(int width, int height) { _parts.Add($"--window-size={width},{height}"); return this; }
    public BraveArgsBuilder DisableGpu() { _parts.Add("--disable-gpu"); return this; }

    /// <summary>Nối các cờ giới hạn cache đĩa (<see cref="BraveCachePolicy.DiskLimitArgs"/>) — bắt buộc cho mọi
    /// profile bền để cache không phình. Kết quả nối bằng dấu cách giống <c>DiskLimitArgString</c>.</summary>
    public BraveArgsBuilder DiskCacheLimit() { _parts.AddRange(BraveCachePolicy.DiskLimitArgs); return this; }

    /// <summary>Thêm <c>--proxy-server=…</c> nếu <paramref name="proxy"/> không rỗng (no-op nếu rỗng, khớp mọi caller).</summary>
    public BraveArgsBuilder ProxyServer(string? proxy)
    {
        if (!string.IsNullOrWhiteSpace(proxy))
            _parts.Add($"--proxy-server={proxy}");
        return this;
    }

    /// <summary>Thêm <c>--load-extension="…"</c> (đường dẫn 1 extension hoặc chuỗi nhiều path ngăn bởi dấu phẩy).</summary>
    public BraveArgsBuilder LoadExtension(string path) { _parts.Add($"--load-extension=\"{path}\""); return this; }

    /// <summary>Thêm URL mở đầu (bọc ngoặc kép) — thường gọi cuối cùng.</summary>
    public BraveArgsBuilder StartUrl(string url) { _parts.Add($"\"{url}\""); return this; }

    /// <summary>Thêm 1 cờ RIÊNG của call-site (không có phương thức chuyên biệt).</summary>
    public BraveArgsBuilder Add(string flag) { _parts.Add(flag); return this; }

    /// <summary>Thêm nhiều cờ RIÊNG của call-site.</summary>
    public BraveArgsBuilder AddRange(IEnumerable<string> flags) { _parts.AddRange(flags); return this; }

    public string Build() => string.Join(" ", _parts);
}
