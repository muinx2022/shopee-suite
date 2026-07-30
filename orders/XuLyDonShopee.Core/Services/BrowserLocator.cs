using XuLyDonShopee.Core.Models;
using ToolkitLocator = Shopee.Toolkit.Browser.BrowserLocator;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Phân giải lựa chọn trình duyệt của người dùng (<see cref="BrowserChoice"/>) thành file thực thi cụ thể.
/// <para>
/// WRAPPER MỎNG: việc DÒ đường dẫn (Windows/Linux/macOS, registry App Paths, snap/flatpak, PATH) nằm ở
/// <see cref="ToolkitLocator"/> (shared/Shopee.Toolkit) — dùng chung với suite để hai phía luôn mở CÙNG
/// một file thực thi. Ở đây chỉ còn phần RIÊNG của module Đơn hàng: luật ưu tiên theo
/// <see cref="BrowserChoice"/> (khái niệm chỉ có ở app này, kèm nhánh "Chromium đóng gói" của Playwright).
/// </para>
/// </summary>
public static class BrowserLocator
{
    /// <summary>
    /// Tìm đường dẫn tới file thực thi Brave theo HĐH hiện tại. Trả về <c>null</c> nếu không
    /// tìm thấy (ví dụ chưa cài Brave, hoặc HĐH không nằm trong danh sách hỗ trợ).
    /// </summary>
    public static string? FindBraveExecutable() => ToolkitLocator.FindBraveExecutable();

    /// <summary>
    /// Tìm đường dẫn tới file thực thi Google Chrome (hoặc Chromium hệ thống trên Linux) theo HĐH
    /// hiện tại. Trả về <c>null</c> nếu không tìm thấy.
    /// </summary>
    public static string? FindChromeExecutable() => ToolkitLocator.FindChromeExecutable();

    /// <summary>
    /// Tìm đường dẫn tới file thực thi Microsoft Edge (cũng là Chromium — dùng chung cờ/CDP như
    /// Chrome/Brave) theo HĐH hiện tại. Trả về <c>null</c> nếu không tìm thấy. Trên Windows 11 Edge
    /// thường luôn có sẵn ở <c>Program Files (x86)</c>.
    /// </summary>
    public static string? FindEdgeExecutable() => ToolkitLocator.FindEdgeExecutable();

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
}
