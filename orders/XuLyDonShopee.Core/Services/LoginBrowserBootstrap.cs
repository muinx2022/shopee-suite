using System.Diagnostics;
using Microsoft.Playwright;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Khởi chạy trình duyệt cho phiên đăng nhập: phân giải file thực thi (Brave/Chrome/Edge, không có thì Chromium
/// đóng gói của Playwright), phóng tiến trình với hồ sơ persistent + cổng gỡ lỗi, chờ CDP sẵn sàng rồi
/// <see cref="IBrowserType.ConnectOverCDPAsync"/>. Không chứa nghiệp vụ Shopee — <see cref="ShopeeLoginService"/>
/// gọi rồi tự điều hướng + dựng <c>LoginSession</c>.
/// </summary>
internal static class LoginBrowserBootstrap
{
    /// <summary>
    /// Đảm bảo có sẵn trình duyệt để mở cho <paramref name="browserChoice"/>. Nếu phân giải được một
    /// trình duyệt thật đã cài trên máy (Chrome/Edge/Brave tùy lựa chọn) thì trả về ngay (0) mà
    /// <b>không tải</b> Chromium đóng gói. Ngược lại (không có trình duyệt thật phù hợp, hoặc chọn
    /// Chromium đóng gói) thì tải Chromium của Playwright (~150MB lần đầu; idempotent — đã cài thì
    /// trả về nhanh). Trả về exit code (0 = thành công); bọc try/catch, trả code khác 0 khi lỗi để
    /// tầng gọi thông báo.
    /// </summary>
    internal static int EnsureBrowserInstalled(BrowserChoice browserChoice)
    {
        // Phân giải được trình duyệt thật → không cần tải Chromium đóng gói (đỡ ~150MB).
        if (BrowserLocator.ResolveExecutable(browserChoice) != null)
        {
            return 0;
        }

        try
        {
            return Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Mô tả trình duyệt <b>THỰC SỰ</b> sẽ được dùng cho <paramref name="browserChoice"/> (để hiển
    /// thị ở Cài đặt / log): phân giải file thực thi rồi phân loại bằng cách <b>so path bằng nhau</b>
    /// với <see cref="BrowserLocator.FindChromeExecutable"/> / <see cref="BrowserLocator.FindEdgeExecutable"/>
    /// / <see cref="BrowserLocator.FindBraveExecutable"/> (KHÔNG đoán theo tên file để tránh sai với
    /// đường dẫn lạ): khớp Chrome → <c>"Chrome (&lt;path&gt;)"</c>; khớp Edge → <c>"Edge (&lt;path&gt;)"</c>;
    /// khớp Brave → <c>"Brave (&lt;path&gt;)"</c>; <c>null</c> (không có trình duyệt thật / chọn Chromium
    /// đóng gói) → <c>"Chromium đóng gói của Playwright"</c>.
    /// <para>
    /// Hành vi mặc định (<see cref="BrowserChoice.Auto"/>): ưu tiên Chrome → Edge → Brave; đây là đổi so
    /// với bản cũ (trước ưu tiên Brave) — CÓ CHỦ ĐÍCH vì Chrome/Edge ít bị Shopee bắt captcha hơn Brave.
    /// </para>
    /// </summary>
    internal static string DescribeBrowser(BrowserChoice browserChoice)
    {
        var exe = BrowserLocator.ResolveExecutable(browserChoice);
        if (exe == null)
        {
            return "Chromium đóng gói của Playwright";
        }

        if (PathEquals(exe, BrowserLocator.FindChromeExecutable()))
        {
            return $"Chrome ({exe})";
        }
        if (PathEquals(exe, BrowserLocator.FindEdgeExecutable()))
        {
            return $"Edge ({exe})";
        }
        if (PathEquals(exe, BrowserLocator.FindBraveExecutable()))
        {
            return $"Brave ({exe})";
        }

        // Không khớp trình duyệt thật nào (không kỳ vọng xảy ra) → mô tả trung tính theo path.
        return $"Trình duyệt ({exe})";
    }

    /// <summary>So sánh hai đường dẫn file (không phân biệt hoa/thường trên Windows). <c>b</c> null → false.</summary>
    private static bool PathEquals(string a, string? b)
    {
        if (string.IsNullOrEmpty(b))
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(a, b, comparison);
    }

    /// <summary>
    /// Phóng trình duyệt với hồ sơ persistent <paramref name="userDataDir"/> rồi nối vào qua CDP. Trả về
    /// tiến trình đang sở hữu + <see cref="IBrowser"/> đã nối + context mặc định (= hồ sơ persistent).
    /// Ném khi không launch/nối được — <see cref="ShopeeLoginService.OpenAsync"/> bọc lỗi thành thông báo
    /// tiếng Việt và dọn dẹp.
    /// <para><b>Chủ đích KHÔNG</b> tiêm init script vá fingerprint: Brave thật đã sạch (webdriver=false,
    /// plugins/window.chrome/WebGL thật), vá lại chỉ tự tạo dấu hiệu lộ bot. Locale VN đặt qua cờ
    /// <c>--lang=vi-VN</c> trong <see cref="BraveLaunchArgs"/>.</para>
    /// </summary>
    internal static async Task<(Process Process, IBrowser Browser, IBrowserContext Context)> LaunchAndConnectAsync(
        IPlaywright playwright, string userDataDir, BrowserChoice browserChoice, CancellationToken ct)
    {
        // Phân giải trình duyệt thật theo lựa chọn của người dùng; không có → Chromium đóng gói (cùng cơ chế CDP).
        var exePath = BrowserLocator.ResolveExecutable(browserChoice);
        if (exePath == null)
        {
            EnsureChromiumInstalledForFallback();
            exePath = playwright.Chromium.ExecutablePath;
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                throw new InvalidOperationException(
                    "Không tìm thấy trình duyệt đã chọn và cũng chưa tải được Chromium đóng gói của Playwright.");
            }
        }

        // Đọc cổng CDP thật từ DevToolsActivePort → xóa file cũ để tránh đọc nhầm cổng phiên trước.
        var portFile = Path.Combine(userDataDir, "DevToolsActivePort");
        try { if (File.Exists(portFile)) File.Delete(portFile); } catch { /* bỏ qua */ }

        // Launch Brave/Chromium với cổng 0 (Chromium tự chọn cổng trống, ghi vào DevToolsActivePort).
        // Trình duyệt điều khiển (Playwright) chỉ dùng để đăng nhập subaccount → KHÔNG nạp extension.
        // Phóng qua BrowserProcessStarter (Suite rót Job Object → chết theo app khi force-kill).
        var launchArgs = BraveLaunchArgs.BuildBraveArgs(userDataDir, 0, extensionPath: null);
        var process = BrowserProcessStarter.StartOrFallback(exePath, launchArgs);
        process.EnableRaisingEvents = true;

        IBrowser? browser = null;
        try
        {
            // Chờ Brave mở cổng CDP (đọc cổng thật) rồi chờ endpoint /json/version sẵn sàng.
            var port = await WaitForDevToolsPortAsync(portFile, process, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            await WaitForCdpEndpointAsync(port, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);

            // Nối vào Brave đang chạy qua CDP.
            browser = await playwright.Chromium
                .ConnectOverCDPAsync($"http://127.0.0.1:{port}").ConfigureAwait(false);

            // Brave chạy --user-data-dir → có sẵn context mặc định = hồ sơ persistent.
            var context = browser.Contexts.Count > 0
                ? browser.Contexts[0]
                : await browser.NewContextAsync().ConfigureAwait(false);

            return (process, browser, context);
        }
        catch
        {
            // Đã phóng được tiến trình rồi mới hỏng (chờ CDP quá giờ / nối CDP lỗi / hủy) → phải tự dọn NGAY:
            // ngắt CDP, KILL cả cây Brave, dispose. Bên gọi chưa cầm được tiến trình nào nên KHÔNG dọn hộ được —
            // bỏ bước này là để lại Brave mồ côi giữ khóa --user-data-dir, lần mở sau lỗi khóa hồ sơ.
            if (browser is not null)
            {
                try { await browser.CloseAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }
            }
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* bỏ qua */ }
            }
            try { process.Dispose(); } catch { /* bỏ qua */ }
            throw;
        }
    }

    /// <summary>
    /// Chờ Brave khởi động xong và ghi cổng CDP vào file <c>DevToolsActivePort</c> (dòng đầu = cổng).
    /// Poll có timeout; nếu tiến trình thoát sớm (thường do hồ sơ đang bị một cửa sổ Brave khác khóa)
    /// thì ném lỗi tiếng Việt.
    /// </summary>
    private static async Task<int> WaitForDevToolsPortAsync(
        string portFile, Process process, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    "Trình duyệt thoát ngay khi khởi động (thường do hồ sơ đang bị một cửa sổ Brave khác khóa). " +
                    "Hãy đóng hết cửa sổ Brave rồi thử lại.");
            }

            try
            {
                if (File.Exists(portFile))
                {
                    var lines = await File.ReadAllLinesAsync(portFile, ct).ConfigureAwait(false);
                    if (lines.Length > 0 && int.TryParse(lines[0].Trim(), out var port) && port > 0)
                    {
                        return port;
                    }
                }
            }
            catch (IOException)
            {
                // File đang được Brave ghi dở — thử lại vòng sau.
            }

            await Task.Delay(150, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            "Quá thời gian chờ trình duyệt mở cổng gỡ lỗi (DevToolsActivePort).");
    }

    /// <summary>
    /// Chờ endpoint CDP HTTP <c>/json/version</c> trả 200 (báo trình duyệt đã sẵn sàng nhận kết nối CDP).
    /// Poll có timeout; hết giờ thì ném lỗi tiếng Việt.
    /// </summary>
    private static async Task WaitForCdpEndpointAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var url = $"http://127.0.0.1:{port}/json/version";
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var res = await http.GetAsync(url, ct).ConfigureAwait(false);
                if (res.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch
            {
                // Chưa sẵn sàng — thử lại vòng sau.
            }

            await Task.Delay(150, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException("Quá thời gian chờ endpoint CDP sẵn sàng.");
    }

    /// <summary>
    /// Tải Chromium đóng gói của Playwright cho nhánh fallback (khi máy không có Brave). Nuốt lỗi —
    /// nếu thực sự thiếu, bước lấy <c>ExecutablePath</c>/launch tiếp theo sẽ ném và được xử lý ở tầng trên.
    /// </summary>
    private static void EnsureChromiumInstalledForFallback()
    {
        try { Microsoft.Playwright.Program.Main(new[] { "install", "chromium" }); }
        catch { /* bỏ qua — bước launch tiếp theo sẽ ném nếu thật sự thiếu */ }
    }
}
