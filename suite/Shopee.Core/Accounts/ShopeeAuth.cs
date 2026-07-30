namespace Shopee.Core.Accounts;

/// <summary>Nới lỏng ngữ nghĩa parse cho từng module — mỗi module trước khi gộp có một bộ ràng buộc riêng.</summary>
/// <param name="CookieOptional">"user|pass" là đủ, phần cookie SPC_F có thể thiếu/hỏng (Check tài khoản).</param>
/// <param name="PasswordOptional">Password được phép rỗng (Search — chỉ cần cookie để nhận diện phiên).</param>
public readonly record struct ShopeeLoginLineOptions(bool CookieOptional, bool PasswordOptional)
{
    /// <summary>Scrape (MultiBrave): phải đủ username + password + cookie SPC_F hợp lệ.</summary>
    public static ShopeeLoginLineOptions Strict => new(false, false);

    /// <summary>Search: cookie vẫn bắt buộc nhưng password được phép rỗng.</summary>
    public static ShopeeLoginLineOptions AllowEmptyPassword => new(false, true);

    /// <summary>Check tài khoản: cookie tuỳ chọn ("user|pass" là đủ để mở form login).</summary>
    public static ShopeeLoginLineOptions AllowMissingCookie => new(true, false);
}

/// <summary>Kết quả parse một dòng tài khoản. <see cref="Error"/> rỗng nghĩa là hợp lệ.</summary>
public readonly record struct ShopeeLoginLine(
    string Username,
    string Password,
    string CookieDomain,
    string SpcF,
    string Error)
{
    public bool Ok => string.IsNullOrEmpty(Error);

    internal static ShopeeLoginLine Fail(string error) => new("", "", "", "", error);
}

/// <summary>
/// Ngữ nghĩa dùng chung của bộ đăng nhập Shopee: parse dòng tài khoản, nhận biết cookie phiên,
/// dựng cookie SPC_F, hằng URL trang đăng nhập. Trước đây Scrape / Search / Check tài khoản mỗi
/// module giữ một bản riêng và ba bản LỆCH nhau (rơi phần cookie sau dấu '|', khác điều kiện
/// bắt buộc…); gộp về đây để chỉ còn một ngữ nghĩa duy nhất.
/// </summary>
public static class ShopeeAuth
{
    /// <summary>Cookie "thiết bị" nạp TRƯỚC khi tải form login — giảm captcha.</summary>
    public const string SpcFCookie = "SPC_F";

    /// <summary>Hai cookie PHIÊN: có giá trị thật nghĩa là đã đăng nhập.</summary>
    public const string SpcStCookie = "SPC_ST";
    public const string SpcEcCookie = "SPC_EC";

    public const string DefaultCookieDomain = ".shopee.vn";

    /// <summary>Trang đăng nhập người mua (giống hệt nhau ở cả ba module trước khi gộp).</summary>
    public const string LoginUrl = "https://shopee.vn/buyer/login?next=https%3A%2F%2Fshopee.vn";

    /// <summary>Tiền tố URL để nhận ra tab đang ở trang đăng nhập.</summary>
    public const string LoginUrlPrefix = "https://shopee.vn/buyer/login";

    /// <summary>
    /// Parse dòng "username|password|.shopee.vn=SPC_F=value". Phần cookie được GHÉP LẠI từ mọi
    /// đoạn sau dấu '|' thứ hai nên giá trị cookie chứa '|' không bị rơi. Tên cookie không bắt buộc
    /// là SPC_F: có tiền tố "SPC_F=" thì lấy phần sau, không thì lấy phần sau dấu '=' kế tiếp.
    /// </summary>
    public static ShopeeLoginLine ParseLoginLine(string? line, ShopeeLoginLineOptions options)
    {
        var parts = (line ?? "").Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length < (options.CookieOptional ? 2 : 3))
            return ShopeeLoginLine.Fail("Dinh dang Shopee account phai la username|password|.shopee.vn=SPC_F=...");

        var username = parts[0];
        var password = parts[1];
        if (string.IsNullOrWhiteSpace(username) ||
            (string.IsNullOrWhiteSpace(password) && !options.PasswordOptional))
            return ShopeeLoginLine.Fail("Username, password, SPC_F khong duoc de trong.");

        // Ghép lại parts[2..] — giá trị cookie có thể chứa chính dấu '|'.
        var cookiePart = parts.Length >= 3 ? string.Join('|', parts.Skip(2)).Trim() : "";
        if (string.IsNullOrWhiteSpace(cookiePart))
            return options.CookieOptional
                ? new ShopeeLoginLine(username, password, "", "", "")
                : ShopeeLoginLine.Fail("Username, password, SPC_F khong duoc de trong.");

        // ".shopee.vn=SPC_F=abc" → domain là phần trước dấu '=' đầu tiên, còn lại là "TÊN=giá trị".
        var eq = cookiePart.IndexOf('=', StringComparison.Ordinal);
        if (eq <= 0)
            return options.CookieOptional
                ? new ShopeeLoginLine(username, password, "", "", "")
                : ShopeeLoginLine.Fail("Cookie SPC_F khong hop le.");

        var domain = cookiePart[..eq].Trim();
        var rest = cookiePart[(eq + 1)..].Trim();

        const string prefix = SpcFCookie + "=";
        string spcF;
        if (rest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            spcF = rest[prefix.Length..].Trim();
        }
        else
        {
            var nameEq = rest.IndexOf('=', StringComparison.Ordinal);
            if (nameEq >= 0)
                spcF = rest[(nameEq + 1)..].Trim();      // tên cookie khác SPC_F → vẫn lấy giá trị
            else if (options.CookieOptional)
                spcF = rest;                             // không có tên cookie → coi cả phần còn lại là giá trị
            else
                return ShopeeLoginLine.Fail("Cookie phai co dang .shopee.vn=SPC_F=...");
        }

        if (!options.CookieOptional &&
            (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(spcF)))
            return ShopeeLoginLine.Fail("Domain hoac gia tri SPC_F khong hop le.");

        return new ShopeeLoginLine(username, password, domain, spcF, "");
    }

    /// <summary>
    /// Cookie này có chứng minh phiên Shopee còn sống không (SPC_ST / SPC_EC trên domain shopee,
    /// giá trị dài hơn 5 ký tự và khác "-"). Dùng chung cho cả ba module.
    /// </summary>
    public static bool IsSessionCookie(string domain, string name, string value) =>
        domain.Contains("shopee", StringComparison.OrdinalIgnoreCase)
        && (name.Equals(SpcStCookie, StringComparison.OrdinalIgnoreCase)
            || name.Equals(SpcEcCookie, StringComparison.OrdinalIgnoreCase))
        && value.Length > 5
        && value != "-";

    /// <summary>
    /// Payload cookie SPC_F hạn 30 ngày. Dùng được cho cả <c>Network.setCookie</c> (Search, Check
    /// tài khoản — payload chính là params) lẫn <c>Storage.setCookies</c> (Scrape — payload nằm
    /// trong mảng <c>cookies</c>).
    /// </summary>
    public static Dictionary<string, object?> BuildSpcFCookie(string? domain, string spcF) => new()
    {
        ["name"] = SpcFCookie,
        ["value"] = spcF,
        ["domain"] = string.IsNullOrWhiteSpace(domain) ? DefaultCookieDomain : domain,
        ["path"] = "/",
        ["secure"] = true,
        ["httpOnly"] = false,
        ["sameSite"] = "Lax",
        ["expires"] = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds(),
    };
}
