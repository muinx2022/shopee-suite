using Microsoft.Playwright;
using Shopee.Core.Ai;

namespace Shopee.Core.BigSeller;

/// <summary>Kết quả 1 lần điền form login BigSeller.</summary>
public enum BigSellerLoginOutcome { Success, NeedsOtp, Failed }

/// <summary>
/// LÕI ĐIỀN FORM login BigSeller (headless) trên 1 <see cref="IPage"/> — dùng chung cho mọi stack Playwright
/// (module Scrape/MultiBrave, module UpdateProduct, và Hub.Web): mở trang login (en_US) → điền email/mật khẩu →
/// đóng popup "Warm Tips" → giải captcha (<see cref="BigSellerCaptchaSolver"/>, AI vision) → tick "I agree" →
/// submit → RETRY (captcha đổi mới mỗi lần sai). Trả về Success (đã vào, có token mới khớp IP máy này),
/// NeedsOtp (thiết bị lạ → BigSeller đòi mã email `loginSecurity`, cần giải tay 1 lần để tạo device-trust),
/// hoặc Failed. Phần điều phối (attach CDP/headless, TTL, xuất token ra file) do caller lo — mỗi module giữ
/// orchestration + enum riêng và map kết quả về enum đó.
/// </summary>
public static class BigSellerLoginForm
{
    private const string LoginUrl = "https://www.bigseller.com/en_US/login.htm";

    /// <summary>Điền form login BigSeller + giải captcha + retry (<paramref name="maxAttempts"/> lần).
    /// <paramref name="onSecurityChallenge"/>: khi URL nhảy sang "security" (thiết bị mới) — null (module) → trả
    /// <see cref="BigSellerLoginOutcome.NeedsOtp"/> (giải tay 1 lần tạo device-trust); có callback (Hub) → để
    /// callback tự giải OTP/Turnstile, true→Success, false→Failed.</summary>
    public static async Task<BigSellerLoginOutcome> RunFormLoginAsync(
        IPage page, string email, string password, AiConfig ai, Action<string>? log, CancellationToken ct,
        int maxAttempts = 5, Func<IPage, CancellationToken, Task<bool>>? onSecurityChallenge = null)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        { log?.Invoke("Thiếu email/mật khẩu BigSeller — không thể auto-login."); return BigSellerLoginOutcome.Failed; }

        await page.GotoAsync(LoginUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20000 });
        await Task.Delay(2000, ct);

        // Đã đăng nhập sẵn (redirect khỏi trang login) → coi như thành công.
        if (!(page.Url ?? "").Contains("login", StringComparison.OrdinalIgnoreCase))
            return BigSellerLoginOutcome.Success;

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
            {
                // Thiết bị lạ → BigSeller đòi mã email. Module (không callback): trả NeedsOtp (giải tay 1 lần tạo
                // device-trust). Có callback (Hub): callback tự giải OTP/Turnstile → true=Success, false=Failed.
                if (onSecurityChallenge is not null)
                    return await onSecurityChallenge(page, ct) ? BigSellerLoginOutcome.Success : BigSellerLoginOutcome.Failed;
                log?.Invoke("⚠ BigSeller đòi mã xác nhận email (thiết bị mới) — cần giải tay 1 lần để tạo device-trust.");
                return BigSellerLoginOutcome.NeedsOtp;
            }
            if (!url.Contains("login", StringComparison.OrdinalIgnoreCase))
            { log?.Invoke("✔ Auto-login thành công — có token mới (khớp IP proxy này)."); return BigSellerLoginOutcome.Success; }

            log?.Invoke("Captcha sai/khác — đổi captcha thử lại.");
            await RefreshCaptchaAsync(page);
            await Task.Delay(1000, ct);
        }
        return BigSellerLoginOutcome.Failed;
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
