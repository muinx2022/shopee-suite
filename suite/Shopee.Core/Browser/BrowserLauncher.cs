using Shopee.Core.Infrastructure;

namespace Shopee.Core.Browser;

/// <summary>Trình duyệt Chromium được hỗ trợ. CDP giống hệt nhau giữa chúng — chỉ khác đường
/// dẫn exe + tên tiến trình để dọn dẹp.</summary>
public enum BrowserKind { Edge, Brave }

/// <summary>
/// Mở một cửa sổ trình duyệt Chromium riêng (Edge hoặc Brave), bật CDP (remote debugging) và
/// proxy. Mỗi tài khoản dùng một profile riêng (thư mục user-data-dir) để cookie/đăng nhập
/// không lẫn giữa các phiên. Lớp này gộp logic Edge (check-account/stat) và Brave (v31) cũ,
/// tham số hoá bằng <see cref="BrowserKind"/>.
/// </summary>
public sealed class BrowserLauncher
{
    private readonly BrowserKind _kind;
    private Process? _process;
    private int _cdpPort;
    private string? _profileDir;

    public BrowserLauncher(BrowserKind kind = BrowserKind.Edge) => _kind = kind;

    public int CdpPort => _cdpPort;
    public BrowserKind Kind => _kind;

    public string? DetectExePath() => Detect(_kind);

    /// <summary>Thư mục "User Data" mẫu của trình duyệt (phải có Default) — dùng làm nguồn copy
    /// extension-state khi tạo profile mới. null nếu chưa từng mở trình duyệt.</summary>
    public static string? DetectUserData(BrowserKind kind)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var path = kind == BrowserKind.Brave
            ? Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data")
            : Path.Combine(local, "Microsoft", "Edge", "User Data");
        return Directory.Exists(Path.Combine(path, "Default")) ? path : null;
    }

    public static string? Detect(BrowserKind kind)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = kind == BrowserKind.Brave
            ?
            [
                Path.Combine(local, @"BraveSoftware\Brave-Browser\Application\brave.exe"),
                @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe",
                @"C:\Program Files (x86)\BraveSoftware\Brave-Browser\Application\brave.exe",
            ]
            : new[]
            {
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Mở trình duyệt tới <paramref name="startUrl"/>. Ném exception nếu không tìm thấy exe.</summary>
    public void Launch(string profileDir, string? proxyServer, string startUrl, IReadOnlyList<string>? extraArgs = null)
    {
        var exePath = Detect(_kind)
            ?? throw new FileNotFoundException(
                _kind == BrowserKind.Brave
                    ? "Không tìm thấy brave.exe. Hãy cài Brave Browser."
                    : "Không tìm thấy msedge.exe. Hãy cài Microsoft Edge.");

        // ĐẶT _profileDir TRƯỚC Kill() để Kill() dọn luôn Brave CŨ đang mở ĐÚNG profile này. Nếu không,
        // instance launcher mới có _profileDir=null → Kill() bỏ qua → Brave cũ (login để mở "giữ nguyên")
        // còn sống → mở lại cùng --user-data-dir bị Brave singleton FORWARD vào cửa sổ cũ → +1 tab mỗi lần.
        _profileDir = Path.GetFullPath(profileDir);
        Kill();
        Directory.CreateDirectory(profileDir);
        // Dọn trạng thái phiên cũ → mở lên KHÔNG khôi phục đống tab cũ (kể cả khi lần trước đóng bẩn).
        // Cùng với --no-restore-last-session, đảm bảo mỗi lần mở là cửa sổ sạch, 1 tab.
        ClearSessionRestoreState(profileDir);

        _cdpPort = PortAllocator.Reserve();
        var args = BuildArgs(_cdpPort, profileDir, proxyServer, startUrl, extraArgs);
        // Phóng QUA Job Object (KILL_ON_JOB_CLOSE) như bầy Brave scrape → cửa sổ automation (kiểm tra tk
        // lỗi / auto-login / BigSeller login) TỰ CHẾT khi app kết thúc — đóng thường, CRASH, hay force-kill
        // — kể cả khi WPF KHÔNG kịp gọi KillCheckBrowser (Unloaded không bắn lúc shutdown). KHÔNG còn rò
        // cửa sổ Brave còn phiên đăng nhập. Interop job lỗi → BraveJobObject tự fallback Process.Start thường.
        _process = BraveJobObject.Start(exePath, args);
    }

    private static string BuildArgs(
        int cdpPort, string userDataDir, string? proxy, string startUrl, IReadOnlyList<string>? extraArgs)
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
            "--proxy-bypass-list=\"localhost;127.0.0.1\"",
            $"--remote-debugging-port={cdpPort}",
        };

        // Chặn cache phình cho profile bền (check-account, bigseller-login). Xem BraveCachePolicy.
        parts.AddRange(BraveCachePolicy.DiskLimitArgs);

        if (!string.IsNullOrWhiteSpace(proxy))
            parts.Add($"--proxy-server={proxy}");

        if (extraArgs is not null)
            parts.AddRange(extraArgs);

        parts.Add($"\"{startUrl}\"");
        return string.Join(" ", parts);
    }

    public void Kill()
    {
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch { }

        // Chromium tách tiến trình: exe vừa Start thường thoát ngay sau khi bàn giao cho tiến
        // trình cửa sổ thật (PID khác) → _process.Kill() không giết được cửa sổ. Phải kill theo
        // --user-data-dir qua BraveProcessReaper (khớp ĐÚNG giá trị, không Contains — tránh giết nhầm
        // "acc_1" khi target "acc_10", vì profile CheckAccount lấy theo username có thể là tiền tố nhau).
        if (!string.IsNullOrWhiteSpace(_profileDir))
            BraveProcessReaper.KillByUserDataDir(_profileDir);

        try { _process?.Dispose(); } catch { }
        _process = null;
        PortAllocator.Release(_cdpPort);
        _cdpPort = 0;
    }

    /// <summary>Xóa file/thư mục phiên (Sessions, Current/Last Session/Tabs) + đặt exit_type=Normal trong
    /// Preferences → Brave KHÔNG khôi phục tab cũ lần mở sau (tránh tab chồng chất khi mở lại profile login).</summary>
    private static void ClearSessionRestoreState(string profileDir)
    {
        try
        {
            var def = Path.Combine(Path.GetFullPath(profileDir), "Default");
            if (!Directory.Exists(def)) return;

            // 1) Xóa thư mục Sessions + các file tab phiên gần nhất.
            try { if (Directory.Exists(Path.Combine(def, "Sessions"))) Directory.Delete(Path.Combine(def, "Sessions"), recursive: true); } catch { }
            foreach (var name in new[] { "Current Session", "Current Tabs", "Last Session", "Last Tabs" })
            {
                try { var p = Path.Combine(def, name); if (File.Exists(p)) File.Delete(p); } catch { }
            }

            // 2) Đặt exit_type = "Normal" trong Preferences → Brave coi như lần trước thoát sạch, không hỏi/khôi phục.
            var prefPath = Path.Combine(def, "Preferences");
            if (File.Exists(prefPath))
            {
                try
                {
                    var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(prefPath)) as System.Text.Json.Nodes.JsonObject;
                    var profile = root? ["profile"] as System.Text.Json.Nodes.JsonObject;
                    if (root is not null && profile is null) { profile = new System.Text.Json.Nodes.JsonObject(); root["profile"] = profile; }
                    if (profile is not null)
                    {
                        profile["exit_type"] = "Normal";
                        profile["exited_cleanly"] = true;
                        File.WriteAllText(prefPath, root!.ToJsonString());
                    }
                }
                catch { }
            }
        }
        catch { }
    }

}
