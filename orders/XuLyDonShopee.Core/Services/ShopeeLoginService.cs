using System.Diagnostics;
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
/// <para>
/// <b>FACADE:</b> thân các bước nằm ở lớp riêng cùng namespace — khởi chạy trình duyệt
/// <see cref="LoginBrowserBootstrap"/>, đăng nhập subaccount <see cref="SubaccountLoginFlow"/>, xác minh qua
/// email <see cref="EmailVerifyFlow"/>, hàm thuần <see cref="LoginParsers"/>, selector/regex
/// <see cref="LoginSelectors"/>.
/// </para>
/// </summary>
public class ShopeeLoginService
{
    /// <summary>URL Nền tảng tài khoản phụ — điểm vào đăng nhập mới (một tài khoản có nhiều shop).</summary>
    public const string SubaccountUrl = "https://subaccount.shopee.com/";

    /// <summary>Trang "Tài khoản" của Nền tảng tài khoản phụ — điểm vào của BẢN SẠCH (cầu nối): có cookie hồ sơ →
    /// hiện trang tài khoản (có "Kênh Người bán"); hết cookie → ra form đăng nhập. Dùng để SSO lại về trang chọn
    /// shop (né sticky-shop server-side khi mở thẳng /portal/shop).</summary>
    public const string SubaccountAccountUrl = "https://subaccount.shopee.com/account";

    // ===== Forwarder cho test (luồng verify-email) =====
    // Logic khớp text thực nằm trong <see cref="LoginParsers"/> (nơi giữ các hàm thuần dùng chung với các luồng).
    // Phơi lại ở cấp class này (internal — InternalsVisibleTo cho XuLyDonShopee.Tests) để unit-test được các hàm
    // thuần đó mà không cần dựng cả phiên trình duyệt.

    /// <summary>Chuẩn hóa text để so khớp bền (bỏ dấu tiếng Việt kể cả đ→d, gộp space, hạ chữ thường).</summary>
    internal static string NormalizeForMatch(string? s) => LoginParsers.NormalizeForMatch(s);

    /// <summary>True nếu dòng mail là "Cảnh báo bảo mật Tài khoản Shopee" (người gửi shopee + tiêu đề chứa
    /// "cảnh báo bảo mật"); loại mail trả hàng/khác của Shopee.</summary>
    internal static bool IsSecurityWarningMailRow(string? rowText) => LoginParsers.IsSecurityWarningMailRow(rowText);

    /// <summary>True nếu text khớp link xác nhận cần bấm (vd "TẠI ĐÂY") — KHÔNG còn khớp "here"/"click here".</summary>
    internal static bool MatchesConfirmLink(string? text) => LoginParsers.MatchesConfirmLink(text);

    /// <summary>True nếu text là trang báo link đã hết hạn/hết hiệu lực.</summary>
    internal static bool MatchesConfirmExpired(string? text) => LoginParsers.MatchesConfirmExpired(text);

    /// <summary>True nếu text là nav "Tài khoản của tôi" trên Nền tảng tài khoản phụ (tín hiệu ĐÃ đăng nhập).</summary>
    internal static bool MatchesMyAccountNav(string? text) => LoginParsers.MatchesMyAccountNav(text);

    /// <summary>True nếu text là entry "Kênh Người bán" (mở sang Seller Centre) trên Nền tảng tài khoản phụ —
    /// dùng ở bước bắc cầu SSO cuối TryLoginSubaccountAsync.</summary>
    internal static bool MatchesSellerChannelEntry(string? text) => LoginParsers.MatchesSellerChannelEntry(text);

    /// <summary>Chuyển JSON mảng <c>{rowKey,name,login}</c> (đọc từ bảng <c>/portal/shop</c>) thành danh sách
    /// <see cref="ShopListItem"/> — forwarder để unit-test hàm thuần mà không cần dựng phiên trình duyệt.</summary>
    internal static IReadOnlyList<ShopListItem> ParseShopListJson(string? json) => LoginParsers.ParseShopListJson(json);

    /// <summary>Chuyển JSON mảng đơn (do <c>pageScanOrders</c>/<c>ScanOrdersJs</c> đọc từ DOM) thành danh sách
    /// <see cref="SyncedOrder"/> — forwarder tái dùng hàm thuần <c>LoginParsers.ParseOrdersJson</c> cho cầu nối
    /// extension (<c>OrdersBridgeSession</c>), không viết lại logic parse.</summary>
    internal static List<SyncedOrder> ParseOrdersJson(string? json) => LoginParsers.ParseOrdersJson(json);

    /// <inheritdoc cref="LoginBrowserBootstrap.DescribeBrowser"/>
    public static string DescribeBrowser() => LoginBrowserBootstrap.DescribeBrowser();

    /// <summary>
    /// Mở một cửa sổ Brave tới trang bán hàng bằng
    /// <b>hồ sơ persistent</b> đặt tại <paramref name="userDataDir"/> (mỗi tài khoản một thư mục riêng)
    /// — nhờ đó lần sau mở lại vẫn còn đăng nhập — rồi trả về phiên đang mở. Đi thẳng IP máy (module đã bỏ
    /// hẳn proxy runtime). Cơ chế: tự khởi chạy tiến trình Brave với cờ stealth + <c>--user-data-dir</c> +
    /// <c>--remote-debugging-port</c>, chờ CDP sẵn sàng, nối vào qua
    /// <see cref="IBrowserType.ConnectOverCDPAsync"/>. Ném <see cref="InvalidOperationException"/>
    /// (message tiếng Việt) nếu không mở được.
    /// </summary>
    public async Task<ILoginSession> OpenAsync(
        string userDataDir, CancellationToken ct = default, Action<string>? log = null)
    {
        IPlaywright? playwright = null;
        Process? process = null;
        IBrowser? browser = null;

        try
        {
            playwright = await Playwright.CreateAsync().ConfigureAwait(false);

            IBrowserContext context;
            (process, browser, context) = await LoginBrowserBootstrap
                .LaunchAndConnectAsync(playwright, userDataDir, ct, log).ConfigureAwait(false);

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
    /// Phiên đăng nhập <b>sở hữu tiến trình Brave</b>: <see cref="Closed"/> hoàn tất khi tiến trình
    /// thoát / CDP ngắt / context đóng; <see cref="DisposeAsync"/> ngắt CDP và KILL cả cây tiến trình
    /// Brave để không để lại tiến trình mồ côi giữ khóa hồ sơ.
    /// <para><b>FACADE:</b> mọi bước nghiệp vụ được ủy quyền cho lớp luồng riêng — phiên chỉ giữ vòng đời
    /// (process/browser/context) và bắc cầu tham số.</para>
    /// </summary>
    private sealed class LoginSession : ILoginSession
    {
        private readonly IPlaywright _playwright;
        private readonly IBrowser _browser;
        private readonly IBrowserContext _context;
        private readonly Process _process;

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

        public Task<ShopeePageState> DetectPageStateAsync(CancellationToken ct = default)
            => ShopeeSessionState.DetectPageStateAsync(_context, ct);

        public Task<bool> TryLoginSubaccountAsync(
            string user, string password, string? verifyEmail, string? verifyEmailPassword,
            Action<string>? log = null, CancellationToken ct = default)
            => SubaccountLoginFlow.RunAsync(_context, user, password, verifyEmail, verifyEmailPassword, log, ct);

        public Task<bool> TryVerifyByEmailAsync(
            string verifyEmail, string verifyEmailPassword, bool autoConfirm, Action<string>? log = null, CancellationToken ct = default)
            => EmailVerifyFlow.RunAsync(_browser, _context, verifyEmail, verifyEmailPassword, autoConfirm, log, ct);

        public Task<string> CaptureCookiesJsonAsync()
            => ShopeeSessionState.CaptureCookiesJsonAsync(_context);

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
