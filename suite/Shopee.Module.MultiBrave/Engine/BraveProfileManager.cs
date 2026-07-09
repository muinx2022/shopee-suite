using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenMultiBraveLauncherV3;

internal static class BraveProfileManager
{
    /// <summary>
    /// Thư mục profile của instance — LUÔN nằm trong persistent-data (bền) để GIỮ cookie login
    /// Shopee qua các phiên app. Trước đây profile Shopee sống ở runtime-sessions (xoá mỗi
    /// phiên) → mất login → mỗi lần mở phải tự đăng nhập lại → Shopee coi là thiết bị lạ và bắn captcha.
    /// Dùng chung cho EnsureProfile + ResolveProfileRoot + "mở thư mục profile" để 3 nơi luôn khớp.
    /// </summary>
    public static DirectoryInfo GetProfileRootDirectory(InstanceConfig config)
    {
        config.EnsureProfileRelativePath();
        var rootBase = AppSession.ResolvePersistentDataPath();
        return new DirectoryInfo(
            Path.Combine(rootBase, config.ProfileRelativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    public static DirectoryInfo EnsureProfile(DirectoryInfo sourceUserData, InstanceConfig config, Action<string>? log = null)
    {
        var profileRoot = GetProfileRootDirectory(config);
        var targetDefault = new DirectoryInfo(Path.Combine(profileRoot.FullName, "Default"));

        var sourceDefault = new DirectoryInfo(Path.Combine(sourceUserData.FullName, "Default"));
        if (!sourceDefault.Exists)
            throw new DirectoryNotFoundException($"Khong tim thay profile Default: {sourceDefault.FullName}");

        if (config.CreateNewProfileOnNextStart || !targetDefault.Exists)
        {
            if (profileRoot.Exists)
            {
                try
                {
                    Directory.Delete(profileRoot.FullName, recursive: true);
                }
                catch
                {
                    foreach (var f in Directory.EnumerateFiles(profileRoot.FullName, "*", SearchOption.AllDirectories))
                    {
                        try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                    }
                    Directory.Delete(profileRoot.FullName, recursive: true);
                }
            }

            profileRoot.Create();
            targetDefault.Create();
            CopyExtensionState(sourceDefault, targetDefault);
            config.CreateNewProfileOnNextStart = false;
            log?.Invoke("Da tao profile moi tu User Data mau.");
        }
        else
        {
            profileRoot.Create();
            log?.Invoke("Toi se dung profile hien co.");
        }

        return profileRoot;
    }

    public static void PrepareProfileForLaunch(string profileRoot)
    {
        ClearCopiedExtensionInstallState(profileRoot);
        ClearHeavyReusableCaches(profileRoot);
        // Gộp về Core: dọn cache runtime (SW/Code Cache/Cache/GPUCache) + trạng thái phiên/tab + đánh dấu
        // thoát sạch. Thứ tự với ClearHeavyReusableCaches không quan trọng (khác hạng mục file).
        Shopee.Core.Browser.BraveCachePolicy.PrepareProfileForLaunch(
            profileRoot,
            Shopee.Core.Browser.ProfileLaunchPrep.ClearRuntimeCache
            | Shopee.Core.Browser.ProfileLaunchPrep.ClearSessionRestore
            | Shopee.Core.Browser.ProfileLaunchPrep.MarkCleanShutdown);
    }

    /// <summary>Xoá các thư mục cache NẶNG, tái tạo được, KHÔNG chứa cookie/đăng nhập (Default\Cache, GPUCache,
    /// Safe Browsing, component_crx_cache, shader/Dawn cache…) trước mỗi lần launch → profile bền không phình
    /// dần qua các phiên chạy dài. Cookie (Default\Network\Cookies) + Local State GIỮ NGUYÊN nên không mất
    /// login/không tăng captcha. Dùng CHUNG danh sách với StartupJanitor (BraveCachePolicy) để không lệch nhau.</summary>
    private static void ClearHeavyReusableCaches(string profileRoot)
    {
        Shopee.Core.Browser.BraveCachePolicy.PruneProfileCache(profileRoot);
    }

    public static string BuildBraveArguments(
        int cdpPort,
        string userDataDir,
        string? proxyServer,
        Action<string>? log = null,
        string? sourceUserData = null,
        bool loadRunnerExtension = true,
        string? bigSellerProxyServer = null)
    {
        // Khối 6 cờ nền cửa sổ (BraveArgsBuilder.Window) + remote-debugging-port, rồi cờ RIÊNG scrape giữ nguyên thứ tự gốc.
        var parts = Shopee.Core.Browser.BraveArgsBuilder.Window(userDataDir)
            .RemoteDebuggingPort(cdpPort)

            // GIỮ CỬA SỔ NỀN LUÔN HOẠT ĐỘNG. Khi chạy nhiều instance, mở/đưa cửa sổ instance khác lên
            // trước làm các cửa sổ còn lại bị che (occluded)/chạy nền → Brave bóp timer + renderer +
            // service worker → scrape "đứng hình" (đúng triệu chứng: kẹt lúc mở sang instance khác).
            // Các cờ dưới tắt toàn bộ cơ chế tiết kiệm tài nguyên đó để mọi instance scrape song song ổn định.
            .Add("--disable-background-timer-throttling")
            .Add("--disable-backgrounding-occluded-windows")
            .Add("--disable-renderer-backgrounding")
            // DisableLoadExtensionCommandLineSwitch: Brave/Chrome 137+ MẶC ĐỊNH chặn --load-extension
            // → extension "Shopee Data Runner" KHÔNG load. Tắt feature này để --load-extension hoạt động lại.
            .Add("--disable-features=CalculateNativeWinOcclusion,IntensiveWakeUpThrottling,DisableLoadExtensionCommandLineSwitch")
            // Chặn cache phình khi 24 cửa sổ chạy song song (profile bền → cache không tự dọn). Xem BraveCachePolicy.
            .DiskCacheLimit();
        if (!string.IsNullOrWhiteSpace(bigSellerProxyServer))
        {
            // TK BIGSELLER CÓ PROXY RIÊNG → split-tunnel qua PAC: bigseller.com đi proxy RIÊNG của tk
            // BigSeller (mỗi tk 1 IP → chạy SONG SONG nhiều tk không bị "nhiều token / 1 IP" → không đá
            // phiên), còn lại (Shopee) GIỮ proxy instance, localhost đi DIRECT (API dữ liệu 127.0.0.1:8012
            // KHÔNG qua proxy). Không dùng được --proxy-server cho việc này vì nó áp cho TOÀN browser.
            var pacUrl = WriteBigSellerSplitPac(userDataDir, proxyServer, bigSellerProxyServer, log);
            if (pacUrl is not null)
            {
                parts.Add($"--proxy-pac-url=\"{pacUrl}\"");
            }
            else if (!string.IsNullOrWhiteSpace(proxyServer))
            {
                // Ghi PAC lỗi → quay về hành vi cũ (BigSeller qua IP máy) để Shopee KHÔNG mất proxy.
                parts.Add($"--proxy-server={proxyServer}");
                parts.Add("--proxy-bypass-list=*.bigseller.com;bigseller.com;*.bigseller.pro;bigseller.pro");
            }
        }
        else if (!string.IsNullOrWhiteSpace(proxyServer))
        {
            // BigSeller đi qua 1 IP CHUNG (IP máy) cho MỌI instance; Shopee vẫn giữ proxy riêng của instance.
            // LÝ DO (24/06, theo log thực tế): khi cho bigseller đi proxy RIÊNG từng instance thì 1 token
            // BigSeller bị phơi trên ~10 IP residential cùng lúc → server coi là CHIA SẺ tài khoản → THU HỒI
            // phiên (xóa muc_token) chỉ sau ~40s → "log in first" hàng loạt. Dồn bigseller về 1 IP máy →
            // server thấy "1 token / 1 IP / nhiều Brave" (BigSeller dung thứ kiểu này) → bền phiên hơn nhiều.
            // (localhost/127.* Chromium tự bypass mặc định nên API dữ liệu local 127.0.0.1 vẫn đi thẳng.)
            parts.Add($"--proxy-server={proxyServer}");
            parts.Add("--proxy-bypass-list=*.bigseller.com;bigseller.com;*.bigseller.pro;bigseller.pro");
        }

        string? runnerPath = null;
        if (loadRunnerExtension)
        {
            runnerPath = RunnerExtensionPaths.ResolveLoadDirectory();
            if (runnerPath is null)
                throw new InvalidOperationException(
                    "Không tìm thấy extension 'Shopee Data Runner' (thiếu background.js). " +
                    "Cần có thư mục 'extensions/shopee-scrape/' cạnh app (đã bundle) hoặc trong repo.");
        }

        var extPaths = CollectExtensionLoadPaths(runnerPath);

        // Nạp THÊM các extension người dùng đã cài trong Brave mặc định (vd ext "product copy" của
        // BigSeller) vào profile scrape — vì profile scrape là bản tách riêng, không có sẵn ext đã cài.
        var userExts = CollectUserExtensions(sourceUserData);
        foreach (var (dir, _) in userExts)
            extPaths.Add(dir);
        if (userExts.Count > 0)
            log?.Invoke($"Nạp {userExts.Count} extension từ Brave mặc định: {string.Join(", ", userExts.Select(e => e.name))}.");

        // Scrape BẮT BUỘC có extension "Product Copy" của BigSeller (cài trong Brave mặc định) → nếu
        // thiếu thì dừng ngay, báo người dùng cài extension cho Brave trước (login flow không cần nên bỏ qua).
        if (loadRunnerExtension)
        {
            var hasProductCopy = userExts.Any(e => e.name.Contains("Product Copy", StringComparison.OrdinalIgnoreCase));
            if (!hasProductCopy)
            {
                log?.Invoke("✖ Không tìm thấy extension 'Product Copy' của BigSeller trong Brave — DỪNG.");
                throw new InvalidOperationException(
                    "Chưa cài extension 'Product Copy' của BigSeller cho Brave.\n\n" +
                    "Hãy CÀI ĐẶT EXTENSION CHO BRAVE TRƯỚC (mở Brave thường → cài extension 'Product Copy' của BigSeller), " +
                    "rồi chạy lại Scrape.");
            }
        }

        if (extPaths.Count > 0)
            parts.Add($"--load-extension=\"{string.Join(",", extPaths)}\"");

        return parts.Build();
    }

    private static List<string> CollectExtensionLoadPaths(string? runnerPath)
    {
        var paths = new List<string>();
        if (runnerPath is not null)
            paths.Add(runnerPath);
        return paths;
    }

    /// <summary>Liệt kê các extension người dùng đã cài trong Brave mặc định (sourceUserData) để
    /// nạp vào profile scrape qua --load-extension. Bỏ qua theme và chính "Shopee Data Runner".</summary>
    private static List<(string dir, string name)> CollectUserExtensions(string? sourceUserData)
    {
        var result = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(sourceUserData))
            return result;

        var extRoot = Path.Combine(sourceUserData, "Default", "Extensions");
        if (!Directory.Exists(extRoot))
            return result;

        foreach (var extIdDir in Directory.GetDirectories(extRoot))
        {
            try
            {
                // Mỗi extId có 1+ thư mục version (vd "1.2.3_0"); lấy thư mục có manifest.json mới nhất.
                string? versionDir = null;
                foreach (var vd in Directory.GetDirectories(extIdDir).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    if (File.Exists(Path.Combine(vd, "manifest.json"))) { versionDir = vd; break; }
                }
                if (versionDir is null)
                    continue;

                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(versionDir, "manifest.json")));
                var root = doc.RootElement;
                if (root.TryGetProperty("theme", out _))
                    continue; // bỏ theme
                var name = root.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                // Tên có thể là placeholder localized "__MSG_key__" (vd ext "Product Copy" → __MSG_crawl_title_01__);
                // resolve ra tên thật để check "Product Copy" không bị trượt.
                name = ResolveLocalizedName(name, versionDir, root);
                if (name.Contains("Shopee Data Runner", StringComparison.OrdinalIgnoreCase))
                    continue; // tránh nạp trùng runner (đã load qua bản bundle)
                result.Add((versionDir, string.IsNullOrWhiteSpace(name) ? Path.GetFileName(extIdDir) : name));
            }
            catch { }
        }
        return result;
    }

    /// <summary>Tên extension trong manifest có thể là placeholder localized "__MSG_key__" (tên thật nằm
    /// trong _locales/{default_locale}/messages.json). Resolve để so khớp đúng tên hiển thị.</summary>
    private static string ResolveLocalizedName(string name, string versionDir, JsonElement manifest)
    {
        if (!name.StartsWith("__MSG_", StringComparison.Ordinal) || !name.EndsWith("__", StringComparison.Ordinal))
            return name;
        try
        {
            var key = name[6..^2];   // bỏ "__MSG_" đầu và "__" cuối
            var locale = manifest.TryGetProperty("default_locale", out var dl) ? (dl.GetString() ?? "en") : "en";
            foreach (var loc in new[] { locale, "en" })
            {
                if (string.IsNullOrWhiteSpace(loc)) continue;
                var msgPath = Path.Combine(versionDir, "_locales", loc, "messages.json");
                if (!File.Exists(msgPath)) continue;
                using var md = JsonDocument.Parse(File.ReadAllText(msgPath));
                foreach (var prop in md.RootElement.EnumerateObject())
                {
                    if (!string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase)) continue;
                    if (prop.Value.TryGetProperty("message", out var msg))
                        return msg.GetString() ?? name;
                }
            }
        }
        catch { }
        return name;
    }

    /// <summary>
    /// Ghi file PAC split-tunnel vào profile rồi trả về URL <c>file://</c> cho <c>--proxy-pac-url</c>:
    /// bigseller.* → proxy RIÊNG của tk BigSeller; localhost → DIRECT (API dữ liệu local); còn lại →
    /// proxy Shopee (hoặc DIRECT nếu Shopee không proxy). Trả null nếu ghi lỗi (caller fallback IP máy).
    /// </summary>
    private static string? WriteBigSellerSplitPac(
        string userDataDir, string? shopeeProxyServer, string bigSellerProxyServer, Action<string>? log)
    {
        try
        {
            var bigSeller = ToPacProxy(bigSellerProxyServer);
            var rest = ToPacProxy(shopeeProxyServer);   // "DIRECT" khi Shopee không proxy
            var pac =
                "function FindProxyForURL(url, host) {\n" +
                "  if (host == \"localhost\" || host == \"127.0.0.1\" || shExpMatch(host, \"127.*\") || host == \"[::1]\") return \"DIRECT\";\n" +
                "  if (host == \"bigseller.com\" || dnsDomainIs(host, \".bigseller.com\") || host == \"bigseller.pro\" || dnsDomainIs(host, \".bigseller.pro\")) return \"" + bigSeller + "\";\n" +
                "  return \"" + rest + "\";\n" +
                "}\n";

            var pacPath = Path.Combine(userDataDir, "bigseller-split.pac");
            File.WriteAllText(pacPath, pac, Encoding.UTF8);
            return new Uri(pacPath).AbsoluteUri;   // file:///D:/.../bigseller-split.pac (đã escape khoảng trắng)
        }
        catch (Exception ex)
        {
            log?.Invoke($"Không ghi được PAC BigSeller ({ex.Message}) — BigSeller tạm đi IP máy.");
            return null;
        }
    }

    /// <summary>Chuyển chuỗi proxy-server ("http://h:p" / "socks5://h:p") sang token PAC ("PROXY h:p" /
    /// "SOCKS5 h:p"). Trống → "DIRECT".</summary>
    private static string ToPacProxy(string? proxyServer)
    {
        if (string.IsNullOrWhiteSpace(proxyServer))
            return "DIRECT";
        var s = proxyServer.Trim();
        var kind = "PROXY";
        foreach (var (scheme, pac) in new[] { ("socks5://", "SOCKS5"), ("socks4://", "SOCKS"), ("https://", "PROXY"), ("http://", "PROXY") })
        {
            if (s.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) { kind = pac; s = s[scheme.Length..]; break; }
        }
        return $"{kind} {s}";
    }

    private static void CopyExtensionState(DirectoryInfo src, DirectoryInfo dst)
    {
        foreach (var file in new[] { "Preferences", "Secure Preferences", "Bookmarks" })
        {
            var s = Path.Combine(src.FullName, file);
            var d = Path.Combine(dst.FullName, file);
            if (File.Exists(s))
                File.Copy(s, d, overwrite: true);
        }

        SanitizeCopiedExtensionPreferences(Path.Combine(dst.FullName, "Preferences"));
        SanitizeCopiedExtensionPreferences(Path.Combine(dst.FullName, "Secure Preferences"));
    }

    private static void ClearCopiedExtensionInstallState(string profileRoot)
    {
        var defaultDir = Path.Combine(profileRoot, "Default");
        foreach (var dir in new[]
        {
            "Extensions",
            "Extension Rules",
            "Extension State",
            "Managed Extension State",
            "Sync Extension Settings",
        })
        {
            DeleteDirectoryQuietly(Path.Combine(defaultDir, dir));
        }

        SanitizeCopiedExtensionPreferences(Path.Combine(defaultDir, "Preferences"));
        SanitizeCopiedExtensionPreferences(Path.Combine(defaultDir, "Secure Preferences"));
    }

    private static void SanitizeCopiedExtensionPreferences(string fileName)
    {
        if (!File.Exists(fileName))
            return;

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(fileName)) as JsonObject;
            if (root is null)
                return;

            var extensions = root["extensions"] as JsonObject;
            if (extensions is null)
            {
                extensions = new JsonObject();
                root["extensions"] = extensions;
            }

            extensions.Remove("alerts");
            extensions.Remove("chrome_url_overrides");
            extensions.Remove("commands");
            extensions.Remove("last_chrome_version");
            extensions.Remove("pinned_extensions");
            extensions.Remove("settings");
            extensions.Remove("toolbar");

            var ui = extensions["ui"] as JsonObject;
            if (ui is null)
            {
                ui = new JsonObject();
                extensions["ui"] = ui;
            }

            ui["developer_mode"] = true;

            if (root["protection"] is JsonObject protection &&
                protection["macs"] is JsonObject macs)
            {
                macs.Remove("extensions");
            }

            File.WriteAllText(
                fileName,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
                Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static void DeleteDirectoryQuietly(string path)
    {
        if (!Directory.Exists(path))
            return;

        try
        {
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }

            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
