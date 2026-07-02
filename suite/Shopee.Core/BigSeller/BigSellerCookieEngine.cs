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
/// </summary>
public static class BigSellerCookieEngine
{
    public const string AuthCookieName = "muc_token";

    public const string DefaultListingUrl =
        "https://www.bigseller.com/web/listing/shopee/index.htm?bsStatus=1";

    private static readonly JsonSerializerOptions FileJsonOpts = new() { WriteIndented = true };

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

    /// <summary>muc_token trong FILE cookie (đọc trực tiếp file). null nếu thiếu.</summary>
    public static AuthTokenInfo? GetFileAuthTokenInfo(string cookieFile)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cookieFile) || !File.Exists(cookieFile)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(cookieFile));
            var cookiesEl = doc.RootElement.TryGetProperty("cookies", out var cp) ? cp : doc.RootElement;
            if (cookiesEl.ValueKind != JsonValueKind.Array) return null;
            foreach (var ck in cookiesEl.EnumerateArray())
            {
                if (ck.ValueKind != JsonValueKind.Object) continue;
                var name = ck.TryGetProperty("name", out var np) ? np.GetString() : null;
                if (!string.Equals(name, AuthCookieName, StringComparison.OrdinalIgnoreCase)) continue;
                var domain = ck.TryGetProperty("domain", out var dp) ? (dp.GetString() ?? "") : "";
                if (!domain.Contains("bigseller", StringComparison.OrdinalIgnoreCase)) continue;
                var map = new Dictionary<string, object?>();
                if (ck.TryGetProperty("value", out var vp)) map["value"] = vp.GetString();
                if (ck.TryGetProperty("expires", out var ep) && ep.ValueKind == JsonValueKind.Number)
                    map["expires"] = ep.TryGetInt64(out var l) ? l : ep.GetDouble();
                return ToAuthTokenInfo(map);
            }
            return null;
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

    // ──────────────────────────────────────────────────────────────────────────────
    //  Ghi cookie ra file
    // ──────────────────────────────────────────────────────────────────────────────

    public static bool TryWriteCookieFile(
        string cookieFile,
        IReadOnlyCollection<Dictionary<string, object?>> bigSellerCookies,
        Action<string>? log = null)
    {
        // Tên tmp unique để tránh race khi nhiều instance write đồng thời.
        var tmp = $"{cookieFile}.{Environment.ProcessId}-{Guid.NewGuid():N}.tmp";
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(cookieFile));
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(
                new { exportedAt = DateTimeOffset.Now, cookies = bigSellerCookies }, FileJsonOpts);
            File.WriteAllText(tmp, json);

            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(tmp, cookieFile, overwrite: true); return true; }
                catch (IOException) when (attempt < 4) { Thread.Sleep(150); }
            }
        }
        catch (Exception ex)
        {
            try { File.Delete(tmp); } catch { }
            log?.Invoke($"BigSeller cookie: không lưu được ra file: {ex.Message}");
            return false;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Import cookie từ file vào browser (qua CDP)
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Import RAW: nạp mọi cookie BigSeller trong file vào browser (KHÔNG kiểm tra token sống —
    /// dùng <see cref="ImportKeepingLiveTokenAsync"/> cho luồng bình thường để khỏi đè token sống).</summary>
    public static async Task<int> ImportFromFileAsync(
        int cdpPort, string cookieFile, Action<string>? log = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cookieFile))
        {
            log?.Invoke("Account chưa cấu hình BigSeller cookie file — bỏ qua.");
            return 0;
        }
        if (!File.Exists(cookieFile))
        {
            log?.Invoke($"BigSeller cookie file không tìm thấy: {cookieFile}");
            return 0;
        }

        var json = await File.ReadAllTextAsync(cookieFile, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var cookiesEl = doc.RootElement.TryGetProperty("cookies", out var cp) ? cp : doc.RootElement;
        if (cookiesEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("File cookie BigSeller không hợp lệ (mảng cookies không tìm thấy).");

        log?.Invoke("Đang nạp cookie BigSeller vào browser…");
        var count = await SetBigSellerCookiesToBrowserAsync(cdpPort, cookiesEl, log, ct).ConfigureAwait(false);
        log?.Invoke($"BigSeller: đã import {count} cookie.");
        return count;
    }

    private static async Task<int> SetBigSellerCookiesToBrowserAsync(
        int cdpPort, JsonElement cookiesArray, Action<string>? log, CancellationToken ct)
    {
        await using var s = await CdpSession.ConnectToBrowserAsync(cdpPort, ct).ConfigureAwait(false);
        var succeeded = 0;

        foreach (var cookie in cookiesArray.EnumerateArray())
        {
            if (cookie.ValueKind != JsonValueKind.Object) continue;

            var domain = cookie.TryGetProperty("domain", out var dp) ? (dp.GetString() ?? "") : "";
            if (!domain.Contains("bigseller", StringComparison.OrdinalIgnoreCase)) continue;

            var payload = BuildCookiePayload(cookie);
            if (payload is null) continue;

            try
            {
                // Storage.setCookies cấp browser = API cookie-store thẩm quyền nhất (set cho mọi domain/path).
                await s.SendAsync("Storage.setCookies", new { cookies = new[] { payload } }, ct).ConfigureAwait(false);
                succeeded++;

                // Copy sang bigseller.pro cho tương thích (best-effort).
                if (TryBuildProPayload(payload, out var proPayload))
                    try { await s.SendAsync("Storage.setCookies", new { cookies = new[] { proPayload } }, ct).ConfigureAwait(false); } catch { }
            }
            catch (Exception ex)
            {
                var name = payload.TryGetValue("name", out var nv) ? nv as string ?? "" : "";
                log?.Invoke($"Cookie {name}: {ex.Message}");
            }
        }
        return succeeded;
    }

    private static Dictionary<string, object?>? BuildCookiePayload(JsonElement cookie)
    {
        var payload = new Dictionary<string, object?>();
        foreach (var k in new[]
        {
            "name", "value", "url", "domain", "path",
            "secure", "httpOnly", "sameSite", "expires",
            "priority", "sourceScheme", "sourcePort",
        })
        {
            if (!cookie.TryGetProperty(k, out var v)) continue;
            payload[k] = v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.TryGetInt64(out var i) ? i : v.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
        }

        if (!payload.ContainsKey("name") || !payload.ContainsKey("value")) return null;

        if (!payload.ContainsKey("url") && payload.TryGetValue("domain", out var dv))
        {
            var ds = (dv as string ?? "").TrimStart('.');
            if (!string.IsNullOrEmpty(ds)) payload["url"] = $"https://{ds}/";
        }
        if (!payload.ContainsKey("url") && !payload.ContainsKey("domain")) return null;

        SanitizeCookiePayloadForCdp(payload, persistSessionCookie: true);
        return payload;
    }

    private static bool TryBuildProPayload(Dictionary<string, object?> source, out Dictionary<string, object?> payload)
    {
        payload = new Dictionary<string, object?>(source);
        var changed = false;
        if (payload.TryGetValue("domain", out var d) && d is string domain &&
            domain.Contains("bigseller.com", StringComparison.OrdinalIgnoreCase))
        {
            payload["domain"] = domain.Replace("bigseller.com", "bigseller.pro", StringComparison.OrdinalIgnoreCase);
            changed = true;
        }
        if (payload.TryGetValue("url", out var u) && u is string url &&
            url.Contains("bigseller.com", StringComparison.OrdinalIgnoreCase))
        {
            payload["url"] = url.Replace("bigseller.com", "bigseller.pro", StringComparison.OrdinalIgnoreCase);
            changed = true;
        }
        return changed;
    }

    /// <summary>Chuẩn hoá payload cookie cho CDP (bỏ field rỗng/null, chuẩn sameSite, persist session cookie
    /// 30 ngày để token sống qua lần mở sau, lọc sourcePort âm). Port nguyên từ CookieCdpWriter của Scrape.</summary>
    private static void SanitizeCookiePayloadForCdp(Dictionary<string, object?> payload, bool persistSessionCookie)
    {
        foreach (var key in payload.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList())
            payload.Remove(key);

        foreach (var key in new[] { "name", "value", "url", "domain", "path", "sameSite", "priority", "sourceScheme" })
        {
            if (payload.TryGetValue(key, out var value) && value is string str && string.IsNullOrWhiteSpace(str))
                payload.Remove(key);
        }

        if (payload.TryGetValue("sameSite", out var sameSite) && sameSite is string ss)
        {
            var normalized = ss.Trim();
            if (normalized.Equals("no_restriction", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("none", StringComparison.OrdinalIgnoreCase))
                payload["sameSite"] = "None";
            else if (normalized.Equals("lax", StringComparison.OrdinalIgnoreCase))
                payload["sameSite"] = "Lax";
            else if (normalized.Equals("strict", StringComparison.OrdinalIgnoreCase))
                payload["sameSite"] = "Strict";
            else
                payload.Remove("sameSite");
        }

        if (payload.TryGetValue("expires", out var expires))
        {
            var value = expires switch { long l => l, int i => i, double d => d, _ => 0 };
            if (value <= 0)
            {
                if (persistSessionCookie) payload["expires"] = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
                else payload.Remove("expires");
            }
        }
        else if (persistSessionCookie)
        {
            payload["expires"] = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
        }

        if (payload.TryGetValue("sourcePort", out var sourcePort))
        {
            var value = sourcePort switch { long l => l, int i => i, double d => d, _ => 0 };
            if (value < 0) payload.Remove("sourcePort");
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  CHÍNH SÁCH GIỮ PHIÊN (bí quyết dùng chung cho Scrape + Update)
    // ──────────────────────────────────────────────────────────────────────────────

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

    /// <summary>Ghi NGƯỢC token sống (server vừa xoay) từ browser trở lại file — chỉ ghi khi token còn sống.
    /// Gọi sau MỖI thao tác thành công để lần mở sau dùng token tươi, tránh "dùng lại token thiu → bị đá".</summary>
    public static async Task WriteBackLiveTokenAsync(
        int cdpPort, string cookieFile, Action<string>? log = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cookieFile)) return;
        try
        {
            var cookies = await GetBigSellerCookiesAsync(cdpPort, ct).ConfigureAwait(false);
            if (!HasAuthCookie(cookies)) return;   // token không sống → đừng ghi đè file bằng rác
            TryWriteCookieFile(cookieFile, cookies, log);
        }
        catch { /* best-effort */ }
    }
}
