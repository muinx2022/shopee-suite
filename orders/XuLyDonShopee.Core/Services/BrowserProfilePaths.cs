namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Tính đường dẫn thư mục hồ sơ (user-data-dir) persistent của trình duyệt cho từng tài khoản.
/// Hàm thuần (không IO) nên test được dễ dàng.
/// </summary>
public static class BrowserProfilePaths
{
    /// <summary>
    /// Thư mục user-data-dir persistent RIÊNG cho một tài khoản trên một trình duyệt cụ thể, nằm trong
    /// &lt;baseDir&gt;/profiles/&lt;id&gt;-&lt;browserKind&gt; (vd <c>profiles/12-chrome</c>, <c>profiles/12-brave</c>).
    /// Tách theo <paramref name="browserKind"/> để mỗi trình duyệt giữ một hồ sơ/fingerprint riêng — đổi
    /// trình duyệt = phiên sạch, đăng nhập lại. <paramref name="browserKind"/> được chuẩn hóa (trim +
    /// lowercase) cho an toàn tên thư mục.
    /// </summary>
    public static string ForAccount(string baseDir, long accountId, string browserKind)
    {
        var kind = (browserKind ?? string.Empty).Trim().ToLowerInvariant();
        return System.IO.Path.Combine(baseDir, "profiles",
            $"{accountId.ToString(System.Globalization.CultureInfo.InvariantCulture)}-{kind}");
    }
}
