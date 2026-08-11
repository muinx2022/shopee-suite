namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Mở trình duyệt cho đường POC GĐ0 "mở sạch": Brave thật với args từ
/// BraveLaunchArgs.BuildCleanPocArgs — KHÔNG Playwright, KHÔNG ConnectOverCDP, KHÔNG remote-debugging-port.
/// Phóng qua <see cref="BrowserProcessStarter"/> (Suite rót Job Object → chết theo app). Trả về Process để
/// tầng UI theo dõi/kill. Ném InvalidOperationException (message tiếng Việt) nếu thiếu Brave
/// hoặc thiếu extension POC.
/// </summary>
public static class PocCleanLauncher
{
    /// <param name="extensionPath">Đường dẫn thư mục extension muốn nạp (BẮT BUỘC). Cầu nối truyền thẳng thư mục
    /// <c>shopee-orders</c> đã phân giải qua <see cref="BraveLaunchArgs.ResolveOrdersBridgeExtension"/>.</param>
    public static System.Diagnostics.Process Open(string userDataDir, string startUrl, string extensionPath)
    {
        var exe = BrowserLocator.RequireBraveExecutable();

        var extPath = extensionPath
            ?? throw new InvalidOperationException(
                "PocCleanLauncher.Open cần extensionPath (thư mục extension 'shopee-orders' đã phân giải).");

        System.IO.Directory.CreateDirectory(userDataDir);

        var args = BraveLaunchArgs.BuildCleanPocArgs(userDataDir, extPath, startUrl);
        return BrowserProcessStarter.StartOrFallback(exe, args);
    }
}
