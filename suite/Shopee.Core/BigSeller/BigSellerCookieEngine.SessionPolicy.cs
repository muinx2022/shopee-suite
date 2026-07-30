namespace Shopee.Core.BigSeller;

// Partial của BigSellerCookieEngine: CHÍNH SÁCH GIỮ PHIÊN — luật quyết định có nạp đè token từ file không
// (bí quyết dùng chung cho Scrape + Update) + đọc iat của JWT để so "token nào mới hơn". Pure move.
public static partial class BigSellerCookieEngine
{
    /// <summary>Thời điểm PHÁT HÀNH (iat) của muc_token — token là JWT, iat là "tuổi" chính xác để so
    /// "token nào mới hơn" xuyên máy. Thuộc tính expires của cookie bị chuẩn hoá +30 ngày lúc ghi file
    /// nên KHÔNG phản ánh đúng tuổi. null nếu không phải JWT / thiếu iat.</summary>
    public static DateTimeOffset? GetJwtIssuedAt(string? tokenValue)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(tokenValue)) return null;
            var parts = tokenValue.Split('.');
            if (parts.Length < 2) return null;
            var b64 = parts[1].Replace('-', '+').Replace('_', '/');
            b64 = (b64.Length % 4) switch { 2 => b64 + "==", 3 => b64 + "=", _ => b64 };
            using var doc = JsonDocument.Parse(Convert.FromBase64String(b64));
            return doc.RootElement.TryGetProperty("iat", out var iat) && iat.TryGetInt64(out var s)
                ? DateTimeOffset.FromUnixTimeSeconds(s)
                : null;
        }
        catch { return null; }
    }

    /// <summary>Quyết định CÓ nên nạp đè cookie từ file không. true = nên import (browser trống, hoặc file
    /// MỚI HƠN = vừa đăng nhập lại). false = GIỮ token sống trong browser (server vừa xoay), đừng đè token cũ.</summary>
    public static bool ShouldImportFromFile(AuthTokenInfo? browserTok, AuthTokenInfo? fileTok, out string reason)
    {
        if (browserTok is not { } bt)
        {
            reason = "browser chưa có muc_token → seed từ file.";
            return true;
        }
        // Token y hệt → không cần import.
        if (fileTok is { } ft && string.Equals(ft.Value, bt.Value, StringComparison.Ordinal))
        {
            reason = "browser đã có đúng token đó → giữ nguyên.";
            return false;
        }
        // File mới hơn theo hạn (user vừa đăng nhập lại) → import đè.
        var fileNewer = fileTok is { Expires: { } fe } && (bt.Expires is not { } be || fe > be);
        if (fileNewer)
        {
            reason = "token trong file MỚI HƠN browser (có thể vừa đăng nhập lại) → nạp đè để cập nhật.";
            return true;
        }
        reason = "browser đã có muc_token sống (server vừa xoay) — GIỮ phiên, KHÔNG nạp đè token cũ từ file.";
        return false;
    }

    /// <summary>Import GIỮ TOKEN SỐNG: chỉ nạp cookie từ file khi browser chưa có token sống / file mới hơn.
    /// Đây là lá chắn chống "đè token server vừa xoay → log in first". Dùng cho luồng mở/relaunch bình thường.</summary>
    public static async Task ImportKeepingLiveTokenAsync(
        int cdpPort, string cookieFile, Action<string>? log = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cookieFile)) return;

        var browserTok = await GetBrowserAuthTokenInfoAsync(cdpPort, ct).ConfigureAwait(false);
        if (browserTok is not null)
        {
            var fileTok = GetFileAuthTokenInfo(cookieFile);
            if (!ShouldImportFromFile(browserTok, fileTok, out var reason))
            {
                log?.Invoke($"BigSeller: {reason}");
                return;
            }
            log?.Invoke($"BigSeller: {reason}");
        }

        await ImportFromFileAsync(cdpPort, cookieFile, log, ct).ConfigureAwait(false);
    }
}
