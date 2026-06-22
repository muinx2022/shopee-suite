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
        ClearSwScriptCache(profileRoot);
        ClearSessionRestoreState(profileRoot);
        MarkProfileCleanShutdown(profileRoot);
    }

    public static string BuildBraveArguments(
        int cdpPort,
        string userDataDir,
        string? proxyServer,
        Action<string>? log = null,
        string? sourceUserData = null,
        bool loadRunnerExtension = true)
    {
        var parts = new List<string>
        {
            $"--user-data-dir=\"{userDataDir}\"",
            "--profile-directory=Default",
            "--new-window",
            "--no-first-run",
            "--no-default-browser-check",
            "--hide-crash-restore-bubble",
            $"--remote-debugging-port={cdpPort}",

            // GIỮ CỬA SỔ NỀN LUÔN HOẠT ĐỘNG. Khi chạy nhiều instance, mở/đưa cửa sổ instance khác lên
            // trước làm các cửa sổ còn lại bị che (occluded)/chạy nền → Brave bóp timer + renderer +
            // service worker → scrape "đứng hình" (đúng triệu chứng: kẹt lúc mở sang instance khác).
            // Các cờ dưới tắt toàn bộ cơ chế tiết kiệm tài nguyên đó để mọi instance scrape song song ổn định.
            "--disable-background-timer-throttling",
            "--disable-backgrounding-occluded-windows",
            "--disable-renderer-backgrounding",
            // DisableLoadExtensionCommandLineSwitch: Brave/Chrome 137+ MẶC ĐỊNH chặn --load-extension
            // → extension "Shopee Data Runner" KHÔNG load. Tắt feature này để --load-extension hoạt động lại.
            "--disable-features=CalculateNativeWinOcclusion,IntensiveWakeUpThrottling,DisableLoadExtensionCommandLineSwitch",
        };
        if (!string.IsNullOrWhiteSpace(proxyServer))
        {
            parts.Add($"--proxy-server={proxyServer}");
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

        return string.Join(" ", parts);
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

    private static void ClearSwScriptCache(string profileRoot)
    {
        try
        {
            var defaultDir = Path.Combine(profileRoot, "Default");

            // Xóa ScriptCache (blob bytecode SW) + Code Cache → buộc Brave nạp lại background.js mới
            // từ extension. GIỮ "Service Worker/Database" (bản đăng ký) để SW không phải đăng ký lại
            // cold mỗi lần launch — đăng ký lại cold mở rộng cửa sổ race "top-level chưa chạy xong"
            // làm các hàm __launcher* tạm thời chưa có (lỗi đó nay được retry, xem IsTransientSwError).
            foreach (var subDir in new[] { Path.Combine("Service Worker", "ScriptCache"), "Code Cache" })
            {
                var dir = Path.Combine(defaultDir, subDir);
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }
        catch { }
    }

    private static void ClearSessionRestoreState(string profileRoot)
    {
        try
        {
            var defaultDir = Path.Combine(profileRoot, "Default");
            foreach (var subDir in new[] { "Sessions" })
            {
                var dir = Path.Combine(defaultDir, subDir);
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
        catch { }
    }

    private static void MarkProfileCleanShutdown(string profileRoot)
    {
        var preferencesPath = Path.Combine(profileRoot, "Default", "Preferences");
        if (!File.Exists(preferencesPath))
            return;

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(preferencesPath)) as JsonObject;
            if (root is null) return;

            var profile = root["profile"] as JsonObject;
            if (profile is null)
            {
                profile = new JsonObject();
                root["profile"] = profile;
            }

            profile["exit_type"] = "Normal";
            profile["exited_cleanly"] = true;

            File.WriteAllText(
                preferencesPath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
                Encoding.UTF8);
        }
        catch { }
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

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(src))
            CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }
}
