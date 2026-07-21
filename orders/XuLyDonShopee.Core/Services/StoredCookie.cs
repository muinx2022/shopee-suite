using System.Text.Encodings.Web;
using System.Text.Json;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Một cookie đã bắt được từ phiên đăng nhập, giữ đủ trường để sau này nạp lại
/// bằng <c>AddCookiesAsync</c> ở bước "chạy tài khoản".
/// </summary>
public sealed record StoredCookie(
    string Name,
    string Value,
    string Domain,
    string Path,
    double Expires,
    bool HttpOnly,
    bool Secure,
    string? SameSite);

/// <summary>
/// Tiện ích serialize/deserialize danh sách <see cref="StoredCookie"/> ra/vào JSON
/// (định dạng có thụt lề để người dùng đọc được trong ô Cookie của form).
/// </summary>
public static class CookieJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // Cho phép ký tự thường gặp trong cookie hiển thị nguyên bản (dễ đọc).
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Chuyển danh sách cookie thành chuỗi JSON có thụt lề.</summary>
    public static string Serialize(IEnumerable<StoredCookie> cookies)
        => JsonSerializer.Serialize(cookies.ToList(), Options);

    /// <summary>
    /// Parse chuỗi JSON thành danh sách cookie. An toàn: null/chuỗi rỗng/JSON hỏng
    /// đều trả về danh sách rỗng thay vì ném lỗi.
    /// </summary>
    public static List<StoredCookie> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<StoredCookie>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<StoredCookie>>(json) ?? new List<StoredCookie>();
        }
        catch
        {
            return new List<StoredCookie>();
        }
    }
}
