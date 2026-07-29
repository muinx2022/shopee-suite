using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Mở trình duyệt cho đường POC GĐ0 "mở sạch": Brave/Chrome/Edge thật với args từ
/// BraveLaunchArgs.BuildCleanPocArgs — KHÔNG Playwright, KHÔNG ConnectOverCDP, KHÔNG remote-debugging-port.
/// Phóng qua <see cref="BrowserProcessStarter"/> (Suite rót Job Object → chết theo app). Trả về Process để
/// tầng UI theo dõi/kill. Ném InvalidOperationException (message tiếng Việt) nếu thiếu trình duyệt thật
/// hoặc thiếu extension POC.
/// </summary>
public static class PocCleanLauncher
{
    /// <param name="extensionPath">Đường dẫn thư mục extension muốn nạp (BẮT BUỘC). Cầu nối truyền thẳng thư mục
    /// <c>shopee-orders</c> đã phân giải qua <see cref="BraveLaunchArgs.ResolveOrdersBridgeExtension"/>.</param>
    public static System.Diagnostics.Process Open(
        string userDataDir, BrowserChoice browserChoice, string startUrl, string extensionPath)
    {
        var exe = BrowserLocator.ResolveExecutable(browserChoice)
            ?? throw new InvalidOperationException(
                "Cầu nối cần Brave/Chrome/Edge thật đã cài trên máy (không dùng Chromium đóng gói). " +
                "Hãy cài một trình duyệt và chọn ở Cài đặt → Trình duyệt.");

        var extPath = extensionPath
            ?? throw new InvalidOperationException(
                "PocCleanLauncher.Open cần extensionPath (thư mục extension 'shopee-orders' đã phân giải).");

        System.IO.Directory.CreateDirectory(userDataDir);

        var args = BraveLaunchArgs.BuildCleanPocArgs(userDataDir, extPath, startUrl);
        return BrowserProcessStarter.StartOrFallback(exe, args);
    }
}
