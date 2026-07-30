using Microsoft.Playwright;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Đọc trạng thái của một phiên đăng nhập Shopee đang mở (context Playwright): bắt cookie phiên và
/// <b>phát hiện trạng thái trang bán hàng</b> (đã đăng nhập / form đăng nhập / verify / captcha / không rõ).
/// Tách khỏi <c>LoginSession</c> để luồng verify email (<see cref="EmailVerifyFlow"/>) gọi thẳng được mà không
/// cần tham chiếu ngược về phiên.
/// </summary>
internal static class ShopeeSessionState
{
    /// <summary>Lấy toàn bộ cookie hiện có của context dưới dạng JSON (định dạng <see cref="CookieJson"/>).</summary>
    internal static async Task<string> CaptureCookiesJsonAsync(IBrowserContext context)
    {
        // Không truyền URL = lấy tất cả cookie trong context.
        var raw = await context.CookiesAsync().ConfigureAwait(false);

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

    /// <summary>
    /// <b>Phát hiện trạng thái trang bán hàng</b> — xem <see cref="ILoginSession.DetectPageStateAsync"/>.
    /// <para><b>Graceful — không bao giờ ném:</b> không có trang / lỗi bất kỳ → <see cref="ShopeePageState.Unknown"/>.</para>
    /// </summary>
    internal static async Task<ShopeePageState> DetectPageStateAsync(IBrowserContext context, CancellationToken ct = default)
    {
        try
        {
            var page = context.Pages.Count > 0 ? context.Pages[0] : null;
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
            if (await LoginPageProbe.IsAnyVisibleByClientRectsAsync(page, LoginSelectors.LoginFormDetectSelectors, ct).ConfigureAwait(false))
            {
                return ShopeePageState.LoginForm;
            }

            // 3) Không ở form login mà có alert xác minh (otp/mã xác/xác minh) → Verify (tín hiệu phụ).
            var alert = (await LoginPageProbe.ReadAlertTextAsync(page).ConfigureAwait(false)).ToLowerInvariant();
            if (alert.Contains("otp") || alert.Contains("mã xác") || alert.Contains("ma xac")
                || alert.Contains("xác minh") || alert.Contains("xac minh"))
            {
                return ShopeePageState.Verify;
            }

            // 4) Cookie phiên đăng nhập → LoggedIn; còn lại Unknown.
            if (ShopeeLoginCookies.IsLoggedIn(await CaptureCookiesJsonAsync(context).ConfigureAwait(false)))
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
}
