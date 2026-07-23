using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Mở trình duyệt cho đường POC GĐ0 "mở sạch": Process.Start Brave/Chrome/Edge thật với args từ
/// BraveLaunchArgs.BuildCleanPocArgs — KHÔNG Playwright, KHÔNG ConnectOverCDP, KHÔNG remote-debugging-port.
/// Trả về Process để tầng UI theo dõi/kill. Ném InvalidOperationException (message tiếng Việt) nếu thiếu
/// trình duyệt thật hoặc thiếu extension POC.
/// </summary>
public static class PocCleanLauncher
{
    public static System.Diagnostics.Process Open(string userDataDir, BrowserChoice browserChoice, string startUrl)
    {
        var exe = BrowserLocator.ResolveExecutable(browserChoice)
            ?? throw new InvalidOperationException(
                "POC 'Mở sạch' cần Brave/Chrome/Edge thật đã cài trên máy (không dùng Chromium đóng gói). " +
                "Hãy cài một trình duyệt và chọn ở Cài đặt → Trình duyệt.");

        var extPath = BraveLaunchArgs.ResolveOrdersExtension()
            ?? throw new InvalidOperationException(
                "Không tìm thấy thư mục extension 'shopee-orders-test' (cạnh app hoặc trong repo). " +
                "POC cần extension này để tự điều hướng + bắn trusted click.");

        System.IO.Directory.CreateDirectory(userDataDir);

        var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var arg in BraveLaunchArgs.BuildCleanPocArgs(userDataDir, extPath, startUrl))
            psi.ArgumentList.Add(arg);

        return System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Không khởi chạy được tiến trình trình duyệt POC.");
    }
}
