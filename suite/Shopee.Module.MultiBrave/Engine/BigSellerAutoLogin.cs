using Microsoft.Playwright;
using Shopee.Core.Ai;
using Shopee.Core.BigSeller;

namespace OpenMultiBraveLauncherV3;

/// <summary>Kết quả 1 lần auto-login BigSeller.</summary>
public enum AutoLoginOutcome { Success, NeedsOtp, Failed }

/// <summary>
/// TỰ ĐĂNG NHẬP BigSeller (headless) trên 1 <see cref="IPage"/> — BẢN SAO của
/// <c>UpdateProduct.BigSellerAutoLogin</c> cho module Scrape (MultiBrave dùng stack riêng: raw CDP + không
/// tham chiếu UpdateProduct, nên nhân bản như <c>BigSellerCookieImporter</c>/<c>CdpClient</c> vốn đã nhân bản).
/// Mở trang login (en_US) → điền email/mật khẩu → đóng popup "Warm Tips" → giải captcha
/// (<see cref="BigSellerCaptchaSolver"/>, AI vision) → tick "I agree" → submit → RETRY. Trả về Success (có
/// token mới KHỚP IP proxy của Brave này — token sync KHÔNG scrape được vì bị coi phiên lạ), NeedsOtp (thiết
/// bị lạ → cần giải mã email tay 1 lần tạo device-trust), hoặc Failed. Orchestration (TTL + attach CDP +
/// xuất token ra file) nằm ở <c>BraveInstanceSession.TryAutoLoginBigSellerAsync</c>.
/// </summary>
public static class BigSellerAutoLogin
{
    private const string LoginUrl = "https://www.bigseller.com/en_US/login.htm";

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

        IPlaywright? pw = null;
        IBrowser? browser = null;
        try
        {
            pw = await Playwright.CreateAsync().ConfigureAwait(false);
            browser = await pw.Chromium.ConnectOverCDPAsync($"http://127.0.0.1:{cdpPort}", new() { Timeout = 30000 }).ConfigureAwait(false);
            var context = browser.Contexts.FirstOrDefault();
            if (context is null) { log?.Invoke("BigSeller auto-login: Brave chưa có context."); return AutoLoginOutcome.Failed; }
            var page = context.Pages.FirstOrDefault(p => (p.Url ?? "").Contains("bigseller", StringComparison.OrdinalIgnoreCase))
                       ?? await context.NewPageAsync().ConfigureAwait(false);

            var outcome = await TryAsync(page, email, password, AiConfigStore.Shared.Current, log, ct).ConfigureAwait(false);
            if (outcome == AutoLoginOutcome.Success)
            {
                BigSellerSessionRegistry.MarkLoggedIn(accountId);
                if (!string.IsNullOrWhiteSpace(cookieFile))
                {
                    try
                    {
                        var cookies = await BigSellerCookieImporter.GetBigSellerCookiesAsync(cdpPort).ConfigureAwait(false);
                        if (BigSellerCookieImporter.HasAuthCookie(cookies))
                            BigSellerCookieImporter.TryWriteCookieFile(cookieFile!, cookies, log);
                    }
                    catch (Exception ex) { log?.Invoke($"BigSeller: xuất token mới ra file lỗi: {ex.Message}"); }
                }
            }
            return outcome;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { log?.Invoke($"BigSeller auto-login lỗi: {ex.Message}"); return AutoLoginOutcome.Failed; }
        finally
        {
            // ConnectOverCDP: Dispose Playwright chỉ NGẮT kết nối (Brave do launcher mở, KHÔNG đóng theo).
            try { pw?.Dispose(); } catch { }
        }
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
        IPlaywright? pw = null;
        IBrowser? browser = null;
        try
        {
            pw = await Playwright.CreateAsync().ConfigureAwait(false);
            browser = await pw.Chromium.ConnectOverCDPAsync($"http://127.0.0.1:{cdpPort}", new() { Timeout = 30000 }).ConfigureAwait(false);
            var context = browser.Contexts.FirstOrDefault();
            if (context is null) { log?.Invoke("BigSeller auto-login: Brave chưa có context — dùng cookie file."); return; }
            var page = context.Pages.FirstOrDefault(p => (p.Url ?? "").Contains("bigseller", StringComparison.OrdinalIgnoreCase))
                       ?? await context.NewPageAsync().ConfigureAwait(false);

            var outcome = await TryAsync(page, email, password, AiConfigStore.Shared.Current, log, ct).ConfigureAwait(false);
            if (outcome == AutoLoginOutcome.Success)
            {
                BigSellerSessionRegistry.MarkLoggedIn(accountId);
                if (!string.IsNullOrWhiteSpace(cookieFile))
                {
                    try
                    {
                        var cookies = await BigSellerCookieImporter.GetBigSellerCookiesAsync(cdpPort).ConfigureAwait(false);
                        if (BigSellerCookieImporter.HasAuthCookie(cookies))
                            BigSellerCookieImporter.TryWriteCookieFile(cookieFile!, cookies, log);
                    }
                    catch (Exception ex) { log?.Invoke($"BigSeller: xuất token mới ra file lỗi: {ex.Message}"); }
                }
            }
            else if (outcome == AutoLoginOutcome.NeedsOtp)
                log?.Invoke("⚠ BigSeller đòi mã xác nhận email (thiết bị mới). Đăng nhập TAY 1 lần (mục BigSeller → Mở Profile → login) để tạo device-trust; sau đó auto-login chạy được (chỉ captcha).");
            else
                log?.Invoke("BigSeller auto-login thất bại lần này — dùng cookie file (có thể vẫn 'login first').");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { log?.Invoke($"BigSeller auto-login lỗi: {ex.Message} — dùng cookie file như cũ."); }
        finally
        {
            // ConnectOverCDP: Dispose Playwright chỉ NGẮT kết nối (Brave do launcher mở, KHÔNG bị đóng theo).
            try { pw?.Dispose(); } catch { }
        }
    }

    public static async Task<AutoLoginOutcome> TryAsync(
        IPage page, string email, string password, AiConfig ai, Action<string>? log, CancellationToken ct, int maxAttempts = 5)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        { log?.Invoke("Thiếu email/mật khẩu BigSeller — không thể auto-login."); return AutoLoginOutcome.Failed; }

        await page.GotoAsync(LoginUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20000 });
        await Task.Delay(2000, ct);

        // Đã đăng nhập sẵn (redirect khỏi trang login) → coi như thành công.
        if (!(page.Url ?? "").Contains("login", StringComparison.OrdinalIgnoreCase))
            return AutoLoginOutcome.Success;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await DismissWarmTipsAsync(page);
            try
            {
                await page.FillAsync("input[name=account]", email);
                await page.FillAsync("input[name=password]", password);
                if (!await page.IsCheckedAsync("input.el-checkbox__original"))
                    await page.Locator(".el-checkbox").First.ClickAsync(new() { Timeout = 3000 });

                var src = await page.GetAttributeAsync("img.comb-code-img", "src");
                if (string.IsNullOrEmpty(src) || !src.Contains("base64,"))
                { log?.Invoke("Không thấy ảnh captcha trên form login."); await Task.Delay(1500, ct); continue; }
                var png = Convert.FromBase64String(src[(src.IndexOf("base64,", StringComparison.Ordinal) + 7)..]);
                var code = await BigSellerCaptchaSolver.SolveAsync(ai, png, ct);
                log?.Invoke($"Auto-login lần {attempt}: captcha đọc '{code}'.");
                if (code.Length < 4) { await RefreshCaptchaAsync(page); await Task.Delay(800, ct); continue; }

                await page.FillAsync("input[name=picVerificationCode]", code);
                try { await page.WaitForFunctionAsync("() => { const b = document.querySelector('button.opt-btn'); return b && !b.disabled; }", null, new() { Timeout = 4000 }); } catch { }
                await page.Locator("button.opt-btn").First.ClickAsync(new() { Force = true, Timeout = 5000 });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log?.Invoke($"Auto-login bước điền lỗi: {ex.Message}"); }

            await Task.Delay(4500, ct);
            await DismissWarmTipsAsync(page);
            var url = page.Url ?? "";
            if (url.Contains("security", StringComparison.OrdinalIgnoreCase))
            { log?.Invoke("⚠ BigSeller đòi mã xác nhận email (thiết bị mới) — cần giải tay 1 lần để tạo device-trust."); return AutoLoginOutcome.NeedsOtp; }
            if (!url.Contains("login", StringComparison.OrdinalIgnoreCase))
            { log?.Invoke("✔ Auto-login thành công — có token mới (khớp IP proxy này)."); return AutoLoginOutcome.Success; }

            log?.Invoke("Captcha sai/khác — đổi captcha thử lại.");
            await RefreshCaptchaAsync(page);
            await Task.Delay(1000, ct);
        }
        return AutoLoginOutcome.Failed;
    }

    /// <summary>Đóng popup "Warm Tips" (China/pro) hay che form — bấm nút KHÔNG-primary ("No Prompt"/đóng),
    /// TRÁNH "Jump Now" (primary → nhảy sang bigseller.pro).</summary>
    private static async Task DismissWarmTipsAsync(IPage page)
    {
        try
        {
            for (var i = 0; i < 3; i++)
            {
                var box = page.Locator(".el-message-box, .el-overlay-message-box");
                if (await box.CountAsync() == 0 || !await box.First.IsVisibleAsync()) return;
                var cancel = page.Locator(".el-message-box__btns .el-button:not(.el-button--primary), .el-overlay-message-box .el-button:not(.el-button--primary)");
                if (await cancel.CountAsync() > 0) await cancel.First.ClickAsync(new() { Timeout = 2000 });
                else { try { await page.Keyboard.PressAsync("Escape"); } catch { } }
                await Task.Delay(500);
            }
        }
        catch { }
    }

    /// <summary>Bấm ảnh captcha để server phát captcha MỚI (sau khi giải sai).</summary>
    private static async Task RefreshCaptchaAsync(IPage page)
    {
        try { await page.Locator("button.comb-code").First.ClickAsync(new() { Timeout = 2000 }); } catch { }
    }
}
