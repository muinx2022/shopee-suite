using Microsoft.Playwright;
using Shopee.Core.Ai;
using Shopee.Core.BigSeller;
using Shopee.Core.Browser;
using Shopee.Core.Cdp;
using Shopee.Core.Infrastructure;

namespace OpenMultiBraveLauncherV3;

/// <summary>Kết quả 1 lần auto-login BigSeller.</summary>
public enum AutoLoginOutcome { Success, NeedsOtp, Failed }

/// <summary>
/// ĐIỀU PHỐI tự đăng nhập BigSeller cho module Scrape (MultiBrave, stack Playwright riêng): attach Brave qua
/// CDP hoặc mở Brave headless → gọi LÕI điền form dùng chung <see cref="BigSellerLoginForm"/> (mở trang login,
/// điền email/mật khẩu, giải captcha AI, tick "I agree", submit, retry) → thành công thì mark phiên + xuất token
/// mới ra cookie-file. Phần cookie/CDP cũng đã về Core (<c>CdpClient</c>, <see cref="BigSellerCookieEngine"/>).
/// Trả về Success (token mới KHỚP IP proxy của Brave này — token sync KHÔNG scrape được vì bị coi phiên lạ),
/// NeedsOtp (thiết bị lạ → cần giải mã email tay 1 lần tạo device-trust), hoặc Failed. Orchestration (TTL +
/// attach CDP + xuất token ra file) nằm ở <c>BraveInstanceSession.TryAutoLoginBigSellerAsync</c>.
/// <para>Cả ba lối vào dưới đây dùng CHUNG <see cref="LoginViaCdpAsync"/> (attach CDP → tìm page → điền form →
/// mark + xuất cookie); phần riêng của từng lối (guard đầu vào, tự phóng Brave headless, chuỗi log) nằm quanh
/// lời gọi đó.</para>
/// </summary>
public static class BigSellerAutoLogin
{
    /// <summary>BUỘC tự đăng nhập ngay trong Brave đang mở tại <paramref name="cdpPort"/> (BỎ guard TTL/IsFresh) —
    /// dùng cho nút "Mở Profile Bigseller" khi phát hiện CHƯA đăng nhập. Attach Playwright qua CDP → điền
    /// email/mật khẩu + giải captcha (AI) → thành công thì mark + xuất token ra <paramref name="cookieFile"/>.
    /// Trả outcome (NeedsOtp = thiết bị mới, cần đăng nhập TAY 1 lần tạo device-trust). ADDITIVE: mọi lỗi nuốt,
    /// trả Failed → caller rơi về đăng nhập tay như cũ.</summary>
    public static async Task<AutoLoginOutcome> ForceLoginInBraveAsync(
        int cdpPort, string accountId, string email, string password, string? cookieFile,
        Action<string>? log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        { log?.Invoke("BigSeller: thiếu email/mật khẩu cho tk này — không tự đăng nhập được, hãy đăng nhập tay."); return AutoLoginOutcome.Failed; }

        return await LoginViaCdpAsync(
            cdpPort, accountId, email, password, cookieFile,
            noContextMessage: "BigSeller auto-login: Brave chưa có context.",
            errorPrefix: "BigSeller auto-login lỗi: ", errorSuffix: "",
            log, ct).ConfigureAwait(false) ?? AutoLoginOutcome.Failed;
    }

    /// <summary>ĐẦU PHIÊN SCRAPE: đảm bảo phiên BigSeller "tươi" bằng cách TỰ ĐĂNG NHẬP ngay TRONG Brave scrape
    /// (attach Playwright qua CDP port <paramref name="cdpPort"/>) → token mint ra KHỚP IP proxy của Brave này
    /// (token mint ở ngữ cảnh khác sẽ bị BigSeller từ chối). Trong TTL (<see cref="BigSellerSessionRegistry"/>)
    /// hoặc chưa nhập mật khẩu → KHÔNG login (để bước nạp cookie-file cũ lo). Thành công → mark fresh + xuất
    /// token mới ra <paramref name="cookieFile"/> (để lane khác / lần sau nạp lại). ADDITIVE + AN TOÀN: mọi lỗi
    /// đều nuốt và rơi về hành vi cũ (nạp cookie từ file) — không phá luồng scrape hiện có.</summary>
    public static async Task EnsureFreshSessionAsync(
        int cdpPort, string accountId, string email, string password, string? cookieFile,
        Action<string>? log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(password))
        { log?.Invoke("BigSeller: chưa nhập mật khẩu cho tk này — bỏ auto-login, dùng cookie file như cũ."); return; }
        if (BigSellerSessionRegistry.IsFresh(accountId))
        { log?.Invoke("BigSeller: phiên còn tươi (đã tự login < TTL) — không login lại, dùng cookie có sẵn/file."); return; }

        log?.Invoke("BigSeller: TỰ đăng nhập đầu phiên để lấy token tươi (khớp IP proxy của Brave này)…");
        var outcome = await LoginViaCdpAsync(
            cdpPort, accountId, email, password, cookieFile,
            noContextMessage: "BigSeller auto-login: Brave chưa có context — dùng cookie file.",
            errorPrefix: "BigSeller auto-login lỗi: ", errorSuffix: " — dùng cookie file như cũ.",
            log, ct).ConfigureAwait(false);

        // null = chưa tới được bước điền form (không có context / lỗi attach) — thông điệp đã log bên trong.
        if (outcome == AutoLoginOutcome.NeedsOtp)
            log?.Invoke("⚠ BigSeller đòi mã xác nhận email (thiết bị mới). Đăng nhập TAY 1 lần (mục BigSeller → Mở Profile → login) để tạo device-trust; sau đó auto-login chạy được (chỉ captcha).");
        else if (outcome == AutoLoginOutcome.Failed)
            log?.Invoke("BigSeller auto-login thất bại lần này — dùng cookie file (có thể vẫn 'login first').");
    }

    /// <summary>Tự đăng nhập 1 tk BigSeller HEADLESS (KHÔNG hiện cửa sổ): TỰ mở Brave --headless (profile riêng
    /// theo <paramref name="accountId"/>, có proxy nếu truyền) → điền email/mật khẩu + giải captcha (AI) → LƯU
    /// cookie ra <paramref name="cookieFile"/> → đóng. Dùng cho nút "Đăng nhập tất cả". Đã đăng nhập sẵn (profile
    /// bền) → lõi form-fill trả Success ngay (giữ token, vẫn lưu lại). NeedsOtp = thiết bị mới → cần đăng nhập TAY 1
    /// lần (mục Cấu hình BigSeller → Mở Profile). Mọi lỗi → Failed (không ném ra ngoài, để vòng "tất cả" chạy tiếp).</summary>
    public static async Task<AutoLoginOutcome> LoginHeadlessAsync(
        string accountId, string email, string password, string cookieFile, string? proxyServer,
        Action<string>? log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        { log?.Invoke("Thiếu email/mật khẩu — bỏ qua tk này."); return AutoLoginOutcome.Failed; }
        if (string.IsNullOrWhiteSpace(cookieFile))
        { log?.Invoke("Thiếu file cookie — bỏ qua tk này."); return AutoLoginOutcome.Failed; }

        var profileDir = Path.Combine(SuitePaths.ModuleDir("bigseller-login"), accountId);
        Directory.CreateDirectory(profileDir);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cookieFile))!);
        // TẮT Brave Shields cho bigseller → tracker (_ga/_fbp/_tt…) nạp được → lưu BỘ cookie ĐẦY ĐỦ (phiên bền).
        BigSellerLoginRunner.EnsureBraveShieldsDown(profileDir);

        var launcher = new BrowserLauncher(BrowserKind.Brave);
        try
        {
            // Mở Brave HEADLESS (không hiện cửa sổ) — proxy riêng nếu tk có (token mint khớp IP với scrape).
            launcher.Launch(profileDir, proxyServer, BigSellerLoginForm.LoginUrl, new[] { "--headless=new" });
            var port = launcher.CdpPort;

            // Chờ CDP sẵn sàng (~tối đa 15s) trước khi attach Playwright.
            for (var i = 0; i < 30 && !await CdpSession.IsBrowserAliveAsync(port, ct).ConfigureAwait(false); i++)
                await Task.Delay(500, ct).ConfigureAwait(false);

            return await LoginViaCdpAsync(
                port, accountId, email, password, cookieFile,
                noContextMessage: "Brave headless chưa có context.",
                errorPrefix: "Auto-login headless lỗi: ", errorSuffix: "",
                log, ct,
                // Headless mở trang login trực tiếp → lấy page đầu tiên (không lọc theo URL bigseller).
                preferBigSellerPage: false,
                // Chờ tracker nạp thêm cho bộ cookie đầy đủ trước khi đọc cookie.
                postLoginDelayMs: 4000,
                // "Đăng nhập tất cả" cần cookie THẬT trên đĩa → không lưu được coi như thất bại.
                cookieFailureIsFatal: true).ConfigureAwait(false) ?? AutoLoginOutcome.Failed;
        }
        catch (OperationCanceledException) { throw; }
        // Lỗi phóng Brave / chờ CDP (LoginViaCdpAsync tự nuốt phần của nó) — vẫn KHÔNG ném ra ngoài để vòng
        // "Đăng nhập tất cả" chạy tiếp tk kế.
        catch (Exception ex) { log?.Invoke($"Auto-login headless lỗi: {ex.Message}"); return AutoLoginOutcome.Failed; }
        finally
        {
            launcher.Kill();   // đóng Brave headless
        }
    }

    /// <summary>
    /// LÕI DÙNG CHUNG của cả ba lối vào: attach Playwright qua CDP tại <paramref name="cdpPort"/> → lấy context/page
    /// → <see cref="BigSellerLoginForm.RunFormLoginAsync"/> → Success thì mark phiên + xuất cookie ra
    /// <paramref name="cookieFile"/>.
    /// <para>Trả <c>null</c> khi KHÔNG tới được bước điền form (Brave chưa có context, hoặc exception đã bị nuốt) —
    /// caller phân biệt được "đã chạy form và trượt" với "chưa chạy được form" để log đúng như bản cũ.</para>
    /// </summary>
    /// <param name="preferBigSellerPage">true = ưu tiên tab đang mở bigseller (attach vào Brave đang chạy);
    /// false = lấy tab đầu tiên (Brave headless do chính hàm này mở ở trang login).</param>
    /// <param name="postLoginDelayMs">Chờ thêm sau khi login thành công, TRƯỚC khi đọc cookie (0 = không chờ).</param>
    /// <param name="cookieFailureIsFatal">true = không có cookie auth / ghi cookie lỗi → trả Failed;
    /// false = chỉ log rồi giữ nguyên outcome (bản cũ của Force/EnsureFresh nuốt lỗi xuất cookie).</param>
    private static async Task<AutoLoginOutcome?> LoginViaCdpAsync(
        int cdpPort, string accountId, string email, string password, string? cookieFile,
        string noContextMessage, string errorPrefix, string errorSuffix,
        Action<string>? log, CancellationToken ct,
        bool preferBigSellerPage = true, int postLoginDelayMs = 0, bool cookieFailureIsFatal = false)
    {
        IPlaywright? pw = null;
        IBrowser? browser = null;
        try
        {
            pw = await Playwright.CreateAsync().ConfigureAwait(false);
            browser = await pw.Chromium.ConnectOverCDPAsync($"http://127.0.0.1:{cdpPort}", new() { Timeout = 30000 }).ConfigureAwait(false);
            var context = browser.Contexts.FirstOrDefault();
            if (context is null) { log?.Invoke(noContextMessage); return null; }
            var page = (preferBigSellerPage
                           ? context.Pages.FirstOrDefault(p => (p.Url ?? "").Contains("bigseller", StringComparison.OrdinalIgnoreCase))
                           : context.Pages.FirstOrDefault())
                       ?? await context.NewPageAsync().ConfigureAwait(false);

            var aiCfg = await HubAiConfig.GetAsync(ct).ConfigureAwait(false);
            var outcome = Map(await BigSellerLoginForm.RunFormLoginAsync(page, email, password, aiCfg, log, ct).ConfigureAwait(false));
            if (outcome == AutoLoginOutcome.Success)
            {
                BigSellerSessionRegistry.MarkLoggedIn(accountId);
                if (postLoginDelayMs > 0)
                    await Task.Delay(postLoginDelayMs, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(cookieFile))
                {
                    try
                    {
                        var cookies = await BigSellerCookieEngine.GetBigSellerCookiesAsync(cdpPort).ConfigureAwait(false);
                        if (BigSellerCookieEngine.HasAuthCookie(cookies))
                            BigSellerCookieEngine.TryWriteCookieFile(cookieFile!, cookies, log);
                        else if (cookieFailureIsFatal)
                        { log?.Invoke("Đăng nhập xong nhưng chưa thấy cookie auth — bỏ lưu."); return AutoLoginOutcome.Failed; }
                    }
                    catch (Exception ex)
                    {
                        if (cookieFailureIsFatal) { log?.Invoke($"Lưu cookie lỗi: {ex.Message}"); return AutoLoginOutcome.Failed; }
                        log?.Invoke($"BigSeller: xuất token mới ra file lỗi: {ex.Message}");
                    }
                }
            }
            return outcome;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { log?.Invoke($"{errorPrefix}{ex.Message}{errorSuffix}"); return null; }
        finally
        {
            // ConnectOverCDP: Dispose Playwright chỉ NGẮT kết nối (Brave do launcher mở, KHÔNG đóng theo).
            try { pw?.Dispose(); } catch { }
        }
    }

    /// <summary>Map kết quả lõi form-fill (Core) → enum outcome của module (giữ nguyên 3 nhánh).</summary>
    private static AutoLoginOutcome Map(BigSellerLoginOutcome o) => o switch
    {
        BigSellerLoginOutcome.Success => AutoLoginOutcome.Success,
        BigSellerLoginOutcome.NeedsOtp => AutoLoginOutcome.NeedsOtp,
        _ => AutoLoginOutcome.Failed,
    };
}
