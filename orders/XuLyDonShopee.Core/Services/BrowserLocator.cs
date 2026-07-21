using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Dò đường dẫn file thực thi của các trình duyệt gốc Chromium đã cài trên máy (đa nền tảng
/// Windows/Linux/macOS): <b>Chrome</b>, <b>Edge</b>, <b>Brave</b>. Dùng để ưu tiên mở trình duyệt
/// thật thay vì tải Chromium đóng gói (~150MB), và để phân giải lựa chọn của người dùng
/// (<see cref="BrowserChoice"/>) thành file thực thi cụ thể.
/// </summary>
public static class BrowserLocator
{
    /// <summary>
    /// Trả về đường dẫn đầu tiên trong <paramref name="candidates"/> mà
    /// <paramref name="exists"/> trả về <c>true</c>. Bỏ qua các phần tử null/rỗng/toàn khoảng trắng.
    /// Trả về <c>null</c> nếu không phần tử nào tồn tại.
    /// </summary>
    /// <remarks>
    /// Hàm lõi thuần (không trực tiếp đụng hệ thống file — nhận predicate <paramref name="exists"/>)
    /// nên test được độc lập với máy thật.
    /// </remarks>
    internal static string? FindFirstExisting(IEnumerable<string> candidates, Func<string, bool> exists)
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

    /// <summary>
    /// Tìm đường dẫn tới file thực thi Brave theo HĐH hiện tại. Trả về <c>null</c> nếu không
    /// tìm thấy (ví dụ chưa cài Brave, hoặc HĐH không nằm trong danh sách hỗ trợ).
    /// </summary>
    public static string? FindBraveExecutable()
    {
        return FindFirstExisting(BuildBraveCandidates(), File.Exists);
    }

    /// <summary>
    /// Tìm đường dẫn tới file thực thi Google Chrome (hoặc Chromium hệ thống trên Linux) theo HĐH
    /// hiện tại. Trả về <c>null</c> nếu không tìm thấy.
    /// </summary>
    public static string? FindChromeExecutable()
    {
        return FindFirstExisting(BuildChromeCandidates(), File.Exists);
    }

    /// <summary>
    /// Tìm đường dẫn tới file thực thi Microsoft Edge (cũng là Chromium — dùng chung cờ/CDP như
    /// Chrome/Brave) theo HĐH hiện tại. Trả về <c>null</c> nếu không tìm thấy. Trên Windows 11 Edge
    /// thường luôn có sẵn ở <c>Program Files (x86)</c>.
    /// </summary>
    public static string? FindEdgeExecutable()
    {
        return FindFirstExisting(BuildEdgeCandidates(), File.Exists);
    }

    /// <summary>
    /// Phân giải lựa chọn trình duyệt của người dùng thành file thực thi cụ thể trên MÁY THẬT.
    /// Xem <see cref="ResolveExecutableCore"/> cho luật; <c>null</c> nghĩa là "dùng Chromium đóng gói
    /// của Playwright" (caller tự xử lý).
    /// </summary>
    public static string? ResolveExecutable(BrowserChoice choice)
        => ResolveExecutableCore(choice, FindChromeExecutable, FindEdgeExecutable, FindBraveExecutable);

    /// <summary>
    /// Lõi thuần phân giải <see cref="BrowserChoice"/> (tiêm predicate dò từng trình duyệt nên test
    /// được độc lập máy thật):
    /// <list type="bullet">
    /// <item><see cref="BrowserChoice.Auto"/> → <paramref name="findChrome"/> ?? <paramref name="findEdge"/>
    /// ?? <paramref name="findBrave"/> (ưu tiên Chromium "sạch" Chrome→Edge trước Brave; hết → <c>null</c>).
    /// Lý do: Chrome/Edge ít bị Shopee bắt captcha hơn Brave (Brave bật sẵn chống-fingerprint); Windows
    /// luôn có Edge nên Auto dù thiếu Chrome vẫn né được Brave.</item>
    /// <item><see cref="BrowserChoice.Chrome"/> → <paramref name="findChrome"/>.</item>
    /// <item><see cref="BrowserChoice.Edge"/> → <paramref name="findEdge"/>.</item>
    /// <item><see cref="BrowserChoice.Brave"/> → <paramref name="findBrave"/>.</item>
    /// <item><see cref="BrowserChoice.BundledChromium"/> → <c>null</c> (luôn dùng đóng gói).</item>
    /// </list>
    /// <c>null</c> = không có file thực thi thật phù hợp → caller dùng Chromium đóng gói.
    /// </summary>
    internal static string? ResolveExecutableCore(
        BrowserChoice choice,
        Func<string?> findChrome,
        Func<string?> findEdge,
        Func<string?> findBrave)
        => choice switch
        {
            BrowserChoice.Chrome => findChrome(),
            BrowserChoice.Edge => findEdge(),
            BrowserChoice.Brave => findBrave(),
            BrowserChoice.BundledChromium => null,
            _ => findChrome() ?? findEdge() ?? findBrave() // Auto
        };

    /// <summary>
    /// Phân giải lựa chọn trình duyệt thành "loại" ngắn (slug an toàn cho tên thư mục) của trình duyệt
    /// THỰC sẽ được mở: <c>"chrome"</c>, <c>"edge"</c>, <c>"brave"</c>, hoặc <c>"chromium"</c>
    /// (khi không có file thực thi thật phù hợp → caller dùng Chromium đóng gói của Playwright).
    /// Dùng để tách hồ sơ persistent theo từng trình duyệt (mỗi trình duyệt một fingerprint riêng).
    /// Lấy exe từ CÙNG nguồn <see cref="ResolveExecutable"/> mà caller dùng để launch nên slug luôn
    /// KHỚP trình duyệt thật được mở.
    /// </summary>
    public static string ResolveBrowserKind(BrowserChoice choice)
        => ClassifyExe(
               ResolveExecutable(choice),
               FindChromeExecutable(),
               FindEdgeExecutable(),
               FindBraveExecutable());

    /// <summary>
    /// Lõi thuần phân loại một đường dẫn exe thành slug loại trình duyệt bằng cách so KHỚP với đường dẫn
    /// Chrome/Edge/Brave đã dò được (tiêm vào nên test được độc lập máy thật). So khớp không phân biệt
    /// hoa/thường (đường dẫn Windows). Trả <c>"chromium"</c> khi <paramref name="exePath"/> rỗng hoặc
    /// không khớp trình duyệt nào (nghĩa là caller sẽ dùng Chromium đóng gói).
    /// </summary>
    internal static string ClassifyExe(string? exePath, string? chromePath, string? edgePath, string? bravePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return "chromium";
        }

        if (!string.IsNullOrWhiteSpace(chromePath)
            && string.Equals(exePath, chromePath, StringComparison.OrdinalIgnoreCase))
        {
            return "chrome";
        }

        if (!string.IsNullOrWhiteSpace(edgePath)
            && string.Equals(exePath, edgePath, StringComparison.OrdinalIgnoreCase))
        {
            return "edge";
        }

        if (!string.IsNullOrWhiteSpace(bravePath)
            && string.Equals(exePath, bravePath, StringComparison.OrdinalIgnoreCase))
        {
            return "brave";
        }

        return "chromium";
    }

    /// <summary>Dựng danh sách đường dẫn ứng viên của Brave theo HĐH.</summary>
    private static IEnumerable<string> BuildBraveCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            const string relative = @"BraveSoftware\Brave-Browser\Application\brave.exe";

            var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
            var programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");

            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                yield return Path.Combine(programFiles, relative);
            }
            if (!string.IsNullOrWhiteSpace(programFilesX86))
            {
                yield return Path.Combine(programFilesX86, relative);
            }
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                yield return Path.Combine(localAppData, relative);
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            yield return "/usr/bin/brave-browser";
            yield return "/usr/bin/brave-browser-stable";
            yield return "/usr/bin/brave";
            yield return "/opt/brave.com/brave/brave-browser";
            yield return "/snap/bin/brave";
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser";
        }
    }

    /// <summary>Dựng danh sách đường dẫn ứng viên của Chrome (kèm Chromium hệ thống trên Linux) theo HĐH.</summary>
    private static IEnumerable<string> BuildChromeCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            const string relative = @"Google\Chrome\Application\chrome.exe";

            var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
            var programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");

            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                yield return Path.Combine(programFiles, relative);
            }
            if (!string.IsNullOrWhiteSpace(programFilesX86))
            {
                yield return Path.Combine(programFilesX86, relative);
            }
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                yield return Path.Combine(localAppData, relative);
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            yield return "/usr/bin/google-chrome";
            yield return "/usr/bin/google-chrome-stable";
            yield return "/opt/google/chrome/chrome";
            yield return "/usr/bin/chromium";
            yield return "/snap/bin/chromium";
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
        }
    }

    /// <summary>Dựng danh sách đường dẫn ứng viên của Microsoft Edge theo HĐH.</summary>
    private static IEnumerable<string> BuildEdgeCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            const string relative = @"Microsoft\Edge\Application\msedge.exe";

            // Edge thường ở Program Files (x86); dò x86 trước.
            var programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
            var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");

            if (!string.IsNullOrWhiteSpace(programFilesX86))
            {
                yield return Path.Combine(programFilesX86, relative);
            }
            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                yield return Path.Combine(programFiles, relative);
            }
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                yield return Path.Combine(localAppData, relative);
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            yield return "/usr/bin/microsoft-edge";
            yield return "/usr/bin/microsoft-edge-stable";
            yield return "/opt/microsoft/msedge/msedge";
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge";
        }
    }
}
