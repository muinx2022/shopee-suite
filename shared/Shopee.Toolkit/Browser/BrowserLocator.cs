using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Shopee.Toolkit.Browser;

/// <summary>
/// Dò đường dẫn file thực thi + thư mục "User Data" của các trình duyệt gốc Chromium đã cài trên máy
/// (<b>Chrome</b>, <b>Edge</b>, <b>Brave</b>) theo HĐH: Windows (đường dẫn cố định + registry App Paths cho
/// Brave), Linux (/usr/bin, snap, flatpak + dò trên PATH), macOS (best-effort). Trả <c>null</c> nếu không thấy.
/// <para>
/// Hợp nhất hai bản chép tay: <c>Shopee.Core.Platform.Windows/Linux.*BrowserLocator</c> (suite — có registry
/// fallback + flatpak + dò PATH, nhưng chỉ biết Brave/Edge) và <c>XuLyDonShopee.Core.Services.BrowserLocator</c>
/// (orders — có thêm Chrome, macOS, vài đường dẫn Linux riêng). Bản này là HỢP của cả hai: thứ tự ứng viên lấy
/// theo bản suite, các đường dẫn chỉ-orders-có được chèn thêm; nhờ vậy hai phía luôn mở CÙNG một file thực thi.
/// </para>
/// </summary>
public static class BrowserLocator
{
    /// <summary>
    /// Trả về đường dẫn đầu tiên trong <paramref name="candidates"/> mà <paramref name="exists"/> trả về
    /// <c>true</c>. Bỏ qua các phần tử null/rỗng/toàn khoảng trắng. Trả về <c>null</c> nếu không phần tử nào tồn tại.
    /// </summary>
    /// <remarks>
    /// Hàm lõi thuần (không trực tiếp đụng hệ thống file — nhận predicate <paramref name="exists"/>)
    /// nên test được độc lập với máy thật.
    /// </remarks>
    public static string? FindFirstExisting(IEnumerable<string> candidates, Func<string, bool> exists)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Đường dẫn file thực thi Brave, hoặc <c>null</c> nếu chưa cài / HĐH không hỗ trợ.</summary>
    public static string? FindBraveExecutable() => FindFirstExisting(BuildBraveCandidates(), File.Exists);

    /// <summary>Đường dẫn file thực thi Google Chrome (kèm Chromium hệ thống trên Linux), hoặc <c>null</c>.</summary>
    public static string? FindChromeExecutable() => FindFirstExisting(BuildChromeCandidates(), File.Exists);

    /// <summary>Đường dẫn file thực thi Microsoft Edge (cũng là Chromium — dùng chung cờ/CDP như Chrome/Brave),
    /// hoặc <c>null</c>. Trên Windows 11 Edge thường luôn có sẵn ở <c>Program Files (x86)</c>.</summary>
    public static string? FindEdgeExecutable() => FindFirstExisting(BuildEdgeCandidates(), File.Exists);

    /// <summary>Thư mục "User Data" mẫu của Brave (phải có thư mục con <c>Default</c>) — dùng làm nguồn copy
    /// extension-state khi tạo profile mới. <c>null</c> nếu chưa từng mở Brave.</summary>
    public static string? FindBraveUserData()
        => FindFirstExisting(BuildBraveUserDataCandidates(), HasDefaultProfile);

    /// <summary>Thư mục "User Data" mẫu của Edge (phải có thư mục con <c>Default</c>). <c>null</c> nếu chưa có.</summary>
    public static string? FindEdgeUserData()
        => FindFirstExisting(BuildEdgeUserDataCandidates(), HasDefaultProfile);

    private static bool HasDefaultProfile(string userDataDir)
        => Directory.Exists(Path.Combine(userDataDir, "Default"));

    // ===================== Ứng viên file thực thi theo HĐH =====================

    /// <summary>Ứng viên Brave. Windows: LocalAppData → Program Files → Program Files (x86) → registry App Paths
    /// (HKCU rồi HKLM, PHƯƠNG ÁN CUỐI). Linux: /usr/bin → /opt → snap → flatpak → dò PATH.</summary>
    private static IEnumerable<string> BuildBraveCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var p in WindowsAppCandidates(
                @"BraveSoftware\Brave-Browser\Application\brave.exe", localAppDataFirst: true))
            {
                yield return p;
            }
            // Fallback CUỐI: App Paths\brave.exe — chỉ dùng khi các đường dẫn cố định trên đều thiếu.
            foreach (var p in BraveRegistryCandidates())
            {
                yield return p;
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            yield return "/usr/bin/brave-browser";
            yield return "/usr/bin/brave-browser-stable";
            yield return "/usr/bin/brave";
            yield return "/opt/brave.com/brave/brave-browser";
            yield return "/snap/bin/brave";
            yield return "/var/lib/flatpak/exports/bin/com.brave.Browser";
            yield return Path.Combine(HomeDir(), ".local/share/flatpak/exports/bin/com.brave.Browser");
            foreach (var p in PathCandidates("brave-browser", "brave-browser-stable", "brave"))
            {
                yield return p;
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser";
        }
    }

    /// <summary>Ứng viên Chrome (kèm Chromium hệ thống trên Linux). Windows: Program Files → x86 → LocalAppData.</summary>
    private static IEnumerable<string> BuildChromeCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var p in WindowsAppCandidates(
                @"Google\Chrome\Application\chrome.exe", localAppDataFirst: false))
            {
                yield return p;
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            yield return "/usr/bin/google-chrome";
            yield return "/usr/bin/google-chrome-stable";
            yield return "/opt/google/chrome/chrome";
            yield return "/usr/bin/chromium";
            yield return "/snap/bin/chromium";
            foreach (var p in PathCandidates("google-chrome", "google-chrome-stable", "chromium"))
            {
                yield return p;
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
        }
    }

    /// <summary>Ứng viên Edge. Windows: Program Files (x86) TRƯỚC (Edge thường nằm ở đó) → Program Files →
    /// LocalAppData. Linux hiếm gặp → best-effort.</summary>
    private static IEnumerable<string> BuildEdgeCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var p in WindowsAppCandidates(
                @"Microsoft\Edge\Application\msedge.exe", localAppDataFirst: false, programFilesX86First: true))
            {
                yield return p;
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            yield return "/usr/bin/microsoft-edge";
            yield return "/usr/bin/microsoft-edge-stable";
            yield return "/opt/microsoft/msedge/microsoft-edge";
            yield return "/opt/microsoft/msedge/msedge";
            foreach (var p in PathCandidates("microsoft-edge", "microsoft-edge-stable"))
            {
                yield return p;
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge";
        }
    }

    // ===================== Ứng viên thư mục User Data =====================

    private static IEnumerable<string> BuildBraveUserDataCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(LocalAppData(), "BraveSoftware", "Brave-Browser", "User Data");
        }
        else if (OperatingSystem.IsLinux())
        {
            var home = HomeDir();
            yield return Path.Combine(home, ".config/BraveSoftware/Brave-Browser");
            yield return Path.Combine(home, ".var/app/com.brave.Browser/config/BraveSoftware/Brave-Browser");
        }
    }

    private static IEnumerable<string> BuildEdgeUserDataCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(LocalAppData(), "Microsoft", "Edge", "User Data");
        }
        else if (OperatingSystem.IsLinux())
        {
            yield return Path.Combine(HomeDir(), ".config/microsoft-edge");
        }
    }

    // ===================== Helper =====================

    /// <summary>Ghép <paramref name="relative"/> vào các gốc cài đặt Windows theo thứ tự yêu cầu. Gốc rỗng
    /// (biến môi trường không có) bị bỏ qua.</summary>
    private static IEnumerable<string> WindowsAppCandidates(
        string relative, bool localAppDataFirst, bool programFilesX86First = false)
    {
        var local = LocalAppData();
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        var roots = localAppDataFirst
            ? new[] { local, pf, pfx86 }
            : programFilesX86First
                ? new[] { pfx86, pf, local }
                : new[] { pf, pfx86, local };

        foreach (var root in roots)
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                yield return Path.Combine(root, relative);
            }
        }
    }

    /// <summary>Đường dẫn brave.exe khai trong registry App Paths (HKCU rồi HKLM). Rỗng nếu không có khoá.</summary>
    [SupportedOSPlatform("windows")]
    private static List<string> BraveRegistryCandidates()
    {
        var found = new List<string>();
        foreach (var (root, sub) in new[]
        {
            (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\App Paths\brave.exe"),
            (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\App Paths\brave.exe"),
        })
        {
            try
            {
                using var key = root.OpenSubKey(sub);
                var value = key?.GetValue(string.Empty)?.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                    found.Add(value);
            }
            catch { }
        }
        return found;
    }

    /// <summary>Ghép <paramref name="binNames"/> vào từng thư mục trong PATH (fallback khi đường dẫn cố định
    /// đều trượt — chỉ THÊM cơ hội tìm thấy, không đổi kết quả khi đã có ứng viên khớp trước đó).</summary>
    private static IEnumerable<string> PathCandidates(params string[] binNames)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var dirs = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        // Tên bin ở vòng NGOÀI (khớp bản suite cũ): quét hết PATH cho tên ưu tiên nhất rồi mới sang tên sau.
        foreach (var bin in binNames)
        {
            foreach (var dir in dirs)
            {
                string candidate;
                try { candidate = Path.Combine(dir.Trim(), bin); }
                catch { continue; } // ký tự không hợp lệ trong PATH — bỏ qua mục này
                yield return candidate;
            }
        }
    }

    private static string LocalAppData()
        => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string HomeDir()
        => Environment.GetEnvironmentVariable("HOME")
           ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
