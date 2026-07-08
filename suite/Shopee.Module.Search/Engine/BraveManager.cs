namespace ShopeeStatApp.Services;

public sealed class BraveManager(AppSettingsService appSettings)
{
    private Process? _process;
    private int _cdpPort;
    private string? _currentProfileDir;

    public bool IsRunning => _process is { HasExited: false };
    public int CdpPort => _cdpPort;

    // App dùng Brave TOÀN BỘ (Search/Scrape/Check Account) để profile + cookie đăng nhập dùng chung,
    // không lẫn giữa các trình duyệt. Trước đây Search chạy Edge → cookie không tái dùng được với Scrape.
    public static string? DetectBravePath()
        => Shopee.Core.Browser.BrowserLauncher.Detect(Shopee.Core.Browser.BrowserKind.Brave)
           ?? FindExecutableOnPath("brave.exe");

    /// <summary>
    /// Resolves proxy. Returns null if not configured.
    /// Throws ProxyUnavailableException if kiotproxy key is set but API returns nothing.
    /// </summary>
    public async Task<string?> ResolveProxyAsync(InstanceConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.KiotProxyKey))
        {
            var (proxy, error) = await FetchKiotProxyAsync(config.KiotProxyKey, config.ProxyType);
            if (proxy is null)
                throw new InvalidOperationException(
                    $"Proxy lỗi: không lấy được proxy từ kiotproxy key \"{config.KiotProxyKey}\".\n" +
                    (string.IsNullOrWhiteSpace(error) ? "Kiểm tra key hoặc thử lại sau." : error));
            return proxy;
        }

        if (!string.IsNullOrWhiteSpace(config.ManualProxy))
        {
            var p = config.ManualProxy.Trim();
            return p.Contains("://") ? p : $"http://{p}";
        }

        // Bắt buộc proxy nhưng chưa cấu hình → KHÔNG chạy bằng IP máy thật (Shopee dễ verify/ban).
        // Khớp hành vi Brave engine (vốn đã enforce RequireProxy).
        if (config.RequireProxy)
            throw new InvalidOperationException(
                "Tài khoản bật \"Bắt buộc proxy\" nhưng chưa cấu hình proxy (KiotProxyKey/ManualProxy). " +
                "Thêm proxy hoặc tắt \"Bắt buộc proxy\" cho tài khoản này.");

        return null; // no proxy configured & not required — ok
    }

    public void Launch(InstanceConfig config, string profileDir, string? proxyServer, int wsPort)
    {
        // Ưu tiên BravePath cấu hình (nếu trỏ đúng brave.exe), nếu không thì tự dò Brave.
        var bravePath = appSettings.Settings.BravePath;
        if (string.IsNullOrWhiteSpace(bravePath) || !File.Exists(bravePath) ||
            !bravePath.EndsWith("brave.exe", StringComparison.OrdinalIgnoreCase))
            bravePath = DetectBravePath()
                ?? throw new FileNotFoundException(
                    "Khong tim thay brave.exe. Hay cai Brave Browser hoac cau hinh duong dan brave trong settings.json.");

        var extPath = ResolveExtensionPath();

        // Close only the Brave instance/profile managed by this app.
        Kill();
        ClearExtensionRuntimeCache(profileDir);

        _cdpPort = Shopee.Core.Infrastructure.PortAllocator.Reserve();
        _currentProfileDir = Path.GetFullPath(profileDir);
        var args = BuildArgs(_cdpPort, profileDir, proxyServer, extPath, wsPort);
        // Phóng qua BraveJobObject (KILL_ON_JOB_CLOSE): app tắt/crash/force-kill → OS tự giết Brave này,
        // không để lại cửa sổ mồ côi. Trước đây Search dùng Process.Start trần → Brave sống sót qua crash.
        _process = Shopee.Core.Browser.BraveJobObject.Start(bravePath, args);
    }

    public async Task CleanupRestoredTabsAsync(int wsPort, CancellationToken ct = default)
    {
        if (_cdpPort <= 0) return;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        JsonDocument? targetsDoc = null;

        try
        {
            var deadline = Environment.TickCount64 + 6_000;
            while (Environment.TickCount64 < deadline)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // 127.0.0.1 (KHÔNG "localhost") — Windows phân giải ::1 IPv6 trước → CDP chậm/timeout.
                    var json = await http.GetStringAsync($"http://127.0.0.1:{_cdpPort}/json/list", ct);
                    targetsDoc = JsonDocument.Parse(json);
                    if (targetsDoc.RootElement.GetArrayLength() > 0) break;
                }
                catch
                {
                    targetsDoc?.Dispose();
                    targetsDoc = null;
                    await Task.Delay(300, ct);
                }
            }

            if (targetsDoc is null) return;

            var pageTargets = targetsDoc.RootElement.EnumerateArray()
                .Where(t => GetJsonString(t, "type") == "page")
                .Select(t => new
                {
                    Id = GetJsonString(t, "id"),
                    Url = GetJsonString(t, "url"),
                })
                .Where(t => !string.IsNullOrWhiteSpace(t.Id))
                .ToList();

            if (pageTargets.Count <= 1) return;

            var keep =
                pageTargets.FirstOrDefault(t => t.Url.Contains($"_ss_ws={wsPort}", StringComparison.OrdinalIgnoreCase))
                ?? pageTargets.FirstOrDefault(t =>
                    t.Url.Contains("_ss_ws=", StringComparison.OrdinalIgnoreCase))
                ?? pageTargets.FirstOrDefault(t =>
                    t.Url.Contains("shopee.vn", StringComparison.OrdinalIgnoreCase)
                    && !t.Url.Contains("shopee.vn/api/", StringComparison.OrdinalIgnoreCase))
                ?? pageTargets[0];

            foreach (var target in pageTargets.Where(t => t.Id != keep.Id))
            {
                try
                {
                    await http.GetStringAsync(
                        $"http://127.0.0.1:{_cdpPort}/json/close/{Uri.EscapeDataString(target.Id)}",
                        ct);
                }
                catch { }
            }
        }
        finally
        {
            targetsDoc?.Dispose();
        }
    }

    private static string GetJsonString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) ? value.GetString() ?? "" : "";

    private static string? FindExecutableOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch { }
        }

        return null;
    }

    private string ResolveExtensionPath()
    {
        var tried = new List<string>();
        var configured = appSettings.Settings.ExtensionPath;

        if (!string.IsNullOrWhiteSpace(configured))
        {
            var resolved = Path.GetFullPath(configured);
            tried.Add(resolved);
            if (IsExtensionDir(resolved))
                return resolved;
        }

        foreach (var candidate in GetExtensionPathCandidates())
        {
            var resolved = Path.GetFullPath(candidate);
            if (tried.Any(x => string.Equals(x, resolved, StringComparison.OrdinalIgnoreCase)))
                continue;

            tried.Add(resolved);
            if (!IsExtensionDir(resolved))
                continue;

            appSettings.Settings.ExtensionPath = resolved;
            try { appSettings.SaveSettings(); } catch { }
            return resolved;
        }

        throw new DirectoryNotFoundException(
            "Khong tim thay extension Shopee Search. Cau hinh extensionPath trong settings.json hoac dat thu muc extensions\\shopee-search canh repo/exe." +
            Environment.NewLine + "Da thu:" + Environment.NewLine +
            string.Join(Environment.NewLine, tried.Select(x => "- " + x)));
    }

    private static IEnumerable<string> GetExtensionPathCandidates()
    {
        var baseDir = Path.GetFullPath(AppContext.BaseDirectory);
        for (var dir = new DirectoryInfo(baseDir); dir is not null; dir = dir.Parent)
            yield return Path.Combine(dir.FullName, "..", "extensions", "shopee-search");

        yield return Path.Combine(AppContext.BaseDirectory, "extensions", "shopee-search");

        // Tìm thêm extensions/shopee-search ở repo root (khi chạy từ thư mục suite).
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var cand = Path.Combine(dir.FullName, "extensions", "shopee-search");
            if (Directory.Exists(cand)) { yield return cand; break; }
        }
    }

    private static bool IsExtensionDir(string path) =>
        Directory.Exists(path) && File.Exists(Path.Combine(path, "manifest.json"));

    /// <summary>Liệt kê tiến trình đang tham chiếu profile này ("tên#pid, …") để chẩn đoán khi KHÔNG tạo được
    /// profile — quét MỌI tiến trình có đường dẫn profile trong command line (không chỉ Brave) để lộ cả kẻ lạ.</summary>
    internal static string DescribeProfileHolders(string profileDir)
    {
        var needle = Path.GetFullPath(profileDir).TrimEnd('\\', '/');
        var found = new List<string>();
        // Quét MỌI tiến trình (names=null) để lộ cả kẻ lạ đang giữ profile, không chỉ Brave.
        foreach (var p in Shopee.Core.Platform.PlatformServices.ProcessFinder.Enumerate(null))
        {
            if (p.CommandLine.Contains(needle, StringComparison.OrdinalIgnoreCase))
                found.Add($"{(string.IsNullOrEmpty(p.Name) ? "?" : p.Name)}#{p.Pid}");
        }
        return string.Join(", ", found);
    }

    private static void ClearExtensionRuntimeCache(string profileDir)
    {
        try
        {
            var defaultDir = Path.Combine(Path.GetFullPath(profileDir), "Default");
            var cacheDirs = new[]
            {
                Path.Combine(defaultDir, "Service Worker"),
                Path.Combine(defaultDir, "Code Cache"),
                Path.Combine(defaultDir, "Cache"),
                Path.Combine(defaultDir, "GPUCache"),
            };

            foreach (var dir in cacheDirs)
            {
                try
                {
                    if (Directory.Exists(dir))
                        Directory.Delete(dir, recursive: true);
                }
                catch { }
            }
        }
        catch { }
    }

    private static string BuildArgs(int cdpPort, string userDataDir, string? proxy, string extPath, int wsPort)
    {
        var parts = new List<string>
        {
            $"--user-data-dir=\"{userDataDir}\"",
            "--profile-directory=Default",
            "--new-window",
            "--no-first-run",
            "--no-default-browser-check",
            "--hide-crash-restore-bubble",
            "--no-restore-last-session",
            "--restore-last-session=false",
            "--disable-background-mode",
            // Brave/Chrome 137+ mặc định chặn --load-extension (DisableLoadExtensionCommandLineSwitch) →
            // tắt feature đó để extension Shopee Search load được (giống engine Scrape).
            "--disable-features=DisableLoadExtensionCommandLineSwitch",
            $"--remote-debugging-port={cdpPort}",
            $"--load-extension=\"{extPath}\"",
        };

        // Profile Search cũng BỀN (giữ cookie login) → phải chặn cache phình như mọi Brave khác của app.
        // Thiếu bước này chính là nguồn cache 27 GB đã đo (xem BraveCachePolicy).
        parts.AddRange(Shopee.Core.Browser.BraveCachePolicy.DiskLimitArgs);

        if (!string.IsNullOrWhiteSpace(proxy))
            parts.Add($"--proxy-server={proxy}");

        // Extension reads WS port from URL hash on first tab
        parts.Add($"\"https://shopee.vn/#_ss_ws={wsPort}\"");

        return string.Join(" ", parts);
    }

    private static async Task<(string? Proxy, string? Error)> FetchKiotProxyAsync(string key, string proxyType)
    {
        // ƯU TIÊN /current: giữ IP hiện hành của key (sống ~30') → login Shopee và search DÙNG CHUNG 1 IP →
        // tránh captcha do nhảy IP. CHỈ /new khi /current chưa có proxy (key chưa kích hoạt / hết hạn). Dùng
        // chung KiotProxyClient (Core) — cùng URL/schema với Scrape/Update, không nhân bản parse proxy nữa.
        var current = await Shopee.Core.Proxy.KiotProxyClient.FetchCurrentAsync(key, default, proxyType);
        if (current.Proxy is not null)
            return (current.Proxy, null);

        var fresh = await Shopee.Core.Proxy.KiotProxyClient.FetchNewAsync(key, default, proxyType);
        return fresh.Proxy is not null ? (fresh.Proxy, null) : (null, fresh.Error ?? current.Error);
    }

    public void Kill()
    {
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch { }

        // Giải phóng handle tiến trình. Trước đây chỉ gán _process=null → mỗi lần relaunch (tab
        // "Tìm theo file" relaunch MỖI link) rò một SafeProcessHandle → tích luỹ → góp phần đơ máy.
        try { _process?.Dispose(); } catch { }

        if (!string.IsNullOrWhiteSpace(_currentProfileDir))
        {
            // Giết ĐÚNG Brave của profile này theo giá trị --user-data-dir (khớp CHÍNH XÁC, không Contains →
            // không giết nhầm acc_1 vs acc_10). Nếu có process bị giết, chờ ngắn cho khoá profile (delete-pending) buông.
            if (Shopee.Core.Browser.BraveProcessReaper.KillByUserDataDir(
                    _currentProfileDir, includeCrashpadOrphans: true) > 0)
                Thread.Sleep(400);
        }

        // The CDP port was reserved for this browser's lifetime; free it now that Brave is gone.
        if (_cdpPort > 0)
        {
            Shopee.Core.Infrastructure.PortAllocator.Release(_cdpPort);
            _cdpPort = 0;
        }

        _process = null;
    }
}

