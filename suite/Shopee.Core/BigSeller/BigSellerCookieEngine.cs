using System.Text.Json;
using Shopee.Core.Cdp;

namespace Shopee.Core.BigSeller;

/// <summary>
/// Engine DÙNG CHUNG quản lý cookie + phiên đăng nhập BigSeller cho cả Shopee Scrape lẫn BigSeller Update.
/// Tự chứa: nói CDP qua <see cref="CdpSession"/> của Core (port-based) nên KHÔNG phụ thuộc CdpClient riêng
/// của từng module. Gói trọn "bí quyết" giữ phiên mà trước đây chỉ Scrape có:
///  • <see cref="ImportKeepingLiveTokenAsync"/> — KHÔNG đè muc_token đang sống trong browser bằng token cũ
///    từ file (server xoay token liên tục; đè token cũ = server đá phiên = "log in first").
///  • <see cref="WriteBackLiveTokenAsync"/> — ghi NGƯỢC token (server vừa xoay) trở lại file sau mỗi lần
///    thành công, để lần mở sau dùng token tươi thay vì token thiu.
/// muc_token = cookie giữ phiên BigSeller. Xem ghi chú dự án [[bigseller-single-session]].
/// <para>Chia làm 4 file cùng class (partial): file này = hằng + predicate + đọc cookie từ browser;
/// <c>.CookieFile.cs</c> = đọc/ghi FILE cookie (atomic); <c>.Importer.cs</c> = nạp file → browser qua 2
/// transport CDP + ghi ngược ra file; <c>.SessionPolicy.cs</c> = luật giữ token sống / so tuổi token.</para>
/// </summary>
public static partial class BigSellerCookieEngine
{
    public const string AuthCookieName = "muc_token";

    public const string DefaultListingUrl =
        "https://www.bigseller.com/web/listing/shopee/index.htm?bsStatus=1";

    // ──────────────────────────────────────────────────────────────────────────────
    //  Predicate / token-info
    // ──────────────────────────────────────────────────────────────────────────────

    public static bool IsBigSellerCookie(Dictionary<string, object?> cookie) =>
        (cookie.GetValueOrDefault("domain") as string ?? "")
            .Contains("bigseller", StringComparison.OrdinalIgnoreCase);

    public static bool HasAuthCookie(IEnumerable<Dictionary<string, object?>> cookies) =>
        cookies.Any(c =>
            IsBigSellerCookie(c) &&
            string.Equals(c.GetValueOrDefault("name") as string, AuthCookieName, StringComparison.OrdinalIgnoreCase) &&
            (c.GetValueOrDefault("value") as string ?? "").Length > 5);

    /// <summary>muc_token để SO SÁNH "token nào mới hơn": giá trị (so trùng) + hạn (expires). null = không có.</summary>
    public readonly record struct AuthTokenInfo(string Value, DateTimeOffset? Expires);

    private static AuthTokenInfo? ToAuthTokenInfo(Dictionary<string, object?>? c)
    {
        if (c is null) return null;
        var val = c.GetValueOrDefault("value") as string ?? "";
        if (val.Length <= 5) return null;
        DateTimeOffset? exp = null;
        if (c.TryGetValue("expires", out var e) && e is not null)
        {
            double secs = e switch
            {
                long l => l,
                double d => d,
                _ => double.TryParse(e.ToString(), out var p) ? p : -1,
            };
            if (secs > 0) exp = DateTimeOffset.FromUnixTimeSeconds((long)secs);
        }
        return new AuthTokenInfo(val, exp);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Đọc cookie từ browser (qua CDP)
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Toàn bộ cookie BigSeller đang có trong browser (Storage.getCookies cấp browser).</summary>
    public static async Task<List<Dictionary<string, object?>>> GetBigSellerCookiesAsync(
        int cdpPort, CancellationToken ct = default)
    {
        await using var s = await CdpSession.ConnectToBrowserAsync(cdpPort, ct).ConfigureAwait(false);
        var result = await s.SendAsync("Storage.getCookies", null, ct).ConfigureAwait(false);
        if (!result.TryGetProperty("cookies", out var cookiesEl) || cookiesEl.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<Dictionary<string, object?>>();
        foreach (var cookie in cookiesEl.EnumerateArray())
        {
            var map = new Dictionary<string, object?>();
            foreach (var p in cookie.EnumerateObject())
            {
                map[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString(),
                    JsonValueKind.Number => p.Value.TryGetInt64(out var i) ? i : p.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => p.Value.ToString(),
                };
            }
            if (IsBigSellerCookie(map)) list.Add(map);
        }
        return list;
    }

    /// <summary>browser ĐÃ có cookie đăng nhập (muc_token) chưa — để xác nhận import thành công.</summary>
    public static async Task<bool> HasAuthCookieInBrowserAsync(int cdpPort, CancellationToken ct = default)
    {
        try { return HasAuthCookie(await GetBigSellerCookiesAsync(cdpPort, ct).ConfigureAwait(false)); }
        catch { return false; }
    }

    /// <summary>muc_token ĐANG có trong BROWSER. null nếu chưa có. Dùng để QUYẾT ĐỊNH có nên nạp đè token
    /// từ file hay không (đừng đè token server vừa xoay = giết phiên → "log in first").</summary>
    public static async Task<AuthTokenInfo?> GetBrowserAuthTokenInfoAsync(int cdpPort, CancellationToken ct = default)
    {
        try
        {
            var cookies = await GetBigSellerCookiesAsync(cdpPort, ct).ConfigureAwait(false);
            return ToAuthTokenInfo(cookies.FirstOrDefault(x =>
                string.Equals(x.GetValueOrDefault("name") as string, AuthCookieName, StringComparison.OrdinalIgnoreCase)));
        }
        catch { return null; }
    }

    /// <summary>Chuỗi CHẨN ĐOÁN muc_token đang có trong browser (giá trị rút gọn + hạn) để trả lời
    /// "login first thì token mất đi đâu": không có / server đá phiên / token hết hạn.</summary>
    public static async Task<string> GetAuthCookieDebugAsync(int cdpPort, CancellationToken ct = default)
    {
        try
        {
            var cookies = await GetBigSellerCookiesAsync(cdpPort, ct).ConfigureAwait(false);
            var c = cookies.FirstOrDefault(x =>
                string.Equals(x.GetValueOrDefault("name") as string, AuthCookieName, StringComparison.OrdinalIgnoreCase));
            if (c is null) return "(không có muc_token)";

            var val = c.GetValueOrDefault("value") as string ?? "";
            var prefix = val.Length <= 8 ? val : val[..8];
            var expStr = "session (không hạn)";
            if (c.TryGetValue("expires", out var e) && e is not null)
            {
                double secs = e switch
                {
                    long l => l,
                    double d => d,
                    _ => double.TryParse(e.ToString(), out var p) ? p : -1,
                };
                if (secs > 0)
                {
                    var exp = DateTimeOffset.FromUnixTimeSeconds((long)secs);
                    expStr = $"{exp.LocalDateTime:dd/MM HH:mm} (còn {(exp - DateTimeOffset.Now).TotalHours:0.0}h)";
                }
            }
            return $"muc_token={prefix}…(len {val.Length}) hạn={expStr}";
        }
        catch (Exception ex) { return $"(lỗi đọc token: {ex.Message})"; }
    }
}
