using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Một phiên đăng nhập đang mở (cửa sổ trình duyệt). Giữ tham chiếu tới browser/context
/// và cho phép bắt cookie khi người dùng đã đăng nhập xong. Đóng phiên qua <c>DisposeAsync</c>.
/// </summary>
public interface ILoginSession : IAsyncDisposable
{
    /// <summary>Lấy toàn bộ cookie hiện có của phiên dưới dạng JSON (định dạng <see cref="CookieJson"/>).</summary>
    Task<string> CaptureCookiesJsonAsync();

    /// <summary>Task hoàn tất khi người dùng đóng cửa sổ trình duyệt (tiến trình Brave thoát / CDP ngắt).</summary>
    Task Closed { get; }

    /// <summary>True nếu cửa sổ trình duyệt đã đóng.</summary>
    bool IsClosed { get; }

    /// <summary>
    /// Tiến trình Brave/Chromium mà phiên đang sở hữu. Dùng ở tầng App để (Plan B) đưa cửa sổ ra trước
    /// (focus) và để kill dự phòng khi dừng phiên. Null nếu phiên không giữ tiến trình.
    /// </summary>
    Process? BraveProcess { get; }

    /// <summary>
    /// Số cửa sổ/tab (Pages) đang mở của phiên. Dùng làm tín hiệu "người dùng đã đóng hết cửa sổ"
    /// đáng tin hơn "tiến trình Brave chết" (Brave có thể còn chạy nền). Trả 0 nếu context đã ngắt.
    /// </summary>
    int OpenPageCount { get; }

    /// <summary>
    /// <b>Phát hiện trạng thái trang bán hàng</b> sau khi mở seller URL: đã đăng nhập / form đăng nhập /
    /// trang verify / trang captcha / không rõ. Ưu tiên theo URL (captcha, <c>/verify</c>), rồi ô đăng nhập
    /// hiển thị (kiểm <c>getClientRects</c>), rồi cookie phiên. Dùng để điều phối auto-login → verify →
    /// captcha-retry ở tầng App.
    /// <para><b>Graceful — không bao giờ ném:</b> không có trang / lỗi bất kỳ → <see cref="ShopeePageState.Unknown"/>.</para>
    /// </summary>
    Task<ShopeePageState> DetectPageStateAsync(CancellationToken ct = default);

    /// <summary>
    /// <b>Xác minh đăng nhập qua email Hotmail/Outlook</b> khi Shopee bắt verify: (1) trên trang verify
    /// Shopee click lựa chọn "verify qua email"; (2) mở TAB MỚI đăng nhập hộp thư Hotmail/Outlook
    /// (username → "Use your password" → password → "Stay signed in?" Yes) — mọi bước dò nhiều selector,
    /// timeout ngắn bỏ qua được, KHÔNG fail cứng; (3) vào hộp thư, ưu tiên tab "Khác"/"Other", tìm mail
    /// Shopee MỚI NHẤT, mở mail rồi click link/nút xác nhận (bắt tab mới nếu link mở tab), đóng tab;
    /// (4) quay lại tab seller, chờ trạng thái về <see cref="ShopeePageState.LoggedIn"/>.
    /// <para>
    /// <b>Graceful — không bao giờ ném (trừ hủy):</b> thiếu cấu hình / không tìm được lựa chọn / login mail
    /// lỗi / không thấy mail / hết thời gian → <c>false</c> (caller giữ phiên cho người dùng verify tay).
    /// Trả <c>true</c> khi seller đã về LoggedIn sau khi click xác nhận. LUÔN đóng các tab đã mở (finally),
    /// KHÔNG log giá trị mật khẩu. Mọi bước ghi qua <paramref name="log"/> để theo dõi trên panel nhật ký.
    /// </para>
    /// </summary>
    Task<bool> TryVerifyByEmailAsync(
        string verifyEmail, string verifyEmailPassword, bool autoConfirm, Action<string>? log = null, CancellationToken ct = default);

    /// <summary>
    /// <b>Đăng nhập Nền tảng tài khoản phụ</b> (<see cref="ShopeeLoginService.SubaccountUrl"/>): nếu đang ở
    /// form đăng nhập subaccount thì tự điền tài khoản (<paramref name="user"/>) + mật khẩu (<paramref name="password"/>)
    /// kiểu người rồi bấm "Đăng nhập"; khi Shopee đòi mã thì <b>mở hộp thư</b> (<paramref name="verifyEmail"/> /
    /// <paramref name="verifyEmailPassword"/>) cho người dùng TỰ lấy mã (KHÔNG tự verify, KHÔNG tự bấm gì trong mail),
    /// đưa cửa sổ về trang Shopee; chờ người dùng nhập code (tối đa 15') tới khi nav "Tài khoản của tôi" hiện thì
    /// DỪNG ở đó (ĐÃ đăng nhập subaccount). Rồi <b>bắc cầu SSO</b>: click "Tài khoản của tôi" → "Kênh Người bán" để
    /// chuyển phiên sang Seller Centre (<c>banhang.shopee.vn</c> — lập cookie seller) và chuẩn hóa <c>Pages[0]</c> =
    /// tab banhang; caller (RunAsync) rồi mới mở danh sách shop <c>/portal/shop</c> và lặp qua từng shop (xem
    /// danh sách shop rồi mở chi tiết từng shop).
    /// <para>
    /// <b>Graceful — không bao giờ ném (trừ hủy người dùng):</b> mọi thất bại (không thấy ô/nút, hết giờ, lỗi) →
    /// <c>false</c> (caller GIỮ cửa sổ cho người dùng thao tác tay). Trả <c>true</c> khi đã bắc cầu SSO sang Seller
    /// Centre (<c>Pages[0]</c> là tab <c>banhang.shopee.vn</c>). KHÔNG log giá trị mật khẩu; mọi nhánh selector trượt
    /// đều log <c>title=…, url=…</c>.
    /// </para>
    /// </summary>
    Task<bool> TryLoginSubaccountAsync(
        string user, string password, string? verifyEmail, string? verifyEmailPassword,
        Action<string>? log = null, CancellationToken ct = default);

}

/// <summary>
/// Mở trang Shopee Seller Centre bằng <b>Brave thật</b> (tự khởi chạy tiến trình Brave rồi nối vào
/// qua CDP — <see cref="IBrowserType.ConnectOverCDPAsync"/>), định tuyến qua proxy nếu có, để người
/// dùng tự đăng nhập; sau đó bắt cookie phiên.
/// <para>
/// Vì tự launch Brave như trình duyệt bình thường (KHÔNG để Playwright launch với cờ
/// <c>--enable-automation</c>) nên KHÔNG hiện thanh "controlled by automated test software" và
/// <c>navigator.webdriver</c> giữ <c>false</c> — <b>do chính Brave thật</b>, không do vá JS.
/// CHỦ ĐÍCH <b>không tiêm init script vá fingerprint</b> (plugins/WebGL/webdriver/window.chrome...) vì
/// các vá đó lại <b>tự tạo dấu hiệu lộ bot</b> (own-property <c>navigator.webdriver</c>, hàm mất
/// <c>"[native code]"</c>, plugin giả không phải <c>Plugin</c>). Dựa vào Brave thật vốn đã sạch
/// (webdriver=false, plugins/window.chrome/WebGL thật) + hành vi kiểu người (Plan 2). Locale VN đặt qua
/// cờ <c>--lang=vi-VN</c>. <b>Không đảm bảo 100%</b> né được anti-bot của Shopee (CDP/fingerprint/hành
/// vi/IP vẫn có thể bị dò) — đây là best-effort.
/// </para>
/// <para>
/// Ưu tiên mở <b>Brave</b> nếu đã cài trên máy; nếu không có Brave dùng <b>Chromium đóng gói</b> của
/// Playwright (cùng cơ chế CDP).
/// </para>
/// </summary>
public class ShopeeLoginService
{
    /// <summary>URL trang bán hàng (Shopee Seller Centre).</summary>
    public const string SellerUrl = "https://banhang.shopee.vn/";

    /// <summary>URL Nền tảng tài khoản phụ — điểm vào đăng nhập mới (một tài khoản có nhiều shop).</summary>
    public const string SubaccountUrl = "https://subaccount.shopee.com/";

    /// <summary>Trang "Tài khoản" của Nền tảng tài khoản phụ — điểm vào của BẢN SẠCH (cầu nối): có cookie hồ sơ →
    /// hiện trang tài khoản (có "Kênh Người bán"); hết cookie → ra form đăng nhập. Dùng để SSO lại về trang chọn
    /// shop (né sticky-shop server-side khi mở thẳng /portal/shop).</summary>
    public const string SubaccountAccountUrl = "https://subaccount.shopee.com/account";

    /// <summary>URL bảng danh sách shop của Nền tảng tài khoản phụ — sau khi đăng nhập, mở thẳng đây để lặp qua từng shop.</summary>
    public const string ShopListUrl = "https://banhang.shopee.vn/portal/shop";

    // ===== Forwarder cho test (luồng verify-email) =====
    // Logic khớp text thực nằm trong nested class LoginSession (nơi giữ các Regex + luồng verify-email). Phơi
    // lại ở cấp class ngoài (internal — InternalsVisibleTo cho XuLyDonShopee.Tests) để unit-test được các hàm
    // thuần này mà không cần dựng cả phiên trình duyệt.

    /// <summary>Chuẩn hóa text để so khớp bền (bỏ dấu tiếng Việt kể cả đ→d, gộp space, hạ chữ thường).</summary>
    internal static string NormalizeForMatch(string? s) => LoginSession.NormalizeForMatch(s);

    /// <summary>True nếu dòng mail là "Cảnh báo bảo mật Tài khoản Shopee" (người gửi shopee + tiêu đề chứa
    /// "cảnh báo bảo mật"); loại mail trả hàng/khác của Shopee.</summary>
    internal static bool IsSecurityWarningMailRow(string? rowText) => LoginSession.IsSecurityWarningMailRow(rowText);

    /// <summary>True nếu text khớp link xác nhận cần bấm (vd "TẠI ĐÂY") — KHÔNG còn khớp "here"/"click here".</summary>
    internal static bool MatchesConfirmLink(string? text) => LoginSession.MatchesConfirmLink(text);

    /// <summary>True nếu text là trang báo link đã hết hạn/hết hiệu lực.</summary>
    internal static bool MatchesConfirmExpired(string? text) => LoginSession.MatchesConfirmExpired(text);

    /// <summary>True nếu text là nav "Tài khoản của tôi" trên Nền tảng tài khoản phụ (tín hiệu ĐÃ đăng nhập).</summary>
    internal static bool MatchesMyAccountNav(string? text) => LoginSession.MatchesMyAccountNav(text);

    /// <summary>True nếu text là entry "Kênh Người bán" (mở sang Seller Centre) trên Nền tảng tài khoản phụ —
    /// dùng ở bước bắc cầu SSO cuối TryLoginSubaccountAsync.</summary>
    internal static bool MatchesSellerChannelEntry(string? text) => LoginSession.MatchesSellerChannelEntry(text);

    /// <summary>Chuyển JSON mảng <c>{rowKey,name,login}</c> (đọc từ bảng <c>/portal/shop</c>) thành danh sách
    /// <see cref="ShopListItem"/> — forwarder để unit-test hàm thuần mà không cần dựng phiên trình duyệt.</summary>
    internal static IReadOnlyList<ShopListItem> ParseShopListJson(string? json) => LoginSession.ParseShopListJson(json);

    /// <summary>Chuyển JSON mảng đơn (do <c>pageScanOrders</c>/<c>ScanOrdersJs</c> đọc từ DOM) thành danh sách
    /// <see cref="SyncedOrder"/> — forwarder tái dùng hàm thuần <c>LoginSession.ParseOrdersJson</c> cho cầu nối
    /// extension (<c>OrdersBridgeSession</c>), không viết lại logic parse.</summary>
    internal static List<SyncedOrder> ParseOrdersJson(string? json) => LoginSession.ParseOrdersJson(json);

    /// <summary>
    /// Đảm bảo có sẵn trình duyệt để mở cho <paramref name="browserChoice"/>. Nếu phân giải được một
    /// trình duyệt thật đã cài trên máy (Chrome/Edge/Brave tùy lựa chọn) thì trả về ngay (0) mà
    /// <b>không tải</b> Chromium đóng gói. Ngược lại (không có trình duyệt thật phù hợp, hoặc chọn
    /// Chromium đóng gói) thì tải Chromium của Playwright (~150MB lần đầu; idempotent — đã cài thì
    /// trả về nhanh). Trả về exit code (0 = thành công); bọc try/catch, trả code khác 0 khi lỗi để
    /// tầng gọi thông báo.
    /// </summary>
    public int EnsureBrowserInstalled(BrowserChoice browserChoice = BrowserChoice.Auto)
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
    public static string DescribeBrowser(BrowserChoice browserChoice)
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
    /// Mở một cửa sổ trình duyệt (Brave nếu có, không thì Chromium đóng gói) tới trang bán hàng bằng
    /// <b>hồ sơ persistent</b> đặt tại <paramref name="userDataDir"/> (mỗi tài khoản một thư mục riêng)
    /// — nhờ đó lần sau mở lại vẫn còn đăng nhập — rồi trả về phiên đang mở. Đi thẳng IP máy (module đã bỏ
    /// hẳn proxy runtime). Cơ chế: tự khởi chạy tiến trình Brave với cờ stealth + <c>--user-data-dir</c> +
    /// <c>--remote-debugging-port</c>, chờ CDP sẵn sàng, nối vào qua
    /// <see cref="IBrowserType.ConnectOverCDPAsync"/>. Ném <see cref="InvalidOperationException"/>
    /// (message tiếng Việt) nếu không mở được.
    /// </summary>
    public async Task<ILoginSession> OpenAsync(
        string userDataDir, BrowserChoice browserChoice = BrowserChoice.Auto, CancellationToken ct = default)
    {
        IPlaywright? playwright = null;
        Process? process = null;
        IBrowser? browser = null;

        try
        {
            playwright = await Playwright.CreateAsync().ConfigureAwait(false);

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
            process = BrowserProcessStarter.StartOrFallback(exePath, launchArgs);
            process.EnableRaisingEvents = true;

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

            // CHỦ ĐÍCH KHÔNG tiêm init script vá fingerprint: Brave thật đã sạch (webdriver=false,
            // plugins/window.chrome/WebGL thật), vá lại chỉ tự tạo dấu hiệu lộ bot. Locale VN đặt qua
            // cờ --lang=vi-VN trong BraveLaunchArgs.

            var page = context.Pages.Count > 0
                ? context.Pages[0]
                : await context.NewPageAsync().ConfigureAwait(false);

            try
            {
                await page.GotoAsync(SubaccountUrl, new PageGotoOptions
                {
                    Timeout = 60000,
                    WaitUntil = WaitUntilState.DOMContentLoaded
                }).ConfigureAwait(false);
            }
            catch
            {
                // Nuốt lỗi timeout/điều hướng — vẫn giữ cửa sổ mở để người dùng tự thao tác.
            }

            return new LoginSession(playwright, browser, context, process);
        }
        catch (Exception ex)
        {
            // Dọn dẹp: ngắt CDP, KILL cả cây tiến trình Brave (tránh Brave mồ côi giữ khóa hồ sơ),
            // giải phóng Playwright.
            if (browser is not null)
            {
                try { await browser.CloseAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }
            }
            if (process is { HasExited: false })
            {
                try { process.Kill(entireProcessTree: true); } catch { /* bỏ qua */ }
            }
            try { process?.Dispose(); } catch { /* bỏ qua */ }
            try { playwright?.Dispose(); } catch { /* bỏ qua */ }

            throw new InvalidOperationException(
                "Không mở được trình duyệt Shopee. Kiểm tra đã cài Brave hoặc Chromium và kết nối mạng. " +
                "Chi tiết: " + ex.Message, ex);
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

    /// <summary>
    /// Phiên đăng nhập <b>sở hữu tiến trình Brave</b>: <see cref="Closed"/> hoàn tất khi tiến trình
    /// thoát / CDP ngắt / context đóng; <see cref="DisposeAsync"/> ngắt CDP và KILL cả cây tiến trình
    /// Brave để không để lại tiến trình mồ côi giữ khóa hồ sơ.
    /// </summary>
    private sealed class LoginSession : ILoginSession
    {
        private readonly IPlaywright _playwright;
        private readonly IBrowser _browser;
        private readonly IBrowserContext _context;
        private readonly Process _process;

        // "TRANG LÀM VIỆC" hiện tại của các hàm flow đơn (mô hình nhiều-shop): tab của shop đang được mở qua
        // OpenShopDetailAsync. null → dùng Pages[0] (tab gốc / danh sách shop). Các hàm flow đọc qua WorkPage()
        // để chạy trên ĐÚNG tab shop thay vì cứng Pages[0]. volatile: RunAsync (thread nền) đặt, hàm flow đọc.
        private volatile IPage? _workPage;

        // Hoàn tất khi cửa sổ đóng (tiến trình Brave thoát / CDP ngắt). RunContinuationsAsynchronously
        // để không chạy tiếp phần chờ ngay trong callback sự kiện của Playwright/Process.
        private readonly TaskCompletionSource _closedTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LoginSession(IPlaywright playwright, IBrowser browser, IBrowserContext context, Process process)
        {
            _playwright = playwright;
            _browser = browser;
            _context = context;
            _process = process;

            // Người dùng đóng cửa sổ → tiến trình Brave thoát (tín hiệu chính); kèm CDP ngắt / context đóng.
            _process.EnableRaisingEvents = true;
            _process.Exited += (_, _) => _closedTcs.TrySetResult();
            _browser.Disconnected += (_, _) => _closedTcs.TrySetResult();
            _context.Close += (_, _) => _closedTcs.TrySetResult();

            // Phòng trường hợp tiến trình đã thoát trước khi gắn handler.
            if (_process.HasExited)
            {
                _closedTcs.TrySetResult();
            }
        }

        public Task Closed => _closedTcs.Task;

        public bool IsClosed => _closedTcs.Task.IsCompleted;

        public Process? BraveProcess => _process;

        public int OpenPageCount
        {
            get
            {
                // Context đã ngắt (browser chết) → coi như không còn cửa sổ.
                try { return _context.Pages.Count; }
                catch { return 0; }
            }
        }

        /// <summary>Đặt "trang làm việc" hiện tại (tab shop) cho các hàm flow đơn. null → về Pages[0].</summary>
        internal void SetWorkPage(IPage? p) => _workPage = p;

        /// <summary>"Trang làm việc" hiện tại của các hàm flow đơn: <see cref="_workPage"/> (tab shop) nếu đã đặt,
        /// ngược lại Pages[0] (tab gốc). null nếu không còn tab nào.</summary>
        private IPage? WorkPage()
        {
            var wp = _workPage;
            if (wp is not null && !wp.IsClosed)
            {
                return wp;
            }
            try { return _context.Pages.Count > 0 ? _context.Pages[0] : null; }
            catch { return null; }
        }

        // Selector ô đăng nhập Shopee (thử theo thứ tự; selector Shopee CÓ THỂ ĐỔI → luôn có fallback,
        // không thấy gì thì bỏ qua để người dùng tự nhập tay).
        private static readonly string[] UserSelectors =
        {
            "input[name='loginKey']",       // ô user chính của Shopee
            "input[type='text']",           // fallback: ô text đầu tiên
            "input[type='email']",
            "input[type='tel']",
        };

        private static readonly string[] PasswordSelectors =
        {
            "input[name='password']",       // ô mật khẩu chính
            "input[type='password']",       // fallback theo type
        };

        private static readonly string[] SubmitSelectors =
        {
            "button[type='submit']",        // nút submit chính
            "button:has-text('Đăng nhập')", // fallback: nút chứa chữ "Đăng nhập"
            "button:has-text('ĐĂNG NHẬP')",
        };

        // ===================== Nền tảng tài khoản phụ (subaccount.shopee.com) =====================
        // Form login subaccount là Vue SPA: input KHÔNG có name → dò trong .login-card trước, rồi placeholder,
        // rồi type (fallback rộng nhất cuối). Nút "Đăng nhập" là <button type="button"> (KHÔNG phải submit) chứa
        // <span>Đăng nhập</span> → tuyệt đối không dò button[type='submit']; khớp text bằng SignInRegex có sẵn.
        private static readonly string[] SubUserSelectors =
            { ".login-card input[type='text']", "input[placeholder*='Tên đăng nhập']", "input[placeholder*='SĐT']", "input[type='text']" };
        private static readonly string[] SubPassSelectors =
            { ".login-card input[type='password']", "input[type='password']" };
        private static readonly string[] SubSubmitSelectors =
            { ".login-card button.shopee-button--primary", "button.shopee-button--primary", "button", "[role='button']" };

        // Nav trái "Tài khoản của tôi" (tín hiệu ĐÃ đăng nhập) + entry "Kênh Người bán" (mở Seller Centre). Mỗi regex
        // chứa CẢ dạng có dấu (khớp InnerText thô NFC qua FindVisibleByTextAsync) LẪN dạng không dấu (khớp text đã qua
        // NormalizeForMatch trong matcher/test, và trang render ascii). KHÔNG bám text EN cứng — có nhánh vi + en.
        private static readonly Regex MyAccountNavRegex =
            new(@"tài khoản của tôi|tai khoan cua toi|my account", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Dùng ở bước bắc cầu SSO cuối TryLoginSubaccountAsync (click "Kênh Người bán" để chuyển sang Seller Centre).
        private static readonly Regex SellerChannelRegex =
            new(@"kênh người bán|kenh nguoi ban|seller\s*cent(re|er)|seller\s*channel", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // ===================== Phát hiện trạng thái trang + verify qua email Hotmail =====================

        // Selector ô đăng nhập Shopee dùng để NHẬN DIỆN "đang ở form login" (CỤ THỂ, không dùng input[type=text]
        // chung — trang bán hàng đã đăng nhập có ô tìm kiếm sẽ nhận nhầm). Kiểm hiển thị bằng getClientRects.
        private static readonly string[] LoginFormDetectSelectors =
        {
            "input[name='loginKey']",
            "input[name='password']",
            "input[type='password']",
        };

        // --- Selector đăng nhập Microsoft/Outlook (đổi thường xuyên → luôn nhiều fallback, timeout ngắn bỏ qua được) ---
        private static readonly string[] MsUserSelectors =
            { "input[type='email']", "input[name='loginfmt']", "#i0116" };
        private static readonly string[] MsPasswordSelectors =
            { "input[name='passwd']", "input[type='password']", "#i0118" };
        private static readonly string[] MsSubmitSelectors =
            { "#idSIButton9", "input[type='submit']", "button[type='submit']" };
        private static readonly string[] MsUsePasswordSelectors =
            { "#idA_PWD_SwitchToPassword", "a", "[role='button']", "button", "span" };
        // Link "Các cách khác để đăng nhập" trên form mới "Xác minh email của bạn" (Fluent UI):
        // span[role='button'] class fui-Link trong span[data-testid='viewFooter'].
        private static readonly string[] MsOtherWaysSelectors =
            { "span[role='button']", "[role='button']", "a", "button" };
        // Lựa chọn "Mật khẩu"/"Password" trên màn danh sách cách đăng nhập (sau khi bấm "Các cách khác"):
        // clickable trước — thứ tự selector là thứ tự ưu tiên (button/role trước div/span to).
        private static readonly string[] MsPasswordOptionSelectors =
            { "button", "[role='button']", "[role='radio']", "[role='listitem']", "[role='link']", "div[data-testid]", "span" };
        // KMSI ("Stay signed in?") chỉ dùng ID: UI cũ là <input value="Yes"> KHÔNG có innerText → không match theo text.
        // KHÔNG dùng "button[type='submit']" trần: trên form mới "Xác minh email" nút submit chính là "Gửi mã" → click nhầm.
        private static readonly string[] MsKmsiYesSelectors =
            { "#acceptButton", "#idSIButton9" };
        // Nút "Đăng nhập"/"Sign in" ở trang landing (khi chưa nhảy thẳng vào form nhập email).
        private static readonly string[] MsSignInSelectors =
            { "a[data-task='signin']", "a[href*='login.live.com']", "a[href*='login.microsoftonline']", "a[href*='login']", "a", "button", "[role='button']" };

        // --- Regex đa ngôn ngữ (vi/en), KHÔNG bám text EN cứng ---
        private static readonly Regex VerifyEmailOptionRegex =
            new("email", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex UsePasswordRegex =
            new(@"use.*password|dùng mật khẩu|sử dụng mật khẩu|mật khẩu|mat khau", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Link "Các cách khác để đăng nhập" (footer form "Xác minh email của bạn" mới của Microsoft).
        private static readonly Regex OtherWaysRegex =
            new(@"cách khác để đăng nhập|cach khac de dang nhap|other ways to sign in", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Nút "Có"/"Yes" ở màn KMSI mới (Fluent) — nút submit generic CHỈ được click khi text khớp đúng đây.
        private static readonly Regex KmsiYesRegex =
            new(@"^\s*(yes|có|co)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ShopeeSenderRegex =
            new("shopee", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Tab "Khác"/"Ưu tiên" của hộp thư Outlook — UI đổi theo NGÔN NGỮ tài khoản (vi/en/es/pt/fr...). Thêm
        // đa ngôn ngữ; các từ thêm đều KHÔNG dấu (Otros/Prioritarios...) nên khớp chắc, không dính lỗi NFC/NFD.
        private static readonly Regex OtherPivotRegex =
            new(@"^\s*(other|otros|outros|autres|khác|khac)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex FocusedPivotRegex =
            new(@"^\s*(focused|prioritarios|prioritaire|prioritaires|ưu tiên|uu tien)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Text CỦA LINK xác nhận trong mail "Cảnh báo bảo mật" của Shopee — link thường CHỈ bọc "TẠI ĐÂY" (không
        // phải cả câu "xác nhận tại đây") nên phải bắt riêng "tại đây". CỐ Ý BỎ "here"/"click here": chữ "here"
        // dính cả link trong mail TRẢ HÀNG của Shopee → click nhầm; mail đã được lọc đúng "Cảnh báo bảo mật" nên
        // chỉ cần khớp các cụm xác nhận tiếng Việt an toàn.
        private static readonly Regex ConfirmLinkRegex =
            new(@"xác nhận|xac nhan|verify|confirm|đúng là tôi|dung la toi|yes,?\s*it'?s me|tại đây|tại đấy|tai day|nhấn vào đây|bấm vào đây|nhan vao day|bam vao day", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SignInRegex =
            new(@"sign\s*in|đăng nhập|dang nhap", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Thông báo Shopee đã XÁC NHẬN đăng nhập thành công (trên tab mở ra sau khi bấm "TẠI ĐÂY") — chờ dấu
        // hiệu này rồi mới đóng tab, kẻo đóng sớm khi Shopee CHƯA kịp ghi nhận xác nhận.
        private static readonly Regex ConfirmSuccessRegex =
            new(@"thành công|thanh cong|đã xác nhận|da xac nhan|xác nhận đăng nhập|xac nhan dang nhap|verified|confirmed|success", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Trang mở ra sau khi bấm "TẠI ĐÂY" báo link đã HẾT HẠN/HẾT HIỆU LỰC (Shopee gửi nhiều mail "Cảnh báo
        // bảo mật" khi thử lại nhiều lần → link mail cũ hết hạn). Gặp trang này thì KHÔNG coi là xác nhận thành
        // công — phải quay lại chờ mail MỚI HƠN. Liệt kê cả dạng có dấu lẫn không dấu (khớp IgnoreCase).
        private static readonly Regex ConfirmExpiredRegex =
            new(@"hết hiệu lực|het hieu luc|hết hạn|het han|đã hết|da het|expired|no longer valid", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Nút "Gửi lại" trên trang xác minh Shopee (sellerPage) — bấm để Shopee GỬI LẠI mail xác thực khi chờ
        // mãi không thấy mail. Khớp text nút (InnerText "Gửi lại").
        private static readonly Regex ResendVerifyRegex =
            new(@"^\s*(gửi lại|gui lai|resend)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Kết quả click link xác nhận trong một mail: không có link / đã xác nhận / link hết hạn (cần chờ mail mới).
        private enum ConfirmOutcome { NoLink, Confirmed, Expired }

        /// <summary>Chuẩn hóa text để so khớp bền: bỏ dấu tiếng Việt (kể cả đ→d), gộp mọi cụm khoảng trắng về một
        /// dấu cách, trim, hạ chữ thường. Dùng cho lọc tiêu đề "Cảnh báo bảo mật" (so <c>Contains</c> không dấu).</summary>
        internal static string NormalizeForMatch(string? s)
        {
            if (string.IsNullOrWhiteSpace(s))
            {
                return string.Empty;
            }

            var collapsed = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            var decomposed = collapsed.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            foreach (var ch in decomposed)
            {
                // Bỏ dấu thanh/dấu phụ (combining marks); đ/Đ không tách được bằng FormD → thay thủ công bên dưới.
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                switch (ch)
                {
                    case 'đ': sb.Append('d'); break;
                    case 'Đ': sb.Append('D'); break;
                    default: sb.Append(ch); break;
                }
            }

            return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
        }

        /// <summary>True nếu text của một dòng mail (InnerText: người gửi + tiêu đề + preview) là mail
        /// <b>"Cảnh báo bảo mật Tài khoản Shopee"</b> — người gửi khớp "shopee" VÀ nội dung (chuẩn hóa không dấu)
        /// CHỨA "canh bao bao mat". Loại mail trả hàng/khuyến mãi/khác của Shopee.</summary>
        internal static bool IsSecurityWarningMailRow(string? rowText)
        {
            if (string.IsNullOrWhiteSpace(rowText) || !ShopeeSenderRegex.IsMatch(rowText))
            {
                return false;
            }

            return NormalizeForMatch(rowText).Contains("canh bao bao mat", StringComparison.Ordinal);
        }

        /// <summary>True nếu <paramref name="text"/> khớp <see cref="ConfirmLinkRegex"/> (text của link cần bấm,
        /// vd "TẠI ĐÂY"). Phơi ra để test — KHÔNG còn khớp "here"/"click here".</summary>
        internal static bool MatchesConfirmLink(string? text)
            => !string.IsNullOrEmpty(text) && ConfirmLinkRegex.IsMatch(text);

        /// <summary>True nếu <paramref name="text"/> khớp <see cref="ConfirmExpiredRegex"/> (trang báo link đã hết
        /// hạn/hết hiệu lực). Phơi ra để test.</summary>
        internal static bool MatchesConfirmExpired(string? text)
            => !string.IsNullOrEmpty(text) && ConfirmExpiredRegex.IsMatch(text);

        /// <summary>True nếu <paramref name="text"/> là nav "Tài khoản của tôi" trên Nền tảng tài khoản phụ: CHUẨN HÓA
        /// không dấu (<see cref="NormalizeForMatch"/> — trị cả NFC/NFD, chữ HOA) rồi khớp <see cref="MyAccountNavRegex"/>.
        /// KHÔNG khớp "Phân bổ chat" / "Tài khoản" đơn lẻ. Phơi ra để test.</summary>
        internal static bool MatchesMyAccountNav(string? text)
            => MyAccountNavRegex.IsMatch(NormalizeForMatch(text));

        /// <summary>True nếu <paramref name="text"/> là entry "Kênh Người bán"/"Seller Centre": CHUẨN HÓA không dấu
        /// (<see cref="NormalizeForMatch"/>) rồi khớp <see cref="SellerChannelRegex"/>. KHÔNG khớp "Kênh" đơn lẻ.
        /// Phơi ra để test.</summary>
        internal static bool MatchesSellerChannelEntry(string? text)
            => SellerChannelRegex.IsMatch(NormalizeForMatch(text));

        public async Task<ShopeePageState> DetectPageStateAsync(CancellationToken ct = default)
        {
            try
            {
                var page = _context.Pages.Count > 0 ? _context.Pages[0] : null;
                if (page is null)
                {
                    return ShopeePageState.Unknown;
                }

                // 1) URL trước: cookie phiên có thể CÒN mà vẫn bị bắt verify/captcha (chép logic từ
                //    ShopeeAccountChecker.WaitOutcomeAsync, điều chỉnh cho seller site).
                var url = (page.Url ?? string.Empty).ToLowerInvariant();
                if (url.Contains("captcha"))
                {
                    return ShopeePageState.Captcha;
                }
                if (url.Contains("/verify"))
                {
                    return ShopeePageState.Verify;
                }

                // 2) Form đăng nhập: ô user/pass HIỂN THỊ (kiểm getClientRects — KHÔNG offsetParent).
                if (await IsAnyVisibleByClientRectsAsync(page, LoginFormDetectSelectors, ct).ConfigureAwait(false))
                {
                    return ShopeePageState.LoginForm;
                }

                // 3) Không ở form login mà có alert xác minh (otp/mã xác/xác minh) → Verify (tín hiệu phụ).
                var alert = (await ReadAlertTextAsync(page).ConfigureAwait(false)).ToLowerInvariant();
                if (alert.Contains("otp") || alert.Contains("mã xác") || alert.Contains("ma xac")
                    || alert.Contains("xác minh") || alert.Contains("xac minh"))
                {
                    return ShopeePageState.Verify;
                }

                // 4) Cookie phiên đăng nhập → LoggedIn; còn lại Unknown.
                if (ShopeeLoginCookies.IsLoggedIn(await CaptureCookiesJsonAsync().ConfigureAwait(false)))
                {
                    return ShopeePageState.LoggedIn;
                }

                return ShopeePageState.Unknown;
            }
            catch
            {
                // Không bao giờ ném (kể cả hủy) — trả Unknown, caller đọc ct riêng để dừng.
                return ShopeePageState.Unknown;
            }
        }

        public async Task<bool> TryLoginSubaccountAsync(
            string user, string password, string? verifyEmail, string? verifyEmailPassword,
            Action<string>? log = null, CancellationToken ct = default)
        {
            void L(string m) => log?.Invoke(m);

            // URL của một trang có phải Seller Centre (banhang.shopee.vn) không.
            static bool UrlIsBanhang(string? u) =>
                !string.IsNullOrEmpty(u) && u.Contains("banhang.shopee.vn", StringComparison.OrdinalIgnoreCase);

            async Task<string> DiagAsync(IPage p)
            {
                try { return $"title=[{await p.TitleAsync().ConfigureAwait(false)}], url={p.Url}"; }
                catch { return $"url={p.Url}"; }
            }

            var page = _context.Pages.Count > 0 ? _context.Pages[0] : null;
            if (page is null)
            {
                return false;
            }

            // Cap NỘI BỘ 20': chờ NGƯỜI dùng gõ code là phần lâu nhất. Timeout nội bộ ≠ HỦY của người dùng —
            // phân biệt ở khối catch dưới.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(20));
            var sct = timeoutCts.Token;

            // Selector nhóm cho nav "Tài khoản của tôi" (tín hiệu đã đăng nhập) — dùng lại ở nhiều bước.
            var accountNavSelectors = new[] { "li", "a", "div", "span", "[role='menuitem']" };

            var rng = new Random();
            IPage? mailPage = null;
            try
            {
                var vp = page.ViewportSize;
                double mx = vp is not null ? vp.Width / 2.0 : 640;
                double my = vp is not null ? vp.Height / 2.0 : 360;

                // ── Bước 2: dò trạng thái đầu (SPA còn render) — poll tối đa ~15s. KHÔNG dùng
                //    ShopeeLoginCookies.IsLoggedIn (cookie SPC_* của shopee.vn KHÔNG nói gì về phiên subaccount).
                bool onLoginForm = false;
                bool loggedIn = false;
                var detectDeadline = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < detectDeadline)
                {
                    sct.ThrowIfCancellationRequested();

                    // "Đang ở form login" = ô mật khẩu subaccount HIỂN THỊ (getClientRects).
                    if (await IsAnyVisibleByClientRectsAsync(page, SubPassSelectors, sct).ConfigureAwait(false))
                    {
                        onLoginForm = true;
                        break;
                    }

                    // "Đã đăng nhập" = phần tử khớp nav "Tài khoản của tôi" HIỂN THỊ.
                    if (await FindVisibleByTextAsync(page, accountNavSelectors, MyAccountNavRegex, sct, 1000).ConfigureAwait(false) is not null)
                    {
                        loggedIn = true;
                        break;
                    }

                    await Task.Delay(500, sct).ConfigureAwait(false);
                }

                if (!onLoginForm && !loggedIn)
                {
                    L("Chưa rõ trạng thái trang subaccount sau 15s — thử tiếp nhánh 'đã đăng nhập'. " + await DiagAsync(page).ConfigureAwait(false));
                }

                // ── Bước 3: ở form login → tự điền tài khoản + mật khẩu rồi bấm "Đăng nhập".
                if (onLoginForm)
                {
                    if (string.IsNullOrEmpty(password))
                    {
                        L("Tài khoản chưa có mật khẩu — đăng nhập tay.");
                        return false;
                    }

                    L("Đang điền form đăng nhập Nền tảng tài khoản phụ...");
                    // Re-query handle TƯƠI ngay trước khi điền (Vue re-render — không giữ handle qua các bước chờ).
                    var userInput = await FindFirstVisibleByRectsAsync(page, SubUserSelectors, 8000, sct).ConfigureAwait(false);
                    var passInput = await FindFirstVisibleByRectsAsync(page, SubPassSelectors, 4000, sct).ConfigureAwait(false);
                    if (userInput is null || passInput is null)
                    {
                        L("Không thấy ô đăng nhập subaccount — đăng nhập tay. " + await DiagAsync(page).ConfigureAwait(false));
                        return false;
                    }

                    (mx, my) = await HumanFillAsync(page, userInput, user, mx, my, rng, sct).ConfigureAwait(false);
                    (mx, my) = await HumanFillAsync(page, passInput, password, mx, my, rng, sct).ConfigureAwait(false);

                    // Nút "Đăng nhập" là <button type="button"> chứa <span>Đăng nhập</span> — khớp text bằng SignInRegex.
                    var submit = await FindVisibleByTextAsync(page, SubSubmitSelectors, SignInRegex, sct, 5000).ConfigureAwait(false);
                    if (submit is null)
                    {
                        L("Không thấy nút 'Đăng nhập' subaccount — đăng nhập tay. " + await DiagAsync(page).ConfigureAwait(false));
                        return false;
                    }
                    (mx, my, _) = await TryHumanClickVisibleAsync(page, submit, mx, my, rng, sct).ConfigureAwait(false);
                    L("Đã bấm Đăng nhập — chờ Shopee đòi mã xác thực...");

                    // ── Bước 4: mở hộp thư cho NGƯỜI DÙNG tự lấy mã (KHÔNG tự verify, KHÔNG tự bấm gì trong mail).
                    if (!string.IsNullOrWhiteSpace(verifyEmail) && !string.IsNullOrWhiteSpace(verifyEmailPassword))
                    {
                        try
                        {
                            bool mailLoggedIn;
                            (mailPage, mailLoggedIn) = await OpenMailboxSignedInAsync(_context, verifyEmail!, verifyEmailPassword!, log, rng, sct).ConfigureAwait(false);
                            L(mailLoggedIn
                                ? "Đã mở hộp thư ở tab bên — lấy mã rồi nhập vào trang Shopee."
                                : "Chưa đăng nhập được hộp thư tự động — GIỮ tab mail mở để bạn tự đăng nhập, lấy mã rồi nhập vào trang Shopee.");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            L("Lỗi khi mở hộp thư: " + ex.Message + " — bạn tự lấy mã và nhập vào trang Shopee.");
                        }

                        // Đưa cửa sổ về trang Shopee cho người dùng gõ code (best-effort).
                        try { await page.BringToFrontAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }
                    }
                    else
                    {
                        L("Chưa cấu hình Email xác minh — bạn tự lấy mã và nhập vào trang Shopee.");
                    }
                }

                // ── Bước 5: chờ NGƯỜI DÙNG nhập code — poll mỗi 3s, tối đa 15'. Thoát khi nav "Tài khoản của tôi"
                //    HIỂN THỊ (đã về trang tài khoản). KHÔNG tự bấm gì trong mail, KHÔNG reload (kẻo xóa ô code).
                bool reached = loggedIn; // đã đăng nhập sẵn từ đầu (hồ sơ bền) → khỏi chờ
                if (!reached)
                {
                    L("Chờ đăng nhập xong (bạn nhập mã nếu Shopee yêu cầu)...");
                    var waitDeadline = DateTime.UtcNow.AddMinutes(15);
                    while (DateTime.UtcNow < waitDeadline)
                    {
                        sct.ThrowIfCancellationRequested();
                        if (await FindVisibleByTextAsync(page, accountNavSelectors, MyAccountNavRegex, sct, 1000).ConfigureAwait(false) is not null)
                        {
                            reached = true;
                            break;
                        }
                        await Task.Delay(3000, sct).ConfigureAwait(false);
                    }
                }

                if (!reached)
                {
                    L("Chờ 15' chưa thấy đăng nhập vào Nền tảng tài khoản phụ — GIỮ cửa sổ để bạn thao tác tay. " + await DiagAsync(page).ConfigureAwait(false));
                    return false;
                }

                // ── Bước 6: đóng tab mail (best-effort, chỉ tab mình mở) rồi click "Tài khoản của tôi".
                if (mailPage is not null)
                {
                    try { await mailPage.CloseAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }
                    mailPage = null;
                }

                L("Đã đăng nhập Nền tảng tài khoản phụ.");

                var myAccountNav = await FindVisibleByTextAsync(page, accountNavSelectors, MyAccountNavRegex, sct, 10000).ConfigureAwait(false);
                if (myAccountNav is null)
                {
                    L("Không thấy 'Tài khoản của tôi' — GIỮ cửa sổ để bạn thao tác tay. " + await DiagAsync(page).ConfigureAwait(false));
                    return false;
                }
                (mx, my, _) = await TryHumanClickVisibleAsync(page, myAccountNav, mx, my, rng, sct).ConfigureAwait(false);
                await Task.Delay(rng.Next(1500, 3001), sct).ConfigureAwait(false);

                // ── Bước 7: click "Kênh Người bán" → chờ Seller Centre (tab MỚI HOẶC cùng tab). Hứng tab mới bằng
                //    event _context.Page TRƯỚC khi click (không bỏ lỡ popup nhanh); song song vẫn quét _context.Pages.
                var sellerEntry = await FindVisibleByTextAsync(
                    page, new[] { "span.entry-text", ".entry", "span", "div", "[role='button']", "a" },
                    SellerChannelRegex, sct, 10000).ConfigureAwait(false);
                if (sellerEntry is null)
                {
                    L("Không thấy entry 'Kênh Người bán' — GIỮ cửa sổ để bạn thao tác tay. " + await DiagAsync(page).ConfigureAwait(false));
                    return false;
                }

                IPage? popped = null;
                void OnNewPage(object? _, IPage p) => popped ??= p;
                _context.Page += OnNewPage;

                IPage sellerPage = page;
                bool sellerInNewTab = false;
                try
                {
                    (mx, my, _) = await TryHumanClickVisibleAsync(page, sellerEntry, mx, my, rng, sct).ConfigureAwait(false);
                    L("Đã bấm 'Kênh Người bán' — chờ Seller Centre mở...");

                    var sellerDeadline = DateTime.UtcNow.AddSeconds(90);
                    while (DateTime.UtcNow < sellerDeadline)
                    {
                        sct.ThrowIfCancellationRequested();

                        // (a) chính page điều hướng sang banhang (cùng tab).
                        if (UrlIsBanhang(page.Url))
                        {
                            sellerPage = page;
                            sellerInNewTab = false;
                            break;
                        }

                        // (b) tab mới (bắt qua event hoặc quét Pages) đã có URL banhang.
                        var candidate = (popped is not null && UrlIsBanhang(popped.Url))
                            ? popped
                            : _context.Pages.FirstOrDefault(p => p != page && UrlIsBanhang(p.Url));
                        if (candidate is not null)
                        {
                            sellerPage = candidate;
                            sellerInNewTab = true;
                            break;
                        }

                        await Task.Delay(500, sct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _context.Page -= OnNewPage;
                }

                if (!UrlIsBanhang(sellerPage.Url))
                {
                    var tabs = new List<string>();
                    foreach (var p in _context.Pages)
                    {
                        tabs.Add(await DiagAsync(p).ConfigureAwait(false));
                    }
                    L("Bấm 'Kênh Người bán' xong chờ 90s chưa thấy Seller Centre — GIỮ cửa sổ để bạn thao tác tay. Các tab: " + string.Join(" ; ", tabs));
                    return false;
                }

                // Tab seller mới mở → chờ DOMContentLoaded best-effort (đừng để bước sau đọc trang trắng).
                if (sellerInNewTab)
                {
                    try
                    {
                        await sellerPage.WaitForLoadStateAsync(LoadState.DOMContentLoaded,
                            new PageWaitForLoadStateOptions { Timeout = 15000 }).ConfigureAwait(false);
                    }
                    catch { /* bỏ qua — trang vẫn dùng được, bước sau tự poll */ }
                }

                // ── Bước 8: chuẩn hóa tab — nếu seller là TAB MỚI → đóng tab subaccount để seller thành Pages[0].
                if (sellerInNewTab)
                {
                    for (int attempt = 0; attempt < 3 && !page.IsClosed; attempt++)
                    {
                        try { await page.CloseAsync().ConfigureAwait(false); } catch { /* bỏ qua — thử lại */ }
                        if (page.IsClosed) break;
                        await Task.Delay(400, sct).ConfigureAwait(false);
                    }

                    if (!page.IsClosed)
                    {
                        L("Cảnh báo: tab subaccount chưa đóng được — theo dõi đơn có thể đọc nhầm tab (Pages[0] không phải Seller Centre).");
                    }
                    else if (_context.Pages.Count == 0 || _context.Pages[0] != sellerPage)
                    {
                        L("Cảnh báo: sau khi đóng subaccount, Seller Centre chưa ở Pages[0] — theo dõi đơn có thể đọc nhầm tab.");
                    }
                }

                L("Đã vào Kênh Người bán.");
                return true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timeout NỘI BỘ (cap 20') — KHÔNG phải người dùng Dừng → degrade êm, KHÔNG ném.
                L("Đăng nhập Nền tảng tài khoản phụ quá thời gian — GIỮ cửa sổ để bạn thao tác tay.");
                return false;
            }
            catch (OperationCanceledException)
            {
                throw; // người dùng Dừng / thoát app → để caller xử như HỦY.
            }
            catch (Exception ex)
            {
                L("Lỗi khi đăng nhập Nền tảng tài khoản phụ: " + ex.Message + " — GIỮ cửa sổ để bạn thao tác tay.");
                return false;
            }
            // KHÔNG đóng tab seller/subaccount ở finally — việc đóng tab subaccount làm CÓ CHỦ ĐÍCH ở Bước 8; tab mail
            // đóng ở Bước 6 (đường thành công) hoặc GIỮ mở ở đường lỗi cho người dùng tự lấy mã.
        }

        public async Task<bool> TryVerifyByEmailAsync(
            string verifyEmail, string verifyEmailPassword, bool autoConfirm, Action<string>? log = null, CancellationToken ct = default)
        {
            void L(string m) => log?.Invoke(m);

            if (string.IsNullOrWhiteSpace(verifyEmail) || string.IsNullOrWhiteSpace(verifyEmailPassword))
            {
                L("Chưa cấu hình Email xác minh cho tài khoản — bỏ qua verify tự động (verify tay).");
                return false;
            }

            var page = _context.Pages.Count > 0 ? _context.Pages[0] : null;
            if (page is null)
            {
                return false;
            }

            // Cap tổng ~8 phút (linh hoạt): mail xác thực Shopee thường ĐẾN MUỘN sau loạt mail cảnh báo → cần
            // chờ đủ lâu. Timeout NỘI BỘ khác HỦY của người dùng — phân biệt ở khối catch dưới.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(8));
            var vct = timeoutCts.Token;

            var rng = new Random();
            IPage? mailPage = null;
            var keepMailOpenForManual = false; // true = đã đăng nhập email + DỪNG cho user test tay → finally KHÔNG đóng tab Outlook
            try
            {
                // BƯỚC 1: trên trang verify Shopee, click lựa chọn "xác minh qua email".
                var emailOption = await FindVisibleByTextAsync(
                    page, new[] { "button", "a", "[role='button']", "label", "li", "div[class*='item']", "div[class*='option']" },
                    VerifyEmailOptionRegex, vct, 8000).ConfigureAwait(false);
                if (emailOption is null)
                {
                    // Log DOM đoạn quyết định để lần sau tinh chỉnh nhanh (title/url).
                    string diag;
                    try { diag = $"title=[{await page.TitleAsync().ConfigureAwait(false)}], url={page.Url}"; }
                    catch { diag = $"url={page.Url}"; }
                    L("Không tìm thấy lựa chọn 'xác minh qua email' trên trang verify — bỏ qua. " + diag);
                    return false;
                }

                L("Chọn phương thức xác minh qua email...");
                var vp = page.ViewportSize;
                double mx = vp is not null ? vp.Width / 2.0 : 640;
                double my = vp is not null ? vp.Height / 2.0 : 360;
                (mx, my, _) = await TryHumanClickVisibleAsync(page, emailOption, mx, my, rng, vct).ConfigureAwait(false);

                // Chờ trang đổi (thường sang màn "đã gửi link xác minh, kiểm tra email").
                await Task.Delay(rng.Next(2000, 5000), vct).ConfigureAwait(false);

                // BƯỚC 2: mở tab mới đăng nhập hộp thư Hotmail/Outlook rồi vào hộp thư (helper dùng chung với luồng
                //    subaccount). Login lỗi → bỏ qua verify như cũ (finally đóng tab mail vì keepMailOpenForManual=false).
                bool mailLoggedIn;
                (mailPage, mailLoggedIn) = await OpenMailboxSignedInAsync(_context, verifyEmail, verifyEmailPassword, log, rng, vct).ConfigureAwait(false);
                if (!mailLoggedIn)
                {
                    L("Không đăng nhập được hộp thư Hotmail/Outlook — bỏ qua verify.");
                    return false;
                }

                // Cờ "Tự động xác nhận" (checkbox ribbon → autoConfirm): TẮT ⇒ đăng nhập email XONG thì DỪNG, GIỮ
                // hộp thư Outlook mở để user TỰ bấm link "TẠI ĐÂY". BẬT ⇒ chạy tiếp đoạn tự-xác-minh bên dưới
                // (tìm mail → click "TẠI ĐÂY" → chờ seller đăng nhập).
                if (!autoConfirm)
                {
                    keepMailOpenForManual = true; // GIỮ tab Outlook cho user (finally không đóng)
                    L("Đã đăng nhập email thành công — DỪNG ('Tự động xác nhận' đang TẮT). Giữ hộp thư mở để bạn tự bấm link 'TẠI ĐÂY' duyệt.");
                    return false;
                }

                // BƯỚC 3+4: tìm mail Shopee mới nhất + mở + click link xác nhận. (mailPage chắc chắn non-null vì
                //           mailLoggedIn=true ⇒ OpenMailboxSignedInAsync đã tạo tab qua NewPageAsync.)
                if (!await OpenShopeeMailAndConfirmAsync(mailPage!, page, log, rng, vct).ConfigureAwait(false))
                {
                    L("Không tìm/không mở được mail xác minh Shopee — bỏ qua.");
                    return false;
                }

                // BƯỚC 5: quay lại tab seller, reload, chờ LoggedIn tối đa 90s.
                L("Đã click xác nhận trong mail — quay lại trang bán hàng, chờ đăng nhập...");
                try
                {
                    await page.ReloadAsync(new PageReloadOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = 30000
                    }).ConfigureAwait(false);
                }
                catch { /* nuốt lỗi reload — vẫn poll trạng thái */ }

                var deadline = DateTime.UtcNow.AddSeconds(90);
                while (DateTime.UtcNow < deadline)
                {
                    vct.ThrowIfCancellationRequested();
                    if (await DetectPageStateAsync(vct).ConfigureAwait(false) == ShopeePageState.LoggedIn)
                    {
                        L("Xác minh qua email xong — đã đăng nhập.");
                        return true;
                    }
                    await Task.Delay(3000, vct).ConfigureAwait(false);
                }

                L("Chờ 90s sau xác nhận mà chưa thấy đăng nhập — bỏ qua (kiểm tra tay).");
                return false;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timeout nội bộ (cap 4') — KHÔNG phải người dùng Dừng → degrade êm, KHÔNG ném (kẻo phá phiên).
                L("Xác minh qua email quá 8 phút — bỏ qua (kiểm tra tay).");
                return false;
            }
            catch (OperationCanceledException)
            {
                throw; // người dùng Dừng / thoát app → để caller xử như HỦY.
            }
            catch (Exception ex)
            {
                L("Lỗi khi xác minh qua email: " + ex.Message);
                return false;
            }
            finally
            {
                // Đóng MỌI tab Microsoft/Outlook đã mở trong lượt xác minh (mailPage + tab redirect OAuth) trừ tab
                // bán hàng Shopee. NGOẠI TRỪ khi đã đăng nhập email + DỪNG cho user test tay
                // (keepMailOpenForManual) → GIỮ tab Outlook mở để user tự thao tác. Best-effort, không để tab treo.
                try
                {
                    var toClose = _browser.Contexts.SelectMany(c => c.Pages)
                        .Where(p => !keepMailOpenForManual && p != page && (p == mailPage ||
                            (!string.IsNullOrEmpty(p.Url) && (
                                p.Url.Contains("outlook", StringComparison.OrdinalIgnoreCase)
                                || p.Url.Contains("live.com", StringComparison.OrdinalIgnoreCase)
                                || p.Url.Contains("microsoftonline", StringComparison.OrdinalIgnoreCase)
                                || p.Url.Contains("office.com", StringComparison.OrdinalIgnoreCase)
                                || p.Url.Contains("m365", StringComparison.OrdinalIgnoreCase)
                                || p.Url.Contains("microsoft.com", StringComparison.OrdinalIgnoreCase)))))
                        .ToList();
                    foreach (var p in toClose)
                    {
                        try { await p.CloseAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }
                    }
                }
                catch { /* context ngắt — bỏ qua */ }
            }
        }

        /// <summary>
        /// Mở TAB MỚI rồi ĐĂNG NHẬP hộp thư Hotmail/Outlook: <c>NewPage</c> → Goto trang đăng nhập Microsoft (nuốt lỗi
        /// điều hướng) → <see cref="LoginHotmailAsync"/>; đăng nhập được thì Goto vào hộp thư Outlook (nuốt lỗi). Trả về
        /// tab mail ĐÃ mở (kể cả khi login thất bại — caller quyết đóng hay giữ) và cờ <c>LoggedIn</c>. Best-effort —
        /// KHÔNG ném (trừ hủy). KHÔNG log giá trị mật khẩu. Dùng chung cho luồng verify (tự bấm link) và luồng
        /// subaccount (chỉ mở cho người dùng tự lấy mã).
        /// </summary>
        internal static async Task<(IPage? MailPage, bool LoggedIn)> OpenMailboxSignedInAsync(
            IBrowserContext context, string email, string password, Action<string>? log, Random rng, CancellationToken ct)
        {
            void L(string m) => log?.Invoke(m);

            var mailPage = await context.NewPageAsync().ConfigureAwait(false);
            L("Mở trang đăng nhập Microsoft để lấy mail...");
            try
            {
                await mailPage.GotoAsync("https://login.microsoftonline.com/", new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 60000
                }).ConfigureAwait(false);
            }
            catch { /* nuốt lỗi điều hướng — các bước dưới poll selector tự lo */ }

            if (!await LoginHotmailAsync(mailPage, email, password, log, rng, ct).ConfigureAwait(false))
            {
                return (mailPage, false);
            }

            // Đăng nhập ở trang login xong → điều hướng vào HỘP THƯ Outlook để đọc mail (login.microsoftonline.com
            // hạ cánh ở portal, không phải hộp thư). Nếu session đã có sẵn thì vào thẳng.
            L("Vào hộp thư Outlook...");
            try
            {
                await mailPage.GotoAsync("https://outlook.live.com/mail/0/", new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 60000
                }).ConfigureAwait(false);
            }
            catch { /* nuốt lỗi điều hướng — bước dưới poll selector tự lo */ }

            return (mailPage, true);
        }

        /// <summary>
        /// Đăng nhập hộp thư Hotmail/Outlook trên <paramref name="mailPage"/>: username → (nếu hiện) "Use your
        /// password"/"Sử dụng mật khẩu" → password → "Stay signed in?" Yes. MỖI bước "chờ có selector thì làm,
        /// timeout ngắn thì bỏ qua sang bước sau" (đã đăng nhập sẵn từ profile → mọi bước tự skip). KHÔNG log
        /// giá trị mật khẩu. Trả <c>false</c> khi phát hiện lỗi đăng nhập (sai user/pass qua error box).
        /// <para>Bước 2 xử lý CẢ form mới "Xác minh email của bạn" (Fluent UI, không còn link "Sử dụng mật khẩu"):
        /// khi không thấy ô mật khẩu lẫn link "Sử dụng mật khẩu", bấm "Các cách khác để đăng nhập" rồi chọn tile
        /// "Mật khẩu" để hiện ô nhập pass. Không thấy thì thất bại mềm (log URL, KHÔNG ném) cho verify tay.</para>
        /// </summary>
        private static async Task<bool> LoginHotmailAsync(
            IPage mailPage, string email, string password, Action<string>? log, Random rng, CancellationToken ct)
        {
            void L(string m) => log?.Invoke(m);
            var vp = mailPage.ViewportSize;
            double mx = vp is not null ? vp.Width / 2.0 : 640;
            double my = vp is not null ? vp.Height / 2.0 : 360;

            // 0) Có thể mở ra trang landing (chưa vào form nhập email) → bấm "Đăng nhập"/"Sign in" trước.
            //    Thử tìm ô email nhanh (6s); không thấy mà có nút Đăng nhập thì bấm rồi tìm lại.
            var userField = await FindFirstVisibleByRectsAsync(mailPage, MsUserSelectors, 6000, ct).ConfigureAwait(false);
            if (userField is null)
            {
                var signIn = await FindVisibleByTextAsync(mailPage, MsSignInSelectors, SignInRegex, ct, 4000).ConfigureAwait(false);
                if (signIn is not null)
                {
                    L("Chưa vào form đăng nhập — bấm 'Đăng nhập'...");
                    (mx, my, _) = await TryHumanClickVisibleAsync(mailPage, signIn, mx, my, rng, ct).ConfigureAwait(false);
                    await Task.Delay(rng.Next(1500, 3500), ct).ConfigureAwait(false);
                }
                userField = await FindFirstVisibleByRectsAsync(mailPage, MsUserSelectors, 15000, ct).ConfigureAwait(false);
            }

            // 1) Username (đã tìm ở bước 0; điền nếu thấy).
            if (userField is not null)
            {
                L("Nhập email đăng nhập hộp thư...");
                (mx, my) = await HumanFillAsync(mailPage, userField, email, mx, my, rng, ct).ConfigureAwait(false);
                var next = await FindFirstVisibleByRectsAsync(mailPage, MsSubmitSelectors, 3000, ct).ConfigureAwait(false);
                if (next is not null)
                {
                    (mx, my) = await HumanMoveAndClickAsync(mailPage, next, mx, my, rng, ct).ConfigureAwait(false);
                }
                await Task.Delay(rng.Next(1500, 3000), ct).ConfigureAwait(false);

                if (await IsSelectorVisibleAsync(mailPage, "#usernameError").ConfigureAwait(false))
                {
                    L("Email hộp thư không hợp lệ (Microsoft báo lỗi tài khoản).");
                    return false;
                }
            }

            // 2) Đưa về Ô MẬT KHẨU. Microsoft redirect nhiều bước (login.microsoftonline → login.live oauth) +
            //    form Fluent "Xác minh email" render CHẬM/MUỘN hơn cửa sổ tìm → nếu tìm 1 lần rồi thôi hay bị trượt.
            //    POLL tới ~45s, mỗi vòng: (a) thấy ô mật khẩu → xong; (b) thấy "Dùng mật khẩu"/"Nhập mật khẩu"
            //    (tile trên màn 'các cách khác') → click; (c) thấy "Các cách khác để đăng nhập" (form passwordless)
            //    → click (vòng sau sẽ thấy tile "Nhập mật khẩu"). Chịu được redirect/render trễ + đi qua nhiều bước.
            IElementHandle? passField = null;
            var passDeadline = DateTime.UtcNow.AddSeconds(45);
            var clickedOtherWays = false;
            while (DateTime.UtcNow < passDeadline)
            {
                ct.ThrowIfCancellationRequested();

                passField = await FindFirstVisibleByRectsAsync(mailPage, MsPasswordSelectors, 1500, ct).ConfigureAwait(false);
                if (passField is not null)
                {
                    break;
                }

                // "Sử dụng mật khẩu" (màn chọn cách) HOẶC tile "Nhập mật khẩu" (màn 'các cách khác') — khớp KHÔNG
                // dấu để tránh lỗi NFC/NFD (text MS dạng tổ hợp dấu).
                var usePwd = await FindByNormalizedTextInFramesAsync(mailPage, MsUsePasswordSelectors, new[] { "mat khau", "password", "contrasena" }, ct, 1200).ConfigureAwait(false);
                if (usePwd is not null)
                {
                    L("Chọn 'Dùng mật khẩu' / 'Nhập mật khẩu'...");
                    (mx, my, _) = await TryHumanClickVisibleAsync(mailPage, usePwd, mx, my, rng, ct).ConfigureAwait(false);
                    await Task.Delay(rng.Next(1200, 2200), ct).ConfigureAwait(false);
                    continue;
                }

                // Form mới "Xác minh email của bạn" (Fluent, passwordless): "Các cách khác để đăng nhập" → (vòng sau
                // thấy tile "Nhập mật khẩu"). Quét mọi frame + khớp KHÔNG dấu (tránh lỗi NFC/NFD). Click 1 lần rồi
                // để vòng sau lo tile mật khẩu.
                var otherWays = await FindByNormalizedTextInFramesAsync(mailPage, MsOtherWaysSelectors, new[] { "cach khac de dang nhap", "other ways to sign in", "otras formas de iniciar sesion" }, ct, 1200).ConfigureAwait(false);
                if (otherWays is not null)
                {
                    L("Form 'Xác minh email' — bấm 'Các cách khác để đăng nhập'...");
                    (mx, my, _) = await TryHumanClickVisibleAsync(mailPage, otherWays, mx, my, rng, ct).ConfigureAwait(false);
                    clickedOtherWays = true;
                    await Task.Delay(rng.Next(1200, 2200), ct).ConfigureAwait(false);
                    continue;
                }

                // Chưa thấy gì (đang redirect / form chưa render) → chờ rồi thử lại.
                await Task.Delay(rng.Next(1200, 2000), ct).ConfigureAwait(false);
            }

            if (passField is null)
            {
                L($"Không đưa được về ô mật khẩu sau 45s ({(clickedOtherWays ? "đã bấm 'Các cách khác' nhưng không thấy tile Mật khẩu" : "không thấy 'Các cách khác'/ô mật khẩu")}; URL: {mailPage.Url}) — bỏ qua, verify tay.");
            }

            // 3) Password (KHÔNG log giá trị).
            if (passField is not null)
            {
                L("Nhập mật khẩu hộp thư...");
                (mx, my) = await HumanFillAsync(mailPage, passField, password, mx, my, rng, ct).ConfigureAwait(false);
                var signIn = await FindFirstVisibleByRectsAsync(mailPage, MsSubmitSelectors, 3000, ct).ConfigureAwait(false);
                if (signIn is not null)
                {
                    (mx, my) = await HumanMoveAndClickAsync(mailPage, signIn, mx, my, rng, ct).ConfigureAwait(false);
                }
                await Task.Delay(rng.Next(2000, 4000), ct).ConfigureAwait(false);

                if (await IsSelectorVisibleAsync(mailPage, "#passwordError").ConfigureAwait(false))
                {
                    L("Sai mật khẩu hộp thư (Microsoft báo lỗi).");
                    return false;
                }
            }

            // 4) "Duy trì đăng nhập?" (KMSI) → bấm "Có" (giữ đăng nhập trong profile). Form Fluent MỚI: nút "Có"
            //    KHÔNG có #acceptButton/#idSIButton9 mà là [data-testid='primaryButton'] — nhưng NHIỀU form khác
            //    cũng có primaryButton (vd "Gửi mã"/"Đăng nhập") nên CHỈ bấm nó khi CHẮC đang ở form KMSI, nhận
            //    diện qua testid ỔN ĐỊNH (kmsiVideo/kmsiImage — không phụ thuộc ngôn ngữ). Bản Outlook cũ:
            //    #acceptButton/#idSIButton9. Poll ~8s vì KMSI render sau submit password (có thể trễ).
            await Task.Delay(rng.Next(1000, 2500), ct).ConfigureAwait(false);
            var kmsiDeadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < kmsiDeadline)
            {
                ct.ThrowIfCancellationRequested();
                var onKmsi = await IsAnyVisibleByClientRectsAsync(
                    mailPage, new[] { "[data-testid='kmsiVideo']", "[data-testid='kmsiImage']" }, ct).ConfigureAwait(false);
                var kmsiSelectors = onKmsi
                    ? new[] { "[data-testid='primaryButton']", "#acceptButton", "#idSIButton9" }
                    : MsKmsiYesSelectors;
                var kmsi = await FindFirstVisibleByRectsAsync(mailPage, kmsiSelectors, 1000, ct).ConfigureAwait(false);
                if (kmsi is not null)
                {
                    L("Bấm 'Có' để giữ đăng nhập hộp thư...");
                    (mx, my) = await HumanMoveAndClickAsync(mailPage, kmsi, mx, my, rng, ct).ConfigureAwait(false);
                    await Task.Delay(rng.Next(1500, 3000), ct).ConfigureAwait(false);
                    break;
                }
                await Task.Delay(rng.Next(500, 900), ct).ConfigureAwait(false);
            }

            return true;
        }

        /// <summary>
        /// Trong hộp thư Outlook: ưu tiên tab "Ưu tiên"/"Focused" (không có mail Shopee thì thử "Khác"/"Other"),
        /// DUYỆT các mail <b>"Cảnh báo bảo mật"</b> của Shopee theo thứ tự MỚI NHẤT trước — mở lần lượt, mail nào
        /// có link xác nhận ("TẠI ĐÂY") thì click. Shopee gửi nhiều mail cảnh báo bảo mật khi thử lại nhiều lần;
        /// nếu link mở ra báo HẾT HẠN thì bỏ, tải lại hộp thư + chờ để tìm mail mới hơn. Lặp reload + chờ tới hết
        /// deadline (~6'). Trả <c>true</c> khi đã click được link (đã xác nhận).
        /// </summary>
        private async Task<bool> OpenShopeeMailAndConfirmAsync(
            IPage mailPage, IPage sellerPage, Action<string>? log, Random rng, CancellationToken ct)
        {
            void L(string m) => log?.Invoke(m);
            const int MaxMailsPerRound = 8; // mỗi vòng duyệt tối đa 8 mail Shopee đầu (tìm cái có link xác nhận)
            var deadline = DateTime.UtcNow.AddMinutes(6); // chờ mail xác thực tới (đến sau loạt mail cảnh báo)

            // Chờ danh sách mail render lần đầu.
            await Task.Delay(rng.Next(2000, 4000), ct).ConfigureAwait(false);

            var round = 0;
            var noMailStreak = 0; // số vòng LIÊN TIẾP không có mail MỚI để thử — đủ 3 thì bấm "Gửi lại" trên trang Shopee
            var triedKeys = new HashSet<string>(StringComparer.Ordinal); // text dòng mail đã thử KHÔNG thành (hết hạn / không có link "TẠI ĐÂY") → không mở lại; XÓA khi bấm 'Gửi lại'
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                round++;

                // Sau đăng nhập, Microsoft đôi khi điều hướng KHỎI Outlook sang trang home M365
                // (m365.cloud.microsoft) → nếu quét mail ở đó sẽ không bao giờ thấy. Lạc khỏi outlook → quay lại
                // hộp thư trước khi quét.
                var mailUrl = mailPage.Url ?? string.Empty;
                if (!mailUrl.Contains("outlook", StringComparison.OrdinalIgnoreCase))
                {
                    L("Không ở Outlook (m365?) — điều hướng lại hộp thư...");
                    try
                    {
                        await mailPage.GotoAsync("https://outlook.live.com/mail/0/", new PageGotoOptions
                        {
                            WaitUntil = WaitUntilState.DOMContentLoaded,
                            Timeout = 60000
                        }).ConfigureAwait(false);
                    }
                    catch { /* nuốt lỗi điều hướng — bước dưới poll selector tự lo */ }
                    await Task.Delay(rng.Next(1500, 3000), ct).ConfigureAwait(false);
                }

                // Ưu tiên tab "Ưu tiên"/"Focused"; không có mail Shopee ở đó → thử "Khác"/"Other".
                await TryClickPivotAsync(mailPage, "focused", FocusedPivotRegex, "Ưu tiên", log, rng, ct).ConfigureAwait(false);
                await Task.Delay(rng.Next(800, 1500), ct).ConfigureAwait(false);
                var rows = await FindAllShopeeMailRowsAsync(mailPage, MaxMailsPerRound, ct).ConfigureAwait(false);
                if (rows.Count == 0)
                {
                    await TryClickPivotAsync(mailPage, "other", OtherPivotRegex, "Khác", log, rng, ct).ConfigureAwait(false);
                    await Task.Delay(rng.Next(800, 1500), ct).ConfigureAwait(false);
                    rows = await FindAllShopeeMailRowsAsync(mailPage, MaxMailsPerRound, ct).ConfigureAwait(false);
                }

                var triedNewMail = false; // vòng này có mở được mail CHƯA-hết-hạn nào để thử không?
                if (rows.Count > 0)
                {
                    L($"Thấy {rows.Count} mail Shopee (mới nhất trước) — mở lần lượt tìm link xác nhận 'TẠI ĐÂY'...");
                    var vp = mailPage.ViewportSize;
                    double mx = vp is not null ? vp.Width / 2.0 : 640;
                    double my = vp is not null ? vp.Height / 2.0 : 360;

                    for (var i = 0; i < rows.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();

                        // Nhận dạng mail theo TEXT dòng (người gửi + tiêu đề + ngày). Mail đã thử mà link HẾT HẠN
                        // thì GHI NHỚ và KHÔNG mở lại — tránh đọc-đi-đọc-lại cùng 1 mail hết hạn vô tận.
                        string key;
                        try { key = ((await rows[i].InnerTextAsync().ConfigureAwait(false)) ?? string.Empty).Trim(); }
                        catch { continue; }
                        if (key.Length > 0 && triedKeys.Contains(key))
                        {
                            continue;
                        }

                        // Mở mail thứ i bằng click CÓ HIT-TEST: Outlook load quảng cáo async, danh sách hay xê dịch —
                        // nếu đúng lúc click mà quảng cáo chèn vào chỗ dòng mail thì elementFromPoint KHÔNG còn là dòng
                        // mail → KHÔNG click (Clicked=false) → bỏ qua. Dòng cũng có thể detached khi list vẽ lại.
                        bool clickedRow;
                        try { (mx, my, clickedRow) = await HumanMoveAndClickVerifiedAsync(mailPage, rows[i], mx, my, rng, ct).ConfigureAwait(false); }
                        catch { continue; }
                        if (!clickedRow)
                        {
                            L($"Mail Shopee #{i + 1}: danh sách đang xê dịch (quảng cáo?) — chưa click được, thử lại vòng sau.");
                            continue;
                        }
                        await Task.Delay(rng.Next(1200, 2500), ct).ConfigureAwait(false);

                        triedNewMail = true;
                        var outcome = await ClickConfirmLinkInMailAsync(mailPage, sellerPage, log, rng, ct).ConfigureAwait(false);
                        if (outcome == ConfirmOutcome.Confirmed)
                        {
                            return true;
                        }
                        if (outcome == ConfirmOutcome.Expired)
                        {
                            // Link HẾT HẠN → GHI NHỚ mail này (không mở lại), thử mail KẾ trong danh sách. Khi mọi
                            // mail đều đã thử-và-hết-hạn → vòng sau rơi vào nhánh 'không có mail mới' → bấm 'Gửi lại'.
                            if (key.Length > 0) triedKeys.Add(key);
                            L($"Mail Shopee #{i + 1}: link hết hạn → bỏ qua mail này (không mở lại), thử mail khác.");
                            continue;
                        }
                        // Mail KHÔNG có link "TẠI ĐÂY" (vd mail vận đơn/thông báo khác của Shopee) → cũng GHI NHỚ
                        // để KHÔNG mở lại mỗi vòng (kẻo cứ mở #1 → NoLink → coi là 'đã thử mail mới' → reset chuỗi
                        // → không bao giờ đủ 3 vòng để bấm 'Gửi lại').
                        if (key.Length > 0) triedKeys.Add(key);
                        L($"Mail Shopee #{i + 1} không có link xác nhận — bỏ qua, thử mail kế.");
                    }
                }

                if (triedNewMail)
                {
                    noMailStreak = 0; // vòng này có thử mail MỚI → reset chuỗi
                }
                else
                {
                    // KHÔNG có mail xác nhận MỚI để thử (hộp thư rỗng HOẶC mọi mail đều đã thử-và-hết-hạn) → đếm.
                    // Sau 3 vòng LIÊN TIẾP → QUAY LẠI trang xác minh Shopee bấm "Gửi lại" (sellerPage vẫn mở), chờ ~1'
                    // cho Shopee gửi mail MỚI (link tươi) rồi kiểm lại.
                    noMailStreak++;
                    L($"Vòng {round}: không có mail xác nhận MỚI (mail cũ đã hết hạn?) — tải lại, chờ mail mới...");
                    if (noMailStreak >= 3 && DateTime.UtcNow < deadline)
                    {
                        noMailStreak = 0;
                        if (await TryResendVerifyEmailAsync(sellerPage, log, rng, ct).ConfigureAwait(false))
                        {
                            L("Đã bấm 'Gửi lại' trên trang xác minh Shopee — chờ ~1' mail mới về rồi kiểm hộp thư lại...");
                            triedKeys.Clear(); // sắp có mail MỚI (link tươi) → quên danh sách đã-thử để quét lại từ đầu
                            await Task.Delay(60000, ct).ConfigureAwait(false);
                        }
                        try { await mailPage.BringToFrontAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }
                    }
                }

                // Reload hộp thư rồi thử vòng kế (chờ mail tới / mail mới hơn).
                try
                {
                    await mailPage.ReloadAsync(new PageReloadOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = 30000
                    }).ConfigureAwait(false);
                }
                catch { /* nuốt lỗi reload */ }
                await Task.Delay(rng.Next(10000, 15000), ct).ConfigureAwait(false);
            }

            L("Hết thời gian chờ mail xác nhận Shopee — bỏ qua (kiểm tra tay).");
            return false;
        }

        /// <summary>Quay lại trang xác minh Shopee (<paramref name="sellerPage"/>) và bấm nút "Gửi lại" để Shopee
        /// GỬI LẠI mail xác thực (khi chờ mãi không thấy mail). Đưa tab lên trước để nút hiển thị (getClientRects),
        /// tìm nút theo <see cref="ResendVerifyRegex"/> trong button/a/[role=button] rồi click kiểu người. Trả
        /// <c>true</c> nếu đã bấm được nút.</summary>
        private static async Task<bool> TryResendVerifyEmailAsync(IPage sellerPage, Action<string>? log, Random rng, CancellationToken ct)
        {
            void L(string m) => log?.Invoke(m);
            try { await sellerPage.BringToFrontAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }
            await Task.Delay(rng.Next(600, 1400), ct).ConfigureAwait(false);

            var btn = await FindVisibleByTextAsync(
                sellerPage, new[] { "button", "a", "[role='button']" }, ResendVerifyRegex, ct, 6000).ConfigureAwait(false);
            if (btn is null)
            {
                L("Không thấy nút 'Gửi lại' trên trang xác minh Shopee — bỏ qua lần gửi lại này.");
                return false;
            }

            var vp = sellerPage.ViewportSize;
            double mx = vp is not null ? vp.Width / 2.0 : 640;
            double my = vp is not null ? vp.Height / 2.0 : 360;
            var (_, _, clicked) = await TryHumanClickVisibleAsync(sellerPage, btn, mx, my, rng, ct).ConfigureAwait(false);
            return clicked;
        }

        /// <summary>
        /// Trong reading-pane của mail đang mở (thường nằm trong iframe), dò link/nút xác nhận (text vi/en
        /// khớp <see cref="ConfirmLinkRegex"/>) rồi click kiểu người. Link thường mở TAB MỚI (target _blank) →
        /// bắt tab mới bằng snapshot trước/sau (như pattern bắt tab phiếu), chờ tải rồi ĐÓNG tab đó. Trả:
        /// <see cref="ConfirmOutcome.NoLink"/> nếu mail không có link xác nhận; <see cref="ConfirmOutcome.Expired"/>
        /// nếu trang mở ra báo link đã hết hạn/hết hiệu lực (đã đóng tab, caller cần chờ mail MỚI HƠN);
        /// <see cref="ConfirmOutcome.Confirmed"/> nếu Shopee báo thành công HOẶC không rõ kết quả (giữ hành vi lạc
        /// quan cũ để không hồi quy ca xác nhận thật nhưng trang thiếu text thành công).
        /// </summary>
        private async Task<ConfirmOutcome> ClickConfirmLinkInMailAsync(
            IPage mailPage, IPage sellerPage, Action<string>? log, Random rng, CancellationToken ct)
        {
            void L(string m) => log?.Invoke(m);

            // Dò trong MỌI frame (thân mail HTML hay nằm trong iframe reading-pane).
            var confirmEl = await FindVisibleByTextInFramesAsync(
                mailPage, new[] { "a", "button", "[role='button']" }, ConfirmLinkRegex, ct, 6000).ConfigureAwait(false);
            if (confirmEl is null)
            {
                return ConfirmOutcome.NoLink;
            }

            L("Bấm link xác nhận trong mail...");
            var before = _browser.Contexts.SelectMany(c => c.Pages).ToList();

            // Cuộn link vào tầm nhìn TRƯỚC (link "TẠI ĐÂY" có thể nằm cuối mail, ngoài màn hình → click tọa độ
            // sẽ trượt), rồi ƯU TIÊN click ĐÚNG phần tử link bằng Playwright: nó tự hit-test theo đúng frame của
            // element (không lệch hệ tọa độ như elementFromPoint ở main frame) → bấm trúng đúng chữ "TẠI ĐÂY".
            try { await confirmEl.ScrollIntoViewIfNeededAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }

            bool clicked = false;
            try
            {
                await confirmEl.ClickAsync(new ElementHandleClickOptions { Timeout = 5000 }).ConfigureAwait(false);
                clicked = true;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* bị che / actionability timeout → lùi về click theo tọa độ ở dưới */ }

            if (!clicked)
            {
                // Fallback: di chuột kiểu người tới tâm bounding box của ĐÚNG link rồi click tọa độ.
                var vp = mailPage.ViewportSize;
                double mx = vp is not null ? vp.Width / 2.0 : 640;
                double my = vp is not null ? vp.Height / 2.0 : 360;
                await HumanMoveAndClickAsync(mailPage, confirmEl, mx, my, rng, ct).ConfigureAwait(false);
            }

            // Link thường mở TAB MỚI → bắt tab (poll ≤10s).
            IPage? confirmTab = null;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (confirmTab is null && DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    confirmTab = _browser.Contexts.SelectMany(c => c.Pages)
                        .FirstOrDefault(p => p != mailPage && p != sellerPage && !before.Contains(p));
                }
                catch { /* context ngắt — thử vòng sau */ }
                if (confirmTab is null)
                {
                    await Task.Delay(400, ct).ConfigureAwait(false);
                }
            }

            if (confirmTab is not null)
            {
                L("Đã mở tab xác nhận — CHỜ Shopee báo xác nhận thành công rồi mới đóng...");
                try
                {
                    await confirmTab.WaitForLoadStateAsync(LoadState.DOMContentLoaded,
                        new PageWaitForLoadStateOptions { Timeout = 15000 }).ConfigureAwait(false);
                }
                catch { /* vẫn poll text thành công ở dưới */ }

                // ĐỪNG đóng sớm: poll tới khi trang xác nhận hiện thông báo THÀNH CÔNG (tối đa 45s) — Shopee cần
                // vài giây để ghi nhận xác nhận; đóng trước lúc đó thì xác nhận KHÔNG ăn. Song song: nếu trang báo
                // link HẾT HẠN/HẾT HIỆU LỰC (mail cũ) → thoát sớm, coi là Expired để caller chờ mail mới hơn.
                var okDeadline = DateTime.UtcNow.AddSeconds(45);
                var confirmed = false;
                var expired = false;
                while (DateTime.UtcNow < okDeadline)
                {
                    ct.ThrowIfCancellationRequested();
                    string body;
                    try { body = await confirmTab.EvaluateAsync<string>("() => document.body ? (document.body.innerText || '') : ''").ConfigureAwait(false); }
                    catch { body = string.Empty; }
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        // Ưu tiên bắt "hết hạn" TRƯỚC: trang lỗi hết hạn không được coi nhầm là thành công.
                        if (ConfirmExpiredRegex.IsMatch(body))
                        {
                            expired = true;
                            break;
                        }
                        if (ConfirmSuccessRegex.IsMatch(body))
                        {
                            confirmed = true;
                            break;
                        }
                    }
                    await Task.Delay(1500, ct).ConfigureAwait(false);
                }

                L(expired
                    ? "Link xác nhận đã HẾT HẠN — đóng tab, sẽ chờ mail MỚI HƠN."
                    : confirmed
                        ? "Shopee đã xác nhận thành công — đóng tab xác nhận."
                        : "Chờ 45s chưa thấy thông báo xác nhận — vẫn đóng tab xác nhận (kiểm tra tay nếu cần).");
                try { await confirmTab.CloseAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }

                if (expired)
                {
                    return ConfirmOutcome.Expired;
                }
            }
            else
            {
                // Link mở CÙNG tab (hoặc AJAX) → chờ một nhịp rồi thôi.
                await Task.Delay(rng.Next(2000, 4000), ct).ConfigureAwait(false);
            }

            return ConfirmOutcome.Confirmed;
        }

        /// <summary>Click tab/pivot (Outlook "Khác"/"Other" hoặc "Ưu tiên"/"Focused") nếu tìm thấy — best-effort,
        /// không thấy thì bỏ qua (một số hộp thư không chia Focused/Other).</summary>
        private static async Task TryClickPivotAsync(
            IPage page, string pivotValue, Regex regex, string label, Action<string>? log, Random rng, CancellationToken ct)
        {
            // ƯU TIÊN chọn theo thuộc tính `value` (focused/other) của tab Outlook (Fluent fui-Tab) — KHÔNG phụ
            // thuộc NGÔN NGỮ UI (vi/en/es/fr...): <button role="tab" value="focused">Prioritarios</button>. Dự
            // phòng: khớp text đa ngôn ngữ (regex) cho bản Outlook cũ/khác không có thuộc tính value.
            var pivot = await FindFirstVisibleByRectsAsync(
                page, new[] { $"button[role='tab'][value='{pivotValue}']", $"[role='tab'][value='{pivotValue}']" }, 2500, ct).ConfigureAwait(false);
            if (pivot is null)
            {
                pivot = await FindVisibleByTextAsync(
                    page, new[] { "button", "[role='tab']", "[role='menuitemradio']", "div[role='heading']", "span" },
                    regex, ct, 2500).ConfigureAwait(false);
            }
            if (pivot is null)
            {
                return;
            }

            try
            {
                var vp = page.ViewportSize;
                double mx = vp is not null ? vp.Width / 2.0 : 640;
                double my = vp is not null ? vp.Height / 2.0 : 360;
                await HumanMoveAndClickAsync(page, pivot, mx, my, rng, ct).ConfigureAwait(false);
                log?.Invoke($"Đã mở mục '{label}' trong hộp thư.");
            }
            catch { /* best-effort — bỏ qua */ }
        }

        /// <summary>Danh sách các dòng mail <b>"Cảnh báo bảo mật" của Shopee</b> ĐANG HIỂN THỊ (người gửi khớp
        /// "shopee" VÀ tiêu đề chứa "cảnh báo bảo mật" — xem <see cref="IsSecurityWarningMailRow"/>) theo thứ tự
        /// DOM (trên cùng = MỚI NHẤT), tối đa <paramref name="maxRows"/>. Trả NHIỀU dòng để caller DUYỆT vì
        /// Shopee gửi nhiều mail cảnh báo bảo mật khi thử lại nhiều lần; mail mới nhất (đầu danh sách) được ưu
        /// tiên. Dùng selector đầu tiên cho ra kết quả (không trộn nhiều selector để tránh trùng dòng); khử trùng
        /// theo text dòng.</summary>
        private static async Task<List<IElementHandle>> FindAllShopeeMailRowsAsync(IPage page, int maxRows, CancellationToken ct)
        {
            foreach (var sel in new[] { "div[role='option']", "div[role='listitem']", "div[role='row']", "[data-convid]" })
            {
                IReadOnlyList<IElementHandle> els;
                try { els = await page.QuerySelectorAllAsync(sel).ConfigureAwait(false); }
                catch { continue; }

                var security = new List<IElementHandle>();  // mail "Cảnh báo bảo mật" — ƯU TIÊN
                var anyShopee = new List<IElementHandle>();  // mọi mail Shopee — DỰ PHÒNG khi Outlook không hiện tiêu đề
                var seenSec = new HashSet<string>();
                var seenAny = new HashSet<string>();
                foreach (var el in els)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if (!await IsElementVisibleByClientRectsAsync(el).ConfigureAwait(false))
                        {
                            continue;
                        }

                        var txt = await el.InnerTextAsync().ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(txt) || !ShopeeSenderRegex.IsMatch(txt))
                        {
                            continue; // không phải mail Shopee
                        }

                        var key = txt.Trim();
                        if (seenAny.Add(key))
                        {
                            anyShopee.Add(el);
                        }
                        // ƯU TIÊN mail "Cảnh báo bảo mật" NẾU đọc được tiêu đề trong dòng. Outlook nhiều khi rút
                        // gọn dòng không hiện tiêu đề → security rỗng → DỰ PHÒNG duyệt mọi mail Shopee (vẫn an
                        // toàn vì chỉ click "TẠI ĐÂY" — regex đã bỏ "here" nên không dính mail trả hàng).
                        if (IsSecurityWarningMailRow(txt) && seenSec.Add(key))
                        {
                            security.Add(el);
                            if (security.Count >= maxRows)
                            {
                                return security;
                            }
                        }
                    }
                    catch { /* detached / lỗi đọc — bỏ qua dòng này */ }
                }

                var chosen = security.Count > 0 ? security : anyShopee;
                if (chosen.Count > 0)
                {
                    // selector này đã cho danh sách mail Shopee — dừng, không trộn selector khác
                    return chosen.Count > maxRows ? chosen.GetRange(0, maxRows) : chosen;
                }
            }

            return new List<IElementHandle>();
        }

        // ===================== Helper dò phần tử theo hiển thị (getClientRects) + text =====================

        /// <summary>True nếu có ÍT NHẤT một phần tử khớp một trong <paramref name="selectors"/> đang HIỂN THỊ
        /// (kiểm bằng <c>getClientRects</c> có kích thước &gt; 0 — KHÔNG dùng offsetParent). Một lượt quét,
        /// không poll (caller tự lặp nếu cần).</summary>
        private static async Task<bool> IsAnyVisibleByClientRectsAsync(IPage page, string[] selectors, CancellationToken ct)
        {
            foreach (var sel in selectors)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var visible = await page.EvaluateAsync<bool>(
                        @"(sel) => { for (const el of document.querySelectorAll(sel)) { const rs = el.getClientRects();"
                        + " for (const r of rs) { if (r.width > 0 && r.height > 0) return true; } } return false; }",
                        sel).ConfigureAwait(false);
                    if (visible)
                    {
                        return true;
                    }
                }
                catch { /* selector không dùng được trên trang này — thử selector kế */ }
            }

            return false;
        }

        /// <summary>True nếu <paramref name="el"/> đang HIỂN THỊ (getClientRects có kích thước &gt; 0). Dùng cho
        /// element handle đơn (kể cả trong iframe — eval chạy trong document của frame đó).</summary>
        private static async Task<bool> IsElementVisibleByClientRectsAsync(IElementHandle el)
        {
            try
            {
                return await el.EvaluateAsync<bool>(
                    "(node) => { const rs = node.getClientRects(); for (const r of rs) { if (r.width > 0 && r.height > 0) return true; } return false; }")
                    .ConfigureAwait(false);
            }
            catch { return false; }
        }

        /// <summary>True nếu <paramref name="selector"/> có phần tử ĐANG HIỂN THỊ (dùng cho error box Microsoft).</summary>
        private static async Task<bool> IsSelectorVisibleAsync(IPage page, string selector)
        {
            try
            {
                var el = await page.QuerySelectorAsync(selector).ConfigureAwait(false);
                return el is not null && await IsElementVisibleByClientRectsAsync(el).ConfigureAwait(false);
            }
            catch { return false; }
        }

        /// <summary>Đọc text các <c>div[role='alert']</c> của trang (nối bằng " | "). Lỗi → chuỗi rỗng.</summary>
        private static async Task<string> ReadAlertTextAsync(IPage page)
        {
            try
            {
                return await page.EvaluateAsync<string>(
                    "() => Array.from(document.querySelectorAll(\"div[role='alert']\")).map(a => a.innerText || '').join(' | ')")
                    .ConfigureAwait(false);
            }
            catch { return string.Empty; }
        }

        /// <summary>Dò phần tử ĐẦU TIÊN đang HIỂN THỊ (getClientRects) khớp một trong <paramref name="selectors"/>,
        /// poll tới hết <paramref name="timeoutMs"/>. Giống <see cref="FindFirstVisibleAsync"/> nhưng kiểm hiển
        /// thị bằng getClientRects (không offsetParent) — dùng cho form Microsoft/Outlook.</summary>
        private static async Task<IElementHandle?> FindFirstVisibleByRectsAsync(
            IPage page, string[] selectors, int timeoutMs, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            do
            {
                ct.ThrowIfCancellationRequested();
                foreach (var sel in selectors)
                {
                    try
                    {
                        var el = await page.QuerySelectorAsync(sel).ConfigureAwait(false);
                        if (el is not null && await IsElementVisibleByClientRectsAsync(el).ConfigureAwait(false))
                        {
                            return el;
                        }
                    }
                    catch { /* selector không dùng được — thử selector kế */ }
                }
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            return null;
        }

        /// <summary>Dò phần tử ĐẦU TIÊN đang HIỂN THỊ khớp selector VÀ có innerText khớp <paramref name="textRegex"/>
        /// (vi/en), poll tới hết <paramref name="timeoutMs"/>. Duyệt theo thứ tự selector (ưu tiên phần tử
        /// clickable trước). Chỉ quét frame chính.</summary>
        private static async Task<IElementHandle?> FindVisibleByTextAsync(
            IPage page, string[] selectors, Regex textRegex, CancellationToken ct, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            do
            {
                ct.ThrowIfCancellationRequested();
                foreach (var sel in selectors)
                {
                    IReadOnlyList<IElementHandle> els;
                    try { els = await page.QuerySelectorAllAsync(sel).ConfigureAwait(false); }
                    catch { continue; }

                    foreach (var el in els)
                    {
                        try
                        {
                            if (!await IsElementVisibleByClientRectsAsync(el).ConfigureAwait(false))
                            {
                                continue;
                            }

                            var txt = await el.InnerTextAsync().ConfigureAwait(false);
                            if (!string.IsNullOrWhiteSpace(txt) && textRegex.IsMatch(txt))
                            {
                                return el;
                            }
                        }
                        catch { /* detached / lỗi đọc — bỏ qua phần tử này */ }
                    }
                }
                await Task.Delay(300, ct).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            return null;
        }

        /// <summary>Như <see cref="FindVisibleByTextInFramesAsync"/> nhưng so khớp KHÔNG PHÂN BIỆT DẤU: chuẩn hóa
        /// InnerText qua <see cref="NormalizeForMatch"/> (FormD + bỏ dấu + đ→d + lower) rồi kiểm CHỨA một trong
        /// <paramref name="normalizedNeedles"/> (phải ĐÃ ở dạng không dấu, chữ thường). TRỊ lỗi: text tiếng Việt
        /// trên trang MS ở dạng tổ hợp dấu (NFD) khác literal regex dựng sẵn (NFC) → Regex.IsMatch trượt dù mắt
        /// thấy giống. VD "Các cách khác để đăng nhập" NFD KHÔNG khớp regex "cách khác..." NFC.</summary>
        private static async Task<IElementHandle?> FindByNormalizedTextInFramesAsync(
            IPage page, string[] selectors, string[] normalizedNeedles, CancellationToken ct, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            do
            {
                ct.ThrowIfCancellationRequested();
                foreach (var frame in page.Frames)
                {
                    foreach (var sel in selectors)
                    {
                        IReadOnlyList<IElementHandle> els;
                        try { els = await frame.QuerySelectorAllAsync(sel).ConfigureAwait(false); }
                        catch { continue; }

                        foreach (var el in els)
                        {
                            try
                            {
                                if (!await IsElementVisibleByClientRectsAsync(el).ConfigureAwait(false))
                                {
                                    continue;
                                }

                                var txt = NormalizeForMatch(await el.InnerTextAsync().ConfigureAwait(false));
                                if (txt.Length > 0 && Array.Exists(normalizedNeedles, n => txt.Contains(n, StringComparison.Ordinal)))
                                {
                                    return el;
                                }
                            }
                            catch { /* detached — bỏ qua */ }
                        }
                    }
                }
                await Task.Delay(300, ct).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            return null;
        }

        /// <summary>Như <see cref="FindVisibleByTextAsync"/> nhưng quét MỌI frame của trang (thân mail HTML của
        /// Outlook thường nằm trong iframe reading-pane).</summary>
        private static async Task<IElementHandle?> FindVisibleByTextInFramesAsync(
            IPage page, string[] selectors, Regex textRegex, CancellationToken ct, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            do
            {
                ct.ThrowIfCancellationRequested();
                foreach (var frame in page.Frames)
                {
                    foreach (var sel in selectors)
                    {
                        IReadOnlyList<IElementHandle> els;
                        try { els = await frame.QuerySelectorAllAsync(sel).ConfigureAwait(false); }
                        catch { continue; }

                        foreach (var el in els)
                        {
                            try
                            {
                                if (!await IsElementVisibleByClientRectsAsync(el).ConfigureAwait(false))
                                {
                                    continue;
                                }

                                var txt = await el.InnerTextAsync().ConfigureAwait(false);
                                if (!string.IsNullOrWhiteSpace(txt) && textRegex.IsMatch(txt))
                                {
                                    return el;
                                }
                            }
                            catch { /* detached — bỏ qua */ }
                        }
                    }
                }
                await Task.Delay(300, ct).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            return null;
        }

        /// <summary>
        /// Dò phần tử đầu tiên <b>đang hiển thị</b> khớp một trong <paramref name="selectors"/> (thử lần
        /// lượt), poll tới khi hết <paramref name="timeoutMs"/>. Trả <c>null</c> nếu không thấy. Nuốt lỗi
        /// từng selector (selector có thể không hợp lệ trên trang hiện tại).
        /// </summary>
        private static async Task<IElementHandle?> FindFirstVisibleAsync(
            IPage page, string[] selectors, int timeoutMs, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            do
            {
                ct.ThrowIfCancellationRequested();
                foreach (var sel in selectors)
                {
                    try
                    {
                        var el = await page.QuerySelectorAsync(sel).ConfigureAwait(false);
                        if (el is not null && await el.IsVisibleAsync().ConfigureAwait(false))
                        {
                            return el;
                        }
                    }
                    catch
                    {
                        // Selector không dùng được trên trang này — thử selector kế.
                    }
                }
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            return null;
        }

        /// <summary>
        /// Điền một ô kiểu người: di chuột cong tới ô + click, rồi gõ <b>từng ký tự</b> với delay ngẫu
        /// nhiên (<see cref="HumanTyping.NextCharDelayMs"/>). Trả về vị trí chuột mới (tâm ô).
        /// </summary>
        private static async Task<(double X, double Y)> HumanFillAsync(
            IPage page, IElementHandle el, string text, double mx, double my, Random rng, CancellationToken ct)
        {
            (mx, my) = await HumanMoveAndClickAsync(page, el, mx, my, rng, ct).ConfigureAwait(false);

            // Ô có thể ĐÃ CÓ SẴN text (trình duyệt autofill / thông tin đã lưu sau khi bấm Save) → gõ đè sẽ NỐI
            // vào text cũ. Xóa SẠCH ô trước khi gõ lại: ưu tiên FillAsync("") (clear chuẩn của Playwright); lỗi
            // thì clear bằng phím (đã click nên focus đang ở ô → Ctrl+A chọn hết text TRONG ô rồi Delete).
            try
            {
                await el.FillAsync("").ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    await page.Keyboard.PressAsync("Control+A").ConfigureAwait(false);
                    await Task.Delay(rng.Next(40, 100), ct).ConfigureAwait(false);
                    await page.Keyboard.PressAsync("Delete").ConfigureAwait(false);
                }
                catch { /* bỏ qua — vẫn thử gõ ở dưới */ }
            }
            await Task.Delay(rng.Next(60, 160), ct).ConfigureAwait(false);

            foreach (var ch in text)
            {
                ct.ThrowIfCancellationRequested();
                // Gõ TỪNG ký tự (KHÔNG fill/dán) + delay kiểu người.
                await page.Keyboard.TypeAsync(ch.ToString()).ConfigureAwait(false);
                await Task.Delay(HumanTyping.NextCharDelayMs(rng), ct).ConfigureAwait(false);
            }

            return (mx, my);
        }

        /// <summary>
        /// Di chuột theo <b>đường cong</b> từ (<paramref name="mx"/>,<paramref name="my"/>) tới tâm phần tử
        /// (+jitter nhỏ), tự <c>Mouse.MoveAsync</c> <b>từng điểm</b> (KHÔNG dùng <c>steps</c> lớn để đi
        /// thẳng). <b>Chỉ đưa chuột tới đích — KHÔNG click.</b> Trả về (vị trí chuột cuối, có bounding box
        /// hay không): box null → kéo phần tử vào tầm nhìn, GIỮ nguyên vị trí chuột, <c>HasBox=false</c>.
        /// </summary>
        private static async Task<(double X, double Y, bool HasBox)> HumanMoveToAsync(
            IPage page, IElementHandle el, double mx, double my, Random rng, CancellationToken ct)
        {
            // Handle có thể đã DETACHED (Vue vẽ lại form sau khi map/modal re-render) → BoundingBoxAsync ném.
            // Bọc try: lỗi handle → coi như không có box (HasBox=false), KHÔNG để exception rò lên catch ngoài.
            ElementHandleBoundingBoxResult? box;
            try { box = await el.BoundingBoxAsync().ConfigureAwait(false); }
            catch { box = null; }

            double tx, ty;
            bool hasBox;
            if (box is not null)
            {
                // Tâm ô + jitter nhỏ (không luôn nhấn đúng chính giữa).
                tx = box.X + box.Width / 2.0 + (rng.NextDouble() - 0.5) * Math.Min(box.Width * 0.3, 20);
                ty = box.Y + box.Height / 2.0 + (rng.NextDouble() - 0.5) * Math.Min(box.Height * 0.3, 8);
                hasBox = true;
            }
            else
            {
                // Không lấy được bounding box → kéo phần tử vào tầm nhìn, giữ nguyên vị trí chuột.
                try { await el.ScrollIntoViewIfNeededAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }
                tx = mx;
                ty = my;
                hasBox = false;
            }

            // Số điểm theo khoảng cách (đường dài → nhiều điểm), giới hạn [12, 60] cho mượt.
            var dist = Math.Sqrt((tx - mx) * (tx - mx) + (ty - my) * (ty - my));
            var steps = Math.Clamp((int)(dist / 8) + 10, 12, 60);

            foreach (var (px, py) in HumanMouse.GeneratePath(mx, my, tx, ty, steps, rng))
            {
                ct.ThrowIfCancellationRequested();
                // Đi TỪNG điểm (steps mặc định = 1) để đường thật sự cong theo path đã sinh.
                await page.Mouse.MoveAsync((float)px, (float)py).ConfigureAwait(false);
                await Task.Delay(rng.Next(5, 26), ct).ConfigureAwait(false); // 5–25ms giữa các điểm
            }

            return (tx, ty, hasBox);
        }

        /// <summary>
        /// Di chuột theo <b>đường cong</b> tới tâm phần tử rồi click kiểu người (down + trễ + up). Trả về
        /// vị trí chuột cuối (điểm đích). <b>Click MÙ theo tọa độ — KHÔNG hit-test</b>: CHỈ dùng cho luồng
        /// đăng nhập (<c>TryHumanLoginAsync</c> — form login đơn giản, không có submenu cụp/flyout
        /// đè). Mọi thao tác NGHIỆP VỤ (menu/modal) dùng <see cref="HumanMoveAndClickVerifiedAsync"/>.
        /// </summary>
        private static async Task<(double X, double Y)> HumanMoveAndClickAsync(
            IPage page, IElementHandle el, double mx, double my, Random rng, CancellationToken ct)
        {
            (double tx, double ty, _) = await HumanMoveToAsync(page, el, mx, my, rng, ct).ConfigureAwait(false);

            // Click kiểu người: nhấn giữ một khoảng ngắn rồi nhả.
            await page.Mouse.DownAsync().ConfigureAwait(false);
            await Task.Delay(rng.Next(40, 121), ct).ConfigureAwait(false);
            await page.Mouse.UpAsync().ConfigureAwait(false);

            return (tx, ty);
        }

        /// <summary>True nếu tại điểm (x,y) của viewport, phần tử nhận sự kiện chính là el / con của el /
        /// tổ tiên của el (elementFromPoint trả node TRÊN CÙNG — bị phần tử khác đè thì trả phần tử đè).</summary>
        private static async Task<bool> IsPointOnElementAsync(IElementHandle el, double x, double y)
        {
            try
            {
                return await el.EvaluateAsync<bool>(
                    "(node, pt) => { const hit = document.elementFromPoint(pt.x, pt.y);" +
                    " return !!hit && (node === hit || node.contains(hit) || hit.contains(node)); }",
                    new { x, y }).ConfigureAwait(false);
            }
            catch { return false; }
        }

        /// <summary>
        /// Primitive click <b>kiểu người CÓ HIT-TEST</b> cho thao tác nghiệp vụ: đưa chuột theo đường cong
        /// tới phần tử (<see cref="HumanMoveToAsync"/>), rồi TRƯỚC KHI nhả click <b>kiểm tra
        /// <c>document.elementFromPoint</c></b> tại điểm click có đúng là phần tử đích (hoặc con/tổ tiên
        /// của nó) — chống <b>click nhầm link khác</b> khi submenu bị cụp hoặc flyout/popover đè lên toạ độ.
        /// Poll hit-test tối đa ~2s với chuột ĐỨNG YÊN tại đích (giống người dừng nhìn rồi mới bấm; popover
        /// hover của item khác tự tắt khi chuột rời item đó). Chỉ <c>Down/trễ/Up</c> khi hit-test PASS. Trả
        /// về (vị trí chuột cuối, đã click hay chưa) — <c>Clicked=false</c> khi không có bounding box hoặc
        /// hit-test fail suốt ~2s (KHÔNG bao giờ click mù vào tọa độ).
        /// </summary>
        private static async Task<(double X, double Y, bool Clicked)> HumanMoveAndClickVerifiedAsync(
            IPage page, IElementHandle el, double mx, double my, Random rng, CancellationToken ct)
        {
            (double tx, double ty, bool hasBox) =
                await HumanMoveToAsync(page, el, mx, my, rng, ct).ConfigureAwait(false);

            // Không có bounding box → thử kéo vào tầm nhìn + move lại MỘT lần; vẫn không có box → KHÔNG click.
            if (!hasBox)
            {
                try { await el.ScrollIntoViewIfNeededAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }
                (tx, ty, hasBox) = await HumanMoveToAsync(page, el, mx, my, rng, ct).ConfigureAwait(false);
                if (!hasBox)
                {
                    return (mx, my, false);
                }
            }

            // Poll hit-test tối đa ~2s: chuột ĐỨNG YÊN tại đích, dừng ngẫu nhiên rồi kiểm — giống người dừng
            // nhìn rồi mới bấm (popover hover của item khác tự tắt vì chuột không còn trên item đó).
            var deadline = DateTime.UtcNow.AddMilliseconds(2000);
            do
            {
                ct.ThrowIfCancellationRequested();
                if (await IsPointOnElementAsync(el, tx, ty).ConfigureAwait(false))
                {
                    // Hit-test PASS → click kiểu người: nhấn giữ một khoảng ngắn rồi nhả.
                    await page.Mouse.DownAsync().ConfigureAwait(false);
                    await Task.Delay(rng.Next(40, 121), ct).ConfigureAwait(false);
                    await page.Mouse.UpAsync().ConfigureAwait(false);
                    return (tx, ty, true);
                }

                await Task.Delay(rng.Next(150, 301), ct).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            // Poll fail suốt ~2s → điểm click đang thuộc phần tử khác (bị che/cụp) → KHÔNG Down/Up.
            return (tx, ty, false);
        }

        public async Task<string> CaptureCookiesJsonAsync()
        {
            // Không truyền URL = lấy tất cả cookie trong context.
            var raw = await _context.CookiesAsync().ConfigureAwait(false);

            var list = raw
                .Select(c => new StoredCookie(
                    c.Name,
                    c.Value,
                    c.Domain,
                    c.Path,
                    c.Expires,
                    c.HttpOnly,
                    c.Secure,
                    c.SameSite.ToString()))
                .ToList();

            return CookieJson.Serialize(list);
        }

        // ===================== Danh sách shop (/portal/shop) — mô hình 1 subaccount = nhiều shop =====================

        // Regex nhận entry nút mở shop ("Chi tiết"): chuẩn hóa không dấu rồi khớp. GIỮ nhiều biến thể (vi + en).
        private static readonly Regex ShopDetailRegex =
            new(@"chi tiet|detail", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // JS CHỈ-ĐỌC quét bảng shop: mỗi dòng tr[data-row-key] → {rowKey, name, login}. Bọc từng dòng trong try để
        // một dòng lạ KHÔNG phá cả bảng. Trả JSON.stringify(mảng). Tên đăng nhập = span trong ô td thứ 2 (fallback
        // text của td thứ 2). Selector dùng class-contains để bền khi Shopee thêm hậu tố hash vào tên class.
        private const string ScanShopListJs = @"() => {
    const norm = s => (s || '').replace(/\s+/g, ' ').trim();
    const rows = document.querySelectorAll(""tr[data-row-key]"");
    const out = [];
    for (const row of rows) {
        try {
            const rowKey = row.getAttribute('data-row-key') || '';
            const nameEl = row.querySelector(""span[class*='shop-name-text']"");
            const name = nameEl ? norm(nameEl.textContent) : '';
            let login = '';
            const tds = row.querySelectorAll('td');
            if (tds.length >= 2) {
                const span = tds[1].querySelector('span');
                login = norm(span ? span.textContent : tds[1].textContent);
            }
            out.push({ rowKey: rowKey, name: name, login: login });
        } catch (e) { /* dòng lạ — bỏ qua */ }
    }
    return JSON.stringify(out);
}";

        // Deserialize không phân biệt hoa/thường: khóa JSON rowKey/name/login khớp thuộc tính record.
        private static readonly JsonSerializerOptions ShopRowJsonOpts = new() { PropertyNameCaseInsensitive = true };

        private sealed record RawShopRow(string? RowKey, string? Name, string? Login);

        /// <summary>
        /// HÀM THUẦN (test được): chuyển JSON mảng <c>{rowKey,name,login}</c> (do <see cref="ScanShopListJs"/> đọc từ
        /// DOM) thành <see cref="ShopListItem"/>. Trim mọi trường; BỎ dòng không có <c>rowKey</c> (không định vị được
        /// để mở). Dòng thiếu login vẫn nhận (LoginName rỗng). JSON rỗng/hỏng → danh sách rỗng.
        /// </summary>
        internal static IReadOnlyList<ShopListItem> ParseShopListJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return Array.Empty<ShopListItem>();
            }

            List<RawShopRow>? raw;
            try { raw = JsonSerializer.Deserialize<List<RawShopRow>>(json, ShopRowJsonOpts); }
            catch { return Array.Empty<ShopListItem>(); }

            if (raw is null)
            {
                return Array.Empty<ShopListItem>();
            }

            var list = new List<ShopListItem>();
            foreach (var r in raw)
            {
                var id = (r.RowKey ?? string.Empty).Trim();
                if (id.Length == 0)
                {
                    continue; // không có mã shop → không định vị được dòng để mở → bỏ
                }
                list.Add(new ShopListItem(id, (r.Name ?? string.Empty).Trim(), (r.Login ?? string.Empty).Trim()));
            }
            return list;
        }


        /// <summary>
        /// Click <b>kiểu người CÓ HIT-TEST</b> nhưng chỉ khi phần tử đang hiển thị
        /// (<c>BoundingBoxAsync() != null</c>): scroll vào tầm nhìn trước, box vẫn null → KHÔNG click và trả
        /// <c>Clicked=false</c>. Có box → gọi <see cref="HumanMoveAndClickVerifiedAsync"/> (chỉ nhả chuột khi
        /// <c>elementFromPoint</c> tại điểm click đúng là phần tử đích — chống click nhầm link khác khi bị
        /// che/cụp); <c>Clicked</c> lấy từ kết quả verified (hit-test fail → false, KHÔNG click mù). Trả về
        /// vị trí chuột mới + đã click hay chưa.
        /// </summary>
        private static async Task<(double X, double Y, bool Clicked)> TryHumanClickVisibleAsync(
            IPage page, IElementHandle el, double mx, double my, Random rng, CancellationToken ct)
        {
            try { await el.ScrollIntoViewIfNeededAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }

            if (!await HasBoundingBoxAsync(el).ConfigureAwait(false))
            {
                try { await el.ScrollIntoViewIfNeededAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }
                if (!await HasBoundingBoxAsync(el).ConfigureAwait(false))
                {
                    return (mx, my, false);
                }
            }

            bool clicked;
            (mx, my, clicked) = await HumanMoveAndClickVerifiedAsync(page, el, mx, my, rng, ct).ConfigureAwait(false);
            return (mx, my, clicked);
        }

        /// <summary>
        /// Phần tử có <b>bounding box</b> không (đang hiển thị), <b>nuốt lỗi</b> handle DETACHED (Vue vẽ lại
        /// form sau khi map/modal re-render khiến <c>BoundingBoxAsync</c> ném) → <c>false</c> graceful, KHÔNG
        /// để exception rò lên catch ngoài cùng của <c>SetPickupAddressAsync</c> (lỗi handle biến
        /// thành "không click được", modal vẫn được Hủy).
        /// </summary>
        private static async Task<bool> HasBoundingBoxAsync(IElementHandle el)
        {
            try { return await el.BoundingBoxAsync().ConfigureAwait(false) is not null; }
            catch { return false; }
        }

        /// Parse JSON (chuỗi <c>ScanOrdersJs</c> trả về) → danh sách <see cref="SyncedOrder"/>. Bọc
        /// từng phần tử trong try (phần tử lạ không phá cả danh sách); đơn KHÔNG có mã (orderSn rỗng) bị BỎ.
        /// Tổng tiền parse qua <see cref="ShopeeShippingNav.ParseVndAmount"/> (bỏ mọi ký tự không phải số).
        /// </summary>
        internal static List<SyncedOrder> ParseOrdersJson(string? json)
        {
            var result = new List<SyncedOrder>();
            if (string.IsNullOrWhiteSpace(json))
            {
                return result;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return result;
                }

                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    try
                    {
                        var orderSn = GetJsonString(el, "orderSn");
                        if (string.IsNullOrWhiteSpace(orderSn))
                        {
                            continue; // không có mã đơn → không làm khóa được, bỏ
                        }

                        var itemsJson = "[]";
                        var itemCount = 0;
                        string? itemSummary = null;
                        string? sku = null;
                        if (el.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                        {
                            itemsJson = items.GetRawText();
                            itemCount = items.GetArrayLength();
                            if (itemCount > 0)
                            {
                                itemSummary = NullIfBlank(GetJsonString(items[0], "name"));
                                sku = ShopeeShippingNav.ExtractSku(itemSummary);
                            }
                        }

                        var totalText = GetJsonString(el, "totalText");
                        result.Add(new SyncedOrder
                        {
                            OrderSn = orderSn,
                            ShopeeOrderId = NullIfBlank(GetJsonString(el, "shopeeOrderId")),
                            BuyerUsername = NullIfBlank(GetJsonString(el, "buyer")),
                            ItemsJson = itemsJson,
                            ItemCount = itemCount,
                            ItemSummary = itemSummary,
                            Sku = sku,
                            TotalPriceText = NullIfBlank(totalText),
                            TotalPrice = ShopeeShippingNav.ParseVndAmount(totalText),
                            PaymentMethod = NullIfBlank(GetJsonString(el, "payment")),
                            Status = NullIfBlank(GetJsonString(el, "status")),
                            StatusDescription = NullIfBlank(GetJsonString(el, "statusDesc")),
                            CancelReason = NullIfBlank(GetJsonString(el, "cancelReason")),
                            Channel = NullIfBlank(GetJsonString(el, "channel")),
                            Carrier = NullIfBlank(GetJsonString(el, "carrier")),
                            TrackingNumber = NullIfBlank(GetJsonString(el, "tracking")),
                        });
                    }
                    catch { /* phần tử lạ — bỏ qua, không phá cả danh sách */ }
                }
            }
            catch { /* JSON hỏng — trả những gì đã parse được */ }

            return result;
        }

        /// <summary>Đọc chuỗi từ property JSON (chỉ nhận String; thiếu / kiểu khác → rỗng).</summary>
        private static string GetJsonString(JsonElement el, string prop)
            => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? string.Empty
                : string.Empty;

        /// <summary>Rỗng/khoảng-trắng → null (để cột DB để NULL thay vì chuỗi rỗng).</summary>
        private static string? NullIfBlank(string? s)
            => string.IsNullOrWhiteSpace(s) ? null : s;

        // ===== Lấy "Số tiền cuối cùng" từ TRANG CHI TIẾT đơn (cột "Ước tính" ở màn Đơn hàng) =====
        public async ValueTask DisposeAsync()
        {
            // Ngắt CDP trước (đóng kết nối Playwright ↔ Brave).
            try { await _browser.CloseAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }

            // KILL cả cây tiến trình Brave để không để lại tiến trình mồ côi giữ khóa --user-data-dir
            // (nếu còn, lần mở sau sẽ lỗi khóa hồ sơ).
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { /* bỏ qua */ }
            // Chờ tiến trình thoát HẲN (giải phóng khóa hồ sơ) trước khi cho phép mở lại cùng hồ sơ.
            try
            {
                using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                await _process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
            }
            catch { /* hết giờ/lỗi — bỏ qua, tầng gọi có retry */ }
            try { _process.Dispose(); } catch { /* bỏ qua */ }

            try { _playwright.Dispose(); } catch { /* bỏ qua */ }
        }
    }
}
