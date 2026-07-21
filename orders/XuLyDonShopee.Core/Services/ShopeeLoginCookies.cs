namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Nhận biết cookie đã đăng nhập Shopee. Trang bán hàng (banhang.shopee.vn) set nhiều cookie theo dõi
/// (SPC_F, SPC_CDS, csrftoken...) NGAY cả khi chưa đăng nhập; chỉ khi có các cookie phiên đăng nhập
/// (SPC_EC/SPC_ST/SPC_U) mới coi là đã đăng nhập — tránh lưu đè cookie hợp lệ cũ bằng cookie rác.
/// </summary>
public static class ShopeeLoginCookies
{
    // Tên cookie báo hiệu đã đăng nhập Shopee (khác các cookie theo dõi tiền-đăng-nhập).
    private static readonly string[] LoginCookieNames = { "SPC_EC", "SPC_ST", "SPC_U" };

    /// <summary>True nếu chuỗi JSON cookie chứa ít nhất một cookie đăng nhập Shopee có giá trị.</summary>
    public static bool IsLoggedIn(string? cookieJson) => IsLoggedIn(CookieJson.Deserialize(cookieJson));

    /// <summary>True nếu danh sách cookie chứa ít nhất một cookie đăng nhập Shopee có giá trị.</summary>
    public static bool IsLoggedIn(IEnumerable<StoredCookie> cookies) =>
        cookies.Any(c => !string.IsNullOrEmpty(c.Value) &&
            LoginCookieNames.Any(n => string.Equals(n, c.Name, StringComparison.OrdinalIgnoreCase)));
}
