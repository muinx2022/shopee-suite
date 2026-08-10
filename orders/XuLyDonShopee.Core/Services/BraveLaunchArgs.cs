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

    // ⛔ ĐÃ GỠ 10/08/2026 — ĐỪNG THÊM LẠI. Sáng 10/08 từng thêm nhóm cờ chặn discard/freeze tab
    // ("HighEfficiencyModeAvailable, BatterySaverModeAvailable, PerformanceControlsPerformanceInterventions,
    // FreezingOnEnergySaver, ModernDiscardStrategy") để chữa lỗi tab nền bị vứt khỏi bộ nhớ. Cái giá đắt hơn
    // nhiều lần: từ đó KHÔNG vòng nào đi hết 12 shop nữa — trình duyệt sạch TỰ CHẾT giữa vòng, luôn trong kỳ
    // nghỉ 3–4' (lúc máy đóng băng/vứt tab chạy), luôn sau ~23 phút. Bằng chứng dứt điểm:
    //     11:54:38 ⚠ Trình duyệt sạch (PID 22024) đã THOÁT — mã thoát -2147483645 (0x80000003).
    // 0x80000003 = STATUS_BREAKPOINT = Chromium tự kết liễu vì một CHECK thất bại; KHÔNG phải hết RAM (máy còn
    // 15,7 GB), KHÔNG phải bị kill từ ngoài (0x40010004), KHÔNG phải thoát êm (0). Tắt nửa vời bộ máy tiết kiệm
    // tài nguyên để lại đúng những đường mã Chromium không lường tới.
    // Lỗi discard nguyên bản vẫn còn lưới: extension nạp lại tab discarded/unloaded rồi thử lại một lượt
    // (execInTab) — mà thực tế 3 vòng gần nhất không hề tái phát ("discarded=" 0 lần).
    // Guard chống thêm lại: BraveCleanPocArgsTests.KhongChanVutTabKhoiBoNho_VonLamTrinhDuyetTuChet.

    // Tắt các tính năng gây bóp/che tài nguyên: Translate (popup dịch), CalculateNativeWinOcclusion
    // (Brave coi cửa sổ bị che → giảm hoạt động), IntensiveWakeUpThrottling (bóp timer tab nền).
    // KHÔNG còn AutomationControlled ở đây — không ép webdriver=false nữa (khớp shopee-suite).
    // Nhóm chặn tải model AI on-device KHÔNG liệt kê ở đây: BraveArgs tự nối cho MỌI call-site
    // (xem BraveArgs.OnDeviceAiModelFeatures) nên chuỗi cuối cùng dài hơn hằng này.
    private const string DisableFeaturesCoBan = "Translate,CalculateNativeWinOcclusion,IntensiveWakeUpThrottling";

    // Chrome/Brave 137+ MẶC ĐỊNH chặn --load-extension → phải tắt feature này thì extension mới nạp được.
    private const string ChoPhepLoadExtension = "DisableLoadExtensionCommandLineSwitch";

    // Model AI on-device (OptGuideOnDeviceModel, 3,98 GB/hồ sơ) được CÀI QUA COMPONENT UPDATER về gốc
    // user-data-dir → chặn updater là đường chặn trực tiếp nhất, bổ sung cho nhóm feature của BraveArgs.
    // Cùng cờ mà 4 đường phóng phía suite đã dùng sẵn (BraveArgs.DiskCacheLimitFlags).
    private const string ChanComponentUpdater = "--disable-component-update";

    /// <summary>
    /// <b>ĐỪNG GỠ.</b> Cờ này chặn đúng cái đã giết trình duyệt sạch suốt ngày 10/08/2026.
    /// <para>
    /// Triệu chứng: trình duyệt tự thoát sau ~23,5 phút, mã <c>0x80000003</c> (STATUS_BREAKPOINT), lặp lại ở BỐN
    /// vòng liên tiếp với sai số 2 giây (22m47s · 23m29s · 23m28s · 23m30s). Đã đoán sai một lượt (đổ cho nhóm cờ
    /// chặn vứt tab — gỡ rồi vẫn chết y hệt). Crash dump trong <c>&lt;hồ sơ&gt;\Crashpad\reports</c> khai thẳng:
    /// </para>
    /// <code>
    /// [24388:13324:0810/122730.160:FATAL:content\browser\gpu\gpu_data_manager_impl_private.cc:417]
    /// GPU process isn't usable. Goodbye.
    /// </code>
    /// <para>
    /// Tiến trình GPU chết rồi dựng lại đều đặn ~2 phút/lần (nhật ký Chromium: gpu-process mới lúc 12:31:43 rồi
    /// 12:33:43). Chromium đếm số lần chết, tụt dần qua các chế độ dự phòng, hết đường thì <b>tự kết liễu CẢ
    /// trình duyệt</b> — đó chính là mốc ~23,5 phút. Cờ này tắt bộ đếm đó: GPU có chết rồi dựng lại thì kệ, trình
    /// duyệt sống tiếp.
    /// </para>
    /// <para>CỐ Ý KHÔNG dùng <c>--disable-gpu</c>: tắt GPU đẩy WebGL sang SwiftShader, mà chuỗi renderer
    /// "Google SwiftShader" là một dấu hiệu bot kinh điển — cả kiến trúc này sinh ra để né anti-bot.</para>
    /// </summary>
    private const string KhongGietTrinhDuyetKhiGpuChet = "--disable-gpu-process-crash-limit";

    // 🔎 CÁCH BẬT LẠI NHẬT KÝ CHROMIUM khi cần mổ xẻ trình duyệt (đã dùng 10/08/2026 để tìm ra lỗi GPU ở trên):
    // thêm "--enable-logging" vào chuỗi cờ dưới → Chromium ghi `<hồ sơ>\chrome_debug.log`. ĐỪNG thêm "--v=1":
    // verbose đổ cả nghìn dòng net/console mỗi phút, đọc không nổi mà vẫn không thấy dòng cần.
    // KHÔNG bật mặc định: file ghi liên tục, không có trần, mà repo này từng ăn quả hồ sơ rò 4 GB.
    // Lưu ý: KHÔNG CẦN cờ này để bắt lỗi trình duyệt tự chết — Crashpad vẫn tự ghi dump vào
    // `<hồ sơ>\Crashpad\reports\*.dmp`, và chính dump đó đã khai ra "GPU process isn't usable. Goodbye."

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
            // BraveArgs tự nối thêm nhóm feature chặn tải model AI on-device (~4 GB/hồ sơ) vào cùng MỘT cờ.
            .DisableFeatures(disableFeatures)
            // Chặn component updater — ĐÂY mới là đường model AI on-device được cài về gốc hồ sơ. Bằng chứng
            // tại chỗ: hai hồ sơ rò 3,98 GB (07/08/2026) đều là hồ sơ của orders, và orders là đường phóng DUY
            // NHẤT không có cờ này (4 đường phía suite đều có qua BraveArgs.DiskCacheLimit và không hồ sơ nào rò).
            .Add(ChanComponentUpdater)
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
            .DisableFeatures(DisableFeaturesCoBan, ChoPhepLoadExtension)
            // Hồ sơ POC cũng bền → chặn component updater (đường cài model AI 4 GB), xem BuildBraveArgs.
            .Add(ChanComponentUpdater)
            .Add(KhongGietTrinhDuyetKhiGpuChet)
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
