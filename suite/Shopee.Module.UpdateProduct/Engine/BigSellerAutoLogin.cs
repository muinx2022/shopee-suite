using Microsoft.Playwright;
using Shopee.Core.Ai;
using Shopee.Core.BigSeller;

namespace UpdateProduct;

/// <summary>Kết quả 1 lần auto-login BigSeller.</summary>
internal enum AutoLoginOutcome { Success, NeedsOtp, Failed }

/// <summary>
/// TỰ ĐĂNG NHẬP BigSeller (headless) trên 1 <see cref="IPage"/>: mở trang login (en_US) → điền email/mật khẩu →
/// đóng popup "Warm Tips" → giải captcha (<see cref="BigSellerCaptchaSolver"/>, AI vision) → tick "I agree" →
/// submit → RETRY (captcha đổi mới mỗi lần sai). Trả về Success (đã vào, có token mới khớp IP máy này),
/// NeedsOtp (thiết bị lạ → BigSeller đòi mã email `loginSecurity`, cần giải tay 1 lần để tạo device-trust),
/// hoặc Failed. Đã verify thực tế: OpenAI đọc captcha đúng; máy có cookie device-trust thì bỏ qua OTP.
/// Caller (flow giao việc) nên gọi TRÊN PROFILE BỀN + IP STICKY của acc, và có thể XOÁ muc_token cũ trước để
/// ép login "không token" (luôn mint token tươi từ server — token sync KHÔNG scrape/import được).
/// </summary>
internal static class BigSellerAutoLogin
{
    private const string LoginUrl = "https://www.bigseller.com/en_US/login.htm";

    /// <summary>ĐẦU PHIÊN (dùng chung Import + Update + Scrape): đảm bảo phiên BigSeller "tươi" bằng cách TỰ
    /// ĐĂNG NHẬP (mint token khớp IP máy này). Token sync KHÔNG scrape/import được (bị coi phiên lạ), nên mỗi
    /// máy tự login bằng credential. Trong TTL (<see cref="BigSellerSessionRegistry"/>) thì bỏ qua (dùng lại
    /// cho cả dây chuyền). Chưa nhập mật khẩu → giữ hành vi cũ (cookie file). Cần OTP (thiết bị mới) → báo để
    /// đăng nhập tay 1 lần tạo device-trust. accountId gắn TTL theo acc; chỉ lane ghi-cookie (exportCookie)
    /// mới xuất token mới ra <paramref name="cookieFile"/>.</summary>
    public static async Task EnsureFreshSessionAsync(
        IPage page, string accountId, string email, string password, string? cookieFile, int debugPort,
        bool exportCookie, Action<string>? log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            log?.Invoke("Chưa nhập mật khẩu BigSeller cho tk này (mục BigSeller) — bỏ auto-login, dùng cookie file như cũ.");
            return;
        }
        if (BigSellerSessionRegistry.IsFresh(accountId))
        {
            log?.Invoke("Phiên đăng nhập còn tươi (đã tự login < TTL) — dùng lại, không login lại.");
            return;
        }
        log?.Invoke("Bắt đầu phiên — TỰ đăng nhập BigSeller để lấy token tươi (mỗi máy tự mint, không phụ thuộc Hub)…");
        AutoLoginOutcome outcome;
        try { outcome = await TryAsync(page, email, password, AiConfigStore.Shared.Current, log, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { log?.Invoke($"Auto-login lỗi: {ex.Message} — thử dùng cookie file như cũ."); return; }

        if (outcome == AutoLoginOutcome.Success)
        {
            BigSellerSessionRegistry.MarkLoggedIn(accountId);
            if (exportCookie && !string.IsNullOrWhiteSpace(cookieFile))
                await BigSellerCookieImporter.TryExportProfileCookiesToFileAsync(debugPort, cookieFile!, log).ConfigureAwait(false);
        }
        else if (outcome == AutoLoginOutcome.NeedsOtp)
            log?.Invoke("⚠ BigSeller đòi mã xác nhận email (thiết bị mới). Hãy đăng nhập TAY 1 lần trên máy này (Tài khoản → Open BigSeller → login → Save & close) để tạo device-trust; sau đó auto-login chạy được (không cần mã nữa).");
        else
            log?.Invoke("Auto-login thất bại lần này — thử dùng cookie file (có thể vẫn 'login first').");
    }

    public static async Task<AutoLoginOutcome> TryAsync(
        IPage page, string email, string password, AiConfig ai, Action<string>? log, CancellationToken ct, int maxAttempts = 5)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        { log?.Invoke("Thiếu email/mật khẩu BigSeller — không thể auto-login."); return AutoLoginOutcome.Failed; }

        await page.GotoAsync(LoginUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20000 });
        await Task.Delay(2000, ct);

        // Đã đăng nhập sẵn (redirect khỏi trang login) → coi như thành công (caller muốn login "không token" thì
        // xoá muc_token trước khi gọi).
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
            { log?.Invoke("✔ Auto-login thành công — có token mới (khớp IP máy này)."); return AutoLoginOutcome.Success; }

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
