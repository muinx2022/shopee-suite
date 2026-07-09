using Microsoft.Playwright;
using Shopee.Core.Ai;
using Shopee.Core.BigSeller;

namespace UpdateProduct;

/// <summary>Kết quả 1 lần auto-login BigSeller.</summary>
internal enum AutoLoginOutcome { Success, NeedsOtp, Failed }

/// <summary>
/// ĐIỀU PHỐI tự đăng nhập BigSeller đầu phiên (Import + Update + Scrape) trên 1 <see cref="IPage"/>: gọi LÕI điền
/// form dùng chung <see cref="BigSellerLoginForm"/> (mở trang login en_US, điền email/mật khẩu, đóng "Warm Tips",
/// giải captcha AI, tick "I agree", submit, retry) → thành công thì mark phiên + (lane ghi-cookie) xuất token mới
/// ra file. Trả về Success (đã vào, có token mới khớp IP máy này), NeedsOtp (thiết bị lạ → BigSeller đòi mã email
/// `loginSecurity`, cần giải tay 1 lần để tạo device-trust), hoặc Failed. Đã verify thực tế: OpenAI đọc captcha
/// đúng; máy có cookie device-trust thì bỏ qua OTP. Caller (flow giao việc) nên gọi TRÊN PROFILE BỀN + IP STICKY
/// của acc, và có thể XOÁ muc_token cũ trước để ép login "không token" (luôn mint token tươi — token sync KHÔNG
/// scrape/import được).
/// </summary>
internal static class BigSellerAutoLogin
{
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
        var aiCfg = await HubAiConfig.GetAsync(ct).ConfigureAwait(false);
        AutoLoginOutcome outcome;
        try { outcome = Map(await BigSellerLoginForm.RunFormLoginAsync(page, email, password, aiCfg, log, ct).ConfigureAwait(false)); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { log?.Invoke($"Auto-login lỗi: {ex.Message} — thử dùng cookie file như cũ."); return; }

        if (outcome == AutoLoginOutcome.Success)
        {
            BigSellerSessionRegistry.MarkLoggedIn(accountId);
            if (exportCookie && !string.IsNullOrWhiteSpace(cookieFile))
                await BigSellerCookieEngine.TryExportProfileCookiesToFileAsync(debugPort, cookieFile!, log).ConfigureAwait(false);
        }
        else if (outcome == AutoLoginOutcome.NeedsOtp)
            log?.Invoke("⚠ BigSeller đòi mã xác nhận email (thiết bị mới). Hãy đăng nhập TAY 1 lần trên máy này (Tài khoản → Open BigSeller → login → Save & close) để tạo device-trust; sau đó auto-login chạy được (không cần mã nữa).");
        else
            log?.Invoke("Auto-login thất bại lần này — thử dùng cookie file (có thể vẫn 'login first').");
    }

    /// <summary>Map kết quả lõi form-fill (Core) → enum outcome của module (giữ nguyên 3 nhánh).</summary>
    private static AutoLoginOutcome Map(BigSellerLoginOutcome o) => o switch
    {
        BigSellerLoginOutcome.Success => AutoLoginOutcome.Success,
        BigSellerLoginOutcome.NeedsOtp => AutoLoginOutcome.NeedsOtp,
        _ => AutoLoginOutcome.Failed,
    };
}
