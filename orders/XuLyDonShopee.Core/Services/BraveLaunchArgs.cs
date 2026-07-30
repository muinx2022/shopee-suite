using Shopee.Toolkit.Browser;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Dựng danh sách tham số dòng lệnh để tự khởi chạy <b>Brave thật</b> (hoặc Chromium đóng gói) rồi
/// nối vào bằng CDP. Hàm thuần (không IO/không trạng thái) nên test được độc lập.
/// <para>
/// WRAPPER MỎNG: từng cờ do <see cref="BraveArgs"/> (shared/Shopee.Toolkit) dựng — CÙNG builder mà phía
/// suite dùng để phóng Brave, nên khối cờ nền cửa sổ / remote-debugging-port / load-extension không còn hai
/// bản chép tay. Ở đây chỉ còn CHÍNH SÁCH riêng của module Đơn hàng: dùng chế độ DANH SÁCH (args đi vào
/// <c>ProcessStartInfo.ArgumentList</c> / <c>args</c> của Playwright nên KHÔNG bọc ngoặc kép), thêm nhóm cờ
/// chống-treo-nền + locale VN + không chặn popup, và tuyệt đối không proxy.
/// </para>
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
    // Nhóm cờ chống Brave bóp tài nguyên khi cửa sổ bị che/chạy nền (nhiều account mở song song) → tránh
    // CDP treo/"hay lỗi". Giữ renderer + timer chạy đều dù cửa sổ không ở tiền cảnh.
    private static readonly string[] KhongBopTaiNguyenNen =
    {
        "--disable-background-timer-throttling",
        "--disable-backgrounding-occluded-windows",
        "--disable-renderer-backgrounding",
    };

    // Tắt các tính năng gây bóp/che tài nguyên: Translate (popup dịch), CalculateNativeWinOcclusion
    // (Brave coi cửa sổ bị che → giảm hoạt động), IntensiveWakeUpThrottling (bóp timer tab nền).
    // KHÔNG còn AutomationControlled ở đây — không ép webdriver=false nữa (khớp shopee-suite).
    private const string DisableFeaturesCoBan = "Translate,CalculateNativeWinOcclusion,IntensiveWakeUpThrottling";

    // Chrome/Brave 137+ MẶC ĐỊNH chặn --load-extension → phải tắt feature này thì extension mới nạp được.
    private const string ChoPhepLoadExtension = "DisableLoadExtensionCommandLineSwitch";

    /// <summary>
    /// Trả về danh sách tham số dòng lệnh cho Brave/Chromium:
    /// cổng gỡ lỗi CDP, thư mục hồ sơ riêng, nhóm cờ chống-treo-nền, locale VN.
    /// <para>KHÔNG còn nhánh <c>--proxy-server</c>: module Đơn hàng đã bỏ hẳn proxy runtime (mọi caller đi IP
    /// máy). Cần lại thì thêm ở đây, đừng dựng lại cả cụm xoay proxy đã gỡ.</para>
    /// </summary>
    /// <param name="userDataDir">Thư mục hồ sơ persistent riêng cho tài khoản.</param>
    /// <param name="remoteDebuggingPort">Cổng CDP; truyền <c>0</c> để Chromium tự chọn cổng trống
    /// (đọc lại cổng thật từ file <c>DevToolsActivePort</c>).</param>
    public static IReadOnlyList<string> BuildBraveArgs(
        string userDataDir, int remoteDebuggingPort, string? extensionPath = null)
    {
        // Có extension → thêm DisableLoadExtensionCommandLineSwitch để Chrome/Brave 137+ CHO PHÉP --load-extension
        // (khớp cách module Search làm). Không có ext → giữ danh sách cũ.
        var coExtension = !string.IsNullOrEmpty(extensionPath);
        var disableFeatures = coExtension
            ? $"{DisableFeaturesCoBan},{ChoPhepLoadExtension}"
            : DisableFeaturesCoBan;

        var b = BraveArgs.CreateRaw()
            .RemoteDebuggingPort(remoteDebuggingPort)
            .WindowBlock(userDataDir)
            .AddRange(KhongBopTaiNguyenNen)
            .Add($"--disable-features={disableFeatures}")
            // Locale tiếng Việt đặt bằng cờ trình duyệt (KHÔNG hook navigator.languages bằng JS —
            // hook JS tự tạo dấu hiệu lộ bot).
            .Add("--lang=vi-VN")
            // KHÔNG chặn popup: nút "In phiếu giao" mở tab phiếu bằng window.open — nếu bị chặn popup thì
            // tab phiếu không mở ra (không bắt được để tải/in). Cho phép popup để tab phiếu luôn mở.
            .Add("--disable-popup-blocking");

        // Nạp extension (POC né anti-bot: thao tác Seller Centre bằng extension thay Playwright). Đường dẫn
        // do tầng gọi phân giải (thư mục extension đã giải nén). Không có → không nạp (giữ hành vi cũ).
        if (coExtension)
        {
            b.LoadExtension(extensionPath!);
        }

        return b.BuildList();
    }

    /// <summary>
    /// Dựng args cho đường POC "mở sạch": KHÔNG --remote-debugging-port (không mở endpoint CDP), KHÔNG proxy,
    /// CÓ --load-extension + start URL ở cuối. Mục tiêu: trình duyệt giống hệt bản mở tay (không có kênh CDP để
    /// anti-bot soi / để Playwright attach), extension tự điều hướng + tự bắn trusted click qua chrome.debugger.
    /// </summary>
    public static IReadOnlyList<string> BuildCleanPocArgs(string userDataDir, string extensionPath, string startUrl)
    {
        // POC LUÔN nạp extension → luôn kèm DisableLoadExtensionCommandLineSwitch. KHÁC BuildBraveArgs: bỏ hẳn
        // --remote-debugging-port (không mở endpoint CDP) và bỏ nhánh proxy; thêm startUrl positional ở cuối.
        return BraveArgs.WindowRaw(userDataDir)
            .AddRange(KhongBopTaiNguyenNen)
            .Add($"--disable-features={DisableFeaturesCoBan},{ChoPhepLoadExtension}")
            .Add("--lang=vi-VN")
            .Add("--disable-popup-blocking")
            .LoadExtension(extensionPath)
            // startUrl positional cuối cùng — mở URL kiểu người dùng (KHÔNG phải CDP navigation).
            .StartUrl(startUrl)
            .BuildList();
    }

    /// <summary>
    /// Phân giải thư mục extension "shopee-orders" (cầu nối WebSocket + trusted click): đi từ thư mục exe
    /// ngược lên tìm <c>extensions/shopee-orders</c> có <c>manifest.json</c>. Không thấy → <c>null</c>.
    /// </summary>
    public static string? ResolveOrdersBridgeExtension() => ResolveExtensionByName("shopee-orders");

    /// <summary>Đi từ thư mục exe ngược lên các cấp cha, trả về đường dẫn tuyệt đối thư mục
    /// <c>extensions/&lt;name&gt;</c> đầu tiên có <c>manifest.json</c>; không thấy → <c>null</c>.</summary>
    private static string? ResolveExtensionByName(string name)
    {
        try
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var cand = Path.Combine(dir.FullName, "extensions", name);
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
