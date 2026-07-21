namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Tính đường dẫn thư mục hồ sơ (user-data-dir) persistent của trình duyệt cho từng tài khoản.
/// Hàm thuần (không IO) nên test được dễ dàng.
/// </summary>
public static class BrowserProfilePaths
{
    /// <summary>Thư mục user-data-dir persistent cho một tài khoản, nằm trong &lt;baseDir&gt;/profiles/&lt;id&gt;.</summary>
    public static string ForAccount(string baseDir, long accountId)
        => System.IO.Path.Combine(baseDir, "profiles",
               accountId.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
