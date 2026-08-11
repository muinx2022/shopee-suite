using System.Diagnostics;
using Microsoft.Playwright;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Khởi chạy trình duyệt cho phiên đăng nhập: lấy đường dẫn Brave, phóng tiến trình với hồ sơ persistent +
/// cổng gỡ lỗi, chờ CDP sẵn sàng rồi <see cref="IBrowserType.ConnectOverCDPAsync"/>. Không chứa nghiệp vụ
/// Shopee — <see cref="ShopeeLoginService"/> gọi rồi tự điều hướng + dựng <c>LoginSession</c>.
/// <para><b>Chỉ Brave</b> (chốt 11/08/2026). Bản cũ có nhánh lùi: không tìm thấy trình duyệt thật thì TẢI
/// Chromium đóng gói của Playwright (~150 MB) rồi chạy bằng nó. Đã bỏ hẳn — nhánh đó biến "máy thiếu Brave"
/// thành một lần tải 150 MB im lặng rồi chạy bằng trình duyệt mà cầu nối không nạp nổi extension.</para>
/// <para>Bỏ tải Chromium KHÔNG ảnh hưởng Playwright: driver đi kèm gói NuGet (thư mục <c>.playwright</c>), còn
/// <see cref="IBrowserType.ConnectOverCDPAsync"/> nối vào Brave đang chạy chứ không cần binary Chromium.</para>
/// </summary>
internal static class LoginBrowserBootstrap
{
    /// <summary>Mô tả Brave sẽ được dùng (để hiển thị ở log): <c>"Brave (&lt;path&gt;)"</c>, hoặc câu báo thiếu
    /// Brave nếu máy chưa cài.</summary>
    internal static string DescribeBrowser()
    {
        var exe = BrowserLocator.FindBraveExecutable();
        return exe is null ? BrowserLocator.ThieuBraveMessage : $"Brave ({exe})";
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
        IPlaywright playwright, string userDataDir, CancellationToken ct,
        Action<string>? log = null)
    {
        // Không có đường lùi: thiếu Brave là lỗi thật, ném ngay kèm câu chỉ chỗ tải.
        var exePath = BrowserLocator.RequireBraveExecutable();

        return await PhongVoiDonHoSoAsync(
            // DỌN HỒ SƠ TRƯỚC MỖI LẦN PHÓNG. Bỏ bước này là để cả vòng chết ngay ở bước đăng nhập khi còn sót cửa
            // sổ của hồ sơ (Chromium chuyển dòng lệnh cho tiến trình cũ rồi tiến trình mới thoát ngay ⇒ không ai
            // ghi DevToolsActivePort). Nguồn sót đã gặp thật: user bấm Dừng rồi Chạy lại ngay, trình duyệt sạch của
            // vòng trước fork sang PID khác nên handle ta giữ đã HasExited (kill trượt), app crash lần chạy trước.
            // KHÔNG match 'shopee-orders': ở đây chỉ cần hồ sơ CỦA MÌNH rảnh, đừng cướp cửa sổ của phiên khác.
            donHoSo: _ => BrowserProfileGuard.FreeProfile(userDataDir, alsoMatchBridgeExtension: false, log),
            phongLan: lan => LaunchOnceAsync(playwright, exePath, userDataDir, lan, ct),
            choTruocKhiThuLai: lan =>
            {
                log?.Invoke($"Mở trình duyệt đăng nhập hỏng ở lần {lan} (hồ sơ đang bị giữ) — dọn lại rồi thử lần kế.");
                return Task.Delay(ChoTruocKhiThuLaiMs, ct);
            },
            soLanToiDa: SoLanThuMoTrinhDuyet,
            ct).ConfigureAwait(false);
    }

    /// <summary>Số lần thử mở trình duyệt đăng nhập trong một vòng (lần đầu + đúng 1 lần thử lại).</summary>
    internal const int SoLanThuMoTrinhDuyet = 2;

    /// <summary>Chờ giữa hai lần thử — cho kill của lần dọn trước kịp ngấm rồi mới phóng lại.</summary>
    private const int ChoTruocKhiThuLaiMs = 1500;

    /// <summary>
    /// Vòng "dọn hồ sơ → phóng", thử tối đa <paramref name="soLanToiDa"/> lần. CHỈ thử lại đúng ca trình duyệt
    /// <b>thoát sớm</b> (<see cref="BrowserExitedEarlyException"/> = hồ sơ đang bị cửa sổ khác giữ); mọi lỗi khác
    /// và hủy đều ném thẳng lên (đừng biến lỗi thật thành thử-lại-vô-ích, đừng nuốt lệnh Dừng của user).
    /// Tách riêng + nhận delegate để test được luồng điều khiển này mà không cần trình duyệt thật.
    /// </summary>
    internal static async Task<T> PhongVoiDonHoSoAsync<T>(
        Action<int> donHoSo, Func<int, Task<T>> phongLan, Func<int, Task> choTruocKhiThuLai,
        int soLanToiDa, CancellationToken ct)
    {
        for (var lan = 1; ; lan++)
        {
            ct.ThrowIfCancellationRequested();
            donHoSo(lan);

            try
            {
                return await phongLan(lan).ConfigureAwait(false);
            }
            catch (BrowserExitedEarlyException) when (lan < soLanToiDa)
            {
                await choTruocKhiThuLai(lan).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Một lần phóng: xóa <c>DevToolsActivePort</c> cũ → phóng trình duyệt → chờ cổng CDP → chờ endpoint → nối CDP.
    /// Hỏng sau khi đã phóng thì tự dọn tiến trình trước khi ném (bên gọi chưa cầm được handle nào).
    /// </summary>
    private static async Task<(Process Process, IBrowser Browser, IBrowserContext Context)> LaunchOnceAsync(
        IPlaywright playwright, string exePath, string userDataDir, int lan, CancellationToken ct)
    {
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
            var port = await WaitForDevToolsPortAsync(portFile, process, exePath, userDataDir, lan,
                TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
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
    /// Trình duyệt thoát ngay khi vừa phóng — hầu như luôn là <i>process singleton handoff</i>: hồ sơ đang được một
    /// cửa sổ khác giữ nên Chromium chuyển dòng lệnh cho tiến trình cũ rồi tự thoát. Có kiểu riêng để tầng trên
    /// phân biệt được ca này mà thử lại (các lỗi khác thì không).
    /// </summary>
    internal sealed class BrowserExitedEarlyException : InvalidOperationException
    {
        internal BrowserExitedEarlyException(string message) : base(message) { }
    }

    /// <summary>
    /// Chờ Brave khởi động xong và ghi cổng CDP vào file <c>DevToolsActivePort</c> (dòng đầu = cổng).
    /// Poll có timeout; nếu tiến trình thoát sớm (thường do hồ sơ đang bị một cửa sổ trình duyệt khác giữ)
    /// thì ném <see cref="BrowserExitedEarlyException"/> (tầng trên thử lại 1 lần sau khi dọn hồ sơ).
    /// </summary>
    private static async Task<int> WaitForDevToolsPortAsync(
        string portFile, Process process, string exePath, string userDataDir, int lan,
        TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                throw new BrowserExitedEarlyException(MoTaThoatSom(process, exePath, userDataDir, lan));
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
    /// Dựng thông báo cho ca trình duyệt thoát ngay khi phóng: tên file thực thi + <b>mã thoát</b> + hồ sơ đang
    /// dùng + <b>lần thử thứ mấy</b>. Đủ để đọc log là biết trình duyệt nào, có phải handoff không (handoff
    /// thường trả mã 0) và bước dọn+thử-lại đã chạy hay chưa.
    /// Hàm thuần theo phần chuỗi — <paramref name="exitCode"/> do bên gọi đọc từ tiến trình.
    /// </summary>
    internal static string MoTaThoatSom(string exePath, string userDataDir, int? exitCode, int lan)
    {
        var ten = string.IsNullOrEmpty(exePath) ? "Trình duyệt" : Path.GetFileName(exePath);
        var ma = exitCode.HasValue ? exitCode.Value.ToString() : "?";
        return $"Trình duyệt thoát ngay khi khởi động ({ten}, mã thoát {ma}, lần thử {lan}/{SoLanThuMoTrinhDuyet}) — "
             + "hồ sơ đang bị một cửa sổ trình duyệt khác giữ nên lệnh bị chuyển sang cửa sổ đó. "
             + $"Hồ sơ: {userDataDir}. "
             + "Đã tự đóng các cửa sổ của hồ sơ này trước mỗi lần thử; vẫn lỗi thì đóng tay mọi cửa sổ "
             + "Brave/Chrome/Edge của app rồi chạy lại.";
    }

    /// <summary>Đọc mã thoát (nuốt lỗi nếu tiến trình chưa thoát/không đọc được) rồi gọi <see cref="MoTaThoatSom(string, string, int?, int)"/>.</summary>
    private static string MoTaThoatSom(Process process, string exePath, string userDataDir, int lan)
    {
        int? exitCode = null;
        try { exitCode = process.ExitCode; } catch { /* chưa thoát hẳn / không đọc được */ }
        return MoTaThoatSom(exePath, userDataDir, exitCode, lan);
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
}
