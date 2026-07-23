using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Dựng danh sách tham số dòng lệnh để tự khởi chạy <b>Brave thật</b> (hoặc Chromium đóng gói) rồi
/// nối vào bằng CDP. Hàm thuần (không IO/không trạng thái) nên test được độc lập.
/// <para>
/// Bộ cờ khớp theo <c>shopee-suite</c> (cùng cơ chế Brave + CDP) — đã chứng minh chạy tốt với Shopee.
/// KHÔNG ép <c>navigator.webdriver=false</c> nữa: bỏ hẳn việc tắt <c>AutomationControlled</c>
/// (cả <c>--disable-blink-features</c> lẫn <c>AutomationControlled</c> trong <c>--disable-features</c>).
/// Lý do: <c>shopee-suite</c> mở Brave+CDP mà KHÔNG ép webdriver=false vẫn chạy tốt ⇒ Shopee KHÔNG gate
/// captcha theo <c>navigator.webdriver</c>; việc tắt <c>AutomationControlled</c> vừa thừa, vừa là dấu hiệu
/// bị anti-bot soi, vừa can thiệp làm <b>captcha không load được</b> (app này xử lý đơn của một seller cố
/// định, phải để captcha load để giải tay). Để webdriver giữ giá trị tự nhiên như shopee-suite.
/// </para>
/// <para>
/// THÊM nhóm cờ chống-treo-nền (<c>--disable-background-timer-throttling</c>,
/// <c>--disable-backgrounding-occluded-windows</c>, <c>--disable-renderer-backgrounding</c>): khi mở nhiều
/// account song song, cửa sổ Brave bị che/chạy nền sẽ bị Brave bóp renderer → CDP treo/"hay lỗi". Nhóm cờ
/// này giữ renderer chạy đều để CDP ổn định. Kèm <c>--disable-features=...IntensiveWakeUpThrottling</c>
/// và <c>CalculateNativeWinOcclusion</c> cùng mục đích chống bóp tài nguyên nền.
/// </para>
/// <para>
/// Giữ nhu cầu riêng của app: <c>--disable-popup-blocking</c> cho nút "In phiếu giao" (mở tab bằng
/// <c>window.open</c>); <c>--lang=vi-VN</c> giữ locale VN. Vẫn <b>KHÔNG</b> thêm <c>--enable-automation</c>,
/// <c>--headless</c>, hay <c>--remote-debugging-pipe</c> (tránh thanh "controlled by automated test software"
/// và giữ launch giống trình duyệt người dùng bình thường).
/// </para>
/// </summary>
public static class BraveLaunchArgs
{
    /// <summary>
    /// Trả về danh sách tham số dòng lệnh cho Brave/Chromium:
    /// cổng gỡ lỗi CDP, thư mục hồ sơ riêng, nhóm cờ chống-treo-nền, locale VN, và proxy (nếu có).
    /// </summary>
    /// <param name="userDataDir">Thư mục hồ sơ persistent riêng cho tài khoản.</param>
    /// <param name="remoteDebuggingPort">Cổng CDP; truyền <c>0</c> để Chromium tự chọn cổng trống
    /// (đọc lại cổng thật từ file <c>DevToolsActivePort</c>).</param>
    /// <param name="proxy">Proxy đã chọn; <c>null</c> = đi IP máy. User/pass KHÔNG nhét vào chuỗi
    /// <c>--proxy-server</c> (Chromium không hỗ trợ) — xác thực xử lý qua CDP ở tầng trên.</param>
    public static IReadOnlyList<string> BuildBraveArgs(
        string userDataDir, int remoteDebuggingPort, ProxyEntry? proxy, string? extensionPath = null)
    {
        // Có extension → thêm DisableLoadExtensionCommandLineSwitch để Chrome/Brave 137+ CHO PHÉP --load-extension
        // (từ 137 mặc định chặn cờ này; khớp cách module Search làm). Không có ext → giữ danh sách cũ.
        var disableFeatures = "Translate,CalculateNativeWinOcclusion,IntensiveWakeUpThrottling";
        if (!string.IsNullOrEmpty(extensionPath))
        {
            disableFeatures += ",DisableLoadExtensionCommandLineSwitch";
        }

        var args = new List<string>
        {
            $"--remote-debugging-port={remoteDebuggingPort}",
            $"--user-data-dir={userDataDir}",
            "--profile-directory=Default",
            "--new-window",
            "--no-first-run",
            "--no-default-browser-check",
            "--hide-crash-restore-bubble",
            // Chống Brave bóp tài nguyên khi cửa sổ bị che/chạy nền (nhiều account mở song song) → tránh
            // CDP treo/"hay lỗi". Giữ renderer + timer chạy đều dù cửa sổ không ở tiền cảnh.
            "--disable-background-timer-throttling",
            "--disable-backgrounding-occluded-windows",
            "--disable-renderer-backgrounding",
            // Tắt các tính năng gây bóp/che tài nguyên: Translate (popup dịch), CalculateNativeWinOcclusion
            // (Brave coi cửa sổ bị che → giảm hoạt động), IntensiveWakeUpThrottling (bóp timer tab nền).
            // KHÔNG còn AutomationControlled ở đây — không ép webdriver=false nữa (khớp shopee-suite).
            $"--disable-features={disableFeatures}",
            // Locale tiếng Việt đặt bằng cờ trình duyệt (KHÔNG hook navigator.languages bằng JS —
            // hook JS tự tạo dấu hiệu lộ bot).
            "--lang=vi-VN",
            // KHÔNG chặn popup: nút "In phiếu giao" mở tab phiếu bằng window.open — nếu bị chặn popup thì
            // tab phiếu không mở ra (không bắt được để tải/in). Cho phép popup để tab phiếu luôn mở.
            "--disable-popup-blocking",
        };

        // Nạp extension (POC né anti-bot: thao tác Seller Centre bằng extension thay Playwright). Đường dẫn
        // do tầng gọi phân giải (thư mục extension đã giải nén). Không có → không nạp (giữ hành vi cũ).
        if (!string.IsNullOrEmpty(extensionPath))
        {
            args.Add($"--load-extension={extensionPath}");
        }

        if (proxy is not null)
        {
            // Proxy đặt qua --proxy-server (http:// hoặc socks5:// theo Type). KHÔNG kèm user:pass.
            args.Add($"--proxy-server={ProxyHealthChecker.ToProxyAddress(proxy)}");
        }

        return args;
    }

    /// <summary>
    /// Phân giải thư mục extension "shopee-orders-test" (POC): tìm <c>extensions/shopee-orders-test</c> cạnh
    /// exe (bản cài) rồi đi ngược lên vài cấp tìm ở gốc repo (khi chạy từ bin dev). Không thấy → <c>null</c>
    /// (không nạp extension). Trả về đường dẫn tuyệt đối nếu thư mục tồn tại (có manifest.json).
    /// </summary>
    public static string? ResolveOrdersExtension()
    {
        try
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var cand = Path.Combine(dir.FullName, "extensions", "shopee-orders-test");
                if (File.Exists(Path.Combine(cand, "manifest.json")))
                {
                    return cand;
                }
            }
        }
        catch { /* bỏ qua — không nạp extension */ }
        return null;
    }
}
