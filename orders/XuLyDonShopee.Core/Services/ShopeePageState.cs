namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Trạng thái trang bán hàng Shopee sau khi mở seller URL — dùng để điều phối auto-login / verify /
/// captcha ở <see cref="ILoginSession.DetectPageStateAsync"/>.
/// </summary>
public enum ShopeePageState
{
    /// <summary>Đã đăng nhập (có cookie phiên Shopee, không rơi vào các trạng thái chặn dưới đây).</summary>
    LoggedIn,

    /// <summary>Đang hiện form đăng nhập (có ô user/password) — cần tự điền user/pass.</summary>
    LoginForm,

    /// <summary>Trang xác minh (verify) — Shopee yêu cầu xác nhận danh tính qua email/OTP.</summary>
    Verify,

    /// <summary>Trang captcha — cần né bằng profile mới (không giải tự động).</summary>
    Captcha,

    /// <summary>Không xác định được (chưa load xong / trang lạ) — để caller xử như bình thường.</summary>
    Unknown
}
