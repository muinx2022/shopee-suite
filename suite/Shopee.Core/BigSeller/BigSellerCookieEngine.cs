using System.Net.WebSockets;
using System.Text.Json;
using Shopee.Core.Cdp;
using Shopee.Core.Infrastructure;

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
        => WriteAtomic(cookieFile,
            JsonSerializer.Serialize(new { exportedAt = DateTimeOffset.Now, cookies = bigSellerCookies }, FileJsonOpts),
            log);

    /// <summary>Overload ghi trực tiếp danh sách <see cref="JsonElement"/> (dùng cho login runner vốn giữ
    /// cookie ở dạng JsonElement thô) — CÙNG cơ chế atomic tmp+move, để chỉ có MỘT bản ghi file cookie.</summary>
    public static bool TryWriteCookieFile(
        string cookieFile,
        IReadOnlyCollection<JsonElement> cookies,
        Action<string>? log = null)
        => WriteAtomic(cookieFile,
            JsonSerializer.Serialize(new { exportedAt = DateTimeOffset.Now, cookies }, FileJsonOpts),
            log);

    // Ghi NGUYÊN TỬ: tmp unique (tránh race đa-instance) → File.Move(overwrite) có retry. File cookie này
    // được Hub sync + các importer đọc đồng thời; ghi trực tiếp (WriteAllText/Bytes) sinh torn-read → cookie
    // hỏng lan ra đa máy. Mọi nơi ghi file cookie PHẢI đi qua đây.
    private static bool WriteAtomic(string cookieFile, string json, Action<string>? log)
    {
        var tmp = $"{cookieFile}.{Environment.ProcessId}-{Guid.NewGuid():N}.tmp";
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(cookieFile));
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

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

        log?.Invoke($"Đang nạp cookie BigSeller từ account: {cookieFile}");
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

        // Cookie tiền tố __Host- theo spec cookie-prefix KHÔNG được kèm domain và BUỘC path="/". Giữ lại
        // domain sẽ khiến CDP từ chối set (mất cookie). Bỏ domain (url đã suy ra từ domain ở trên) + ép
        // path="/". Port từ CookieCdpWriter của UpdateProduct — hành vi đang chạy live trước refactor.
        var cookieName = payload.TryGetValue("name", out var nv) ? nv as string ?? "" : "";
        if (cookieName.StartsWith("__Host-", StringComparison.OrdinalIgnoreCase))
        {
            payload.Remove("domain");
            payload["path"] = "/";
        }

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

    // ──────────────────────────────────────────────────────────────────────────────
    //  TRANSPORT CdpClient (port-based, WebSocket dùng-một-lần) — cho module phóng Brave
    // ──────────────────────────────────────────────────────────────────────────────
    //  Các module Scrape (MultiBrave) + Update/Import (UpdateProduct) nạp cookie qua <see cref="CdpClient"/>
    //  (mở/đóng WS theo thao tác) với cơ chế "belt-and-suspenders": Network.setCookie (page) + Storage.setCookies
    //  (browser) + fallback bỏ sourceScheme/sourcePort + copy sang bigseller.pro. GIỮ NGUYÊN hành vi đang chạy
    //  (gộp từ 2 bản BigSellerCookieImporter + CookieCdpWriter của 2 module). KHÁC path CdpSession ở trên (chỉ
    //  Storage.setCookies) — cố ý giữ CẢ HAI transport để không đổi hành vi bản production.

    private static bool IsBigSellerUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Host.Contains("bigseller", StringComparison.OrdinalIgnoreCase);

    private static bool IsLoginUrl(string url) =>
        url.Contains("login", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("passport", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("signin", StringComparison.OrdinalIgnoreCase);

    /// <summary>Import cookie BigSeller từ file vào browser qua CDP (transport CdpClient: Network.setCookie +
    /// Storage.setCookies + copy .pro). <paramref name="navigateUrl"/> != null → điều hướng tab BigSeller tới đó
    /// sau khi nạp; ngược lại nếu <paramref name="reloadBigSellerTabs"/> → reload tab. Dùng chung cho MultiBrave
    /// (không reload/navigate) + UpdateProduct (navigate crawl/listing URL).</summary>
    public static async Task<int> ImportFromFileAsync(
        int cdpPort, string cookieFile, Action<string>? log,
        bool reloadBigSellerTabs, string? navigateUrl, CancellationToken ct = default)
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

        var client = new CdpClient(cdpPort);
        if (!await client.WaitForReadyAsync(cancellationToken: ct).ConfigureAwait(false))
        {
            log?.Invoke($"CDP port {cdpPort} chưa sẵn sàng để import cookie.");
            return 0;
        }

        var cookiesEl = await CookieFileHelper.ParseCookiesRootFromFileAsync(cookieFile, ct).ConfigureAwait(false);
        CookieFileHelper.ValidateCookiesArray(cookiesEl);

        log?.Invoke($"Đang nạp cookie BigSeller từ account: {cookieFile}");
        var count = await SetBigSellerCookiesViaCdpClientAsync(client, cookiesEl, log, ct).ConfigureAwait(false);

        if (count > 0 && !string.IsNullOrWhiteSpace(navigateUrl))
        {
            await NavigateBigSellerTabsAsync(client, navigateUrl!).ConfigureAwait(false);
            log?.Invoke($"Đã điều hướng BigSeller tới: {navigateUrl}");
            await Task.Delay(2000, ct).ConfigureAwait(false);
        }
        else if (count > 0 && reloadBigSellerTabs)
        {
            await client.ReloadPageTargetsAsync(IsBigSellerUrl).ConfigureAwait(false);
            await Task.Delay(2000, ct).ConfigureAwait(false);
        }

        log?.Invoke($"BigSeller: đã import {count} cookie.");
        return count;
    }

    /// <summary>Probe xem tab BigSeller có ĐANG đăng nhập không (điều hướng + poll location.href/readyState):
    /// false = bị đá về trang login / không vào được khu /web/; true = ổn định trong khu app; null = không probe
    /// được (lỗi tạm). Dùng để quyết định có nạp lại cookie từ file hay giữ phiên hiện tại.</summary>
    public static async Task<bool?> ProbeLoggedInAsync(
        int cdpPort, string? probeUrl = null, Action<string>? log = null, CancellationToken ct = default)
    {
        var url = string.IsNullOrWhiteSpace(probeUrl) ? DefaultListingUrl : probeUrl;
        var client = new CdpClient(cdpPort);
        try
        {
            var wsUrl = await client.EnsurePageTargetAsync(IsBigSellerUrl, url).ConfigureAwait(false);
            using var page = new ClientWebSocket();
            await page.ConnectAsync(new Uri(wsUrl), ct).ConfigureAwait(false);
            await CdpClient.SendAsync(page, 60, "Page.navigate", new { url }).ConfigureAwait(false);

            var stableOkPolls = 0;
            for (var i = 0; i < 40; i++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(500, ct).ConfigureAwait(false);

                string href;
                string ready;
                try
                {
                    var result = await CdpClient.SendAsync(page, 61 + i, "Runtime.evaluate", new
                    {
                        expression = "JSON.stringify({href: location.href, ready: document.readyState})",
                        returnByValue = true,
                    }).ConfigureAwait(false);
                    if (!result.TryGetProperty("result", out var rv) || !rv.TryGetProperty("value", out var vv))
                        continue;

                    using var doc = JsonDocument.Parse(vv.GetString() ?? "{}");
                    href = doc.RootElement.TryGetProperty("href", out var h) ? h.GetString() ?? "" : "";
                    ready = doc.RootElement.TryGetProperty("ready", out var r) ? r.GetString() ?? "" : "";
                }
                catch
                {
                    continue;
                }

                if (IsLoginUrl(href))
                    return false;
                if (!string.Equals(ready, "complete", StringComparison.OrdinalIgnoreCase))
                {
                    stableOkPolls = 0;
                    continue;
                }
                if (!href.Contains("/web/", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (++stableOkPolls >= 3)
                    return true;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Cookie: khong probe duoc trang BigSeller: {ex.Message}");
        }
        return null;
    }

    /// <summary>Xuất cookie BigSeller ĐANG có trong browser (profile này) ra file account — chỉ khi còn muc_token
    /// sống. <paramref name="verifySessionAlive"/> → probe thêm để chắc phiên chưa bị server thu hồi trước khi ghi
    /// (tránh ghi đè file bằng token đã chết). Dùng cho lane ghi-cookie của Update/Import.</summary>
    public static async Task<bool> TryExportProfileCookiesToFileAsync(
        int cdpPort, string? cookieFile, Action<string>? log = null, bool verifySessionAlive = false)
    {
        var file = (cookieFile ?? "").Trim();
        if (string.IsNullOrWhiteSpace(file))
            return false;

        try
        {
            var bigseller = await GetBigSellerCookiesAsync(cdpPort).ConfigureAwait(false);
            if (!HasAuthCookie(bigseller))
                return false;

            if (verifySessionAlive &&
                await ProbeLoggedInAsync(cdpPort, log: log).ConfigureAwait(false) != true)
            {
                log?.Invoke("Cookie: phien BigSeller khong con song — bo qua luu cookie ra file.");
                return false;
            }

            if (!TryWriteCookieFile(file, bigseller, log))
                return false;

            log?.Invoke($"Cookie: da luu {bigseller.Count} cookie BigSeller moi vao file account ({Path.GetFileName(file)}).");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Cookie: khong luu duoc cookie BigSeller ra file: {ex.Message}");
            return false;
        }
    }

    // Nạp từng cookie BigSeller: Storage.setCookies (browser) TRƯỚC + Network.setCookie (page) + fallback bỏ
    // sourceScheme/sourcePort nếu chưa ok, rồi copy sang bigseller.pro. Đếm "thành công" khi Network.setCookie ok
    // HOẶC Storage.setCookies ok (bản UpdateProduct — chắc-ăn hơn bản MultiBrave vốn chỉ đếm Network.setCookie;
    // khác biệt CHỈ ở con số trong log, xác nhận phiên vẫn qua HasAuthCookieInBrowser sau đó).
    private static async Task<int> SetBigSellerCookiesViaCdpClientAsync(
        CdpClient client, JsonElement cookiesArray, Action<string>? log, CancellationToken ct)
    {
        var wsUrl = await client.GetPageWebSocketUrlAsync().ConfigureAwait(false);
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(wsUrl), ct).ConfigureAwait(false);
        await CdpClient.SendAsync(socket, 1, "Network.enable", new { }).ConfigureAwait(false);

        var succeeded = 0;
        var cmdId = 1000;

        foreach (var cookie in cookiesArray.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            if (cookie.ValueKind != JsonValueKind.Object)
                continue;

            var domain = cookie.TryGetProperty("domain", out var dp) ? (dp.GetString() ?? "") : "";
            if (!domain.Contains("bigseller", StringComparison.OrdinalIgnoreCase))
                continue;

            var payload = BuildCookiePayload(cookie);
            if (payload is null)
                continue;

            try
            {
                var storageOk = await TrySetCookieWithBrowserStorageAsync(client, payload, ct).ConfigureAwait(false);
                var result = await CdpClient.SendAsync(socket, cmdId++, "Network.setCookie", payload).ConfigureAwait(false);
                var ok = result.TryGetProperty("success", out var sp) && sp.GetBoolean();
                if (!ok)
                {
                    var fb = new Dictionary<string, object?>(payload);
                    fb.Remove("sourceScheme");
                    fb.Remove("sourcePort");
                    var fbResult = await CdpClient.SendAsync(socket, cmdId++, "Network.setCookie", fb).ConfigureAwait(false);
                    ok = fbResult.TryGetProperty("success", out var fp) && fp.GetBoolean();
                }
                if (!ok && storageOk)
                    ok = true;

                // Copy sang bigseller.pro cho tương thích (best-effort).
                if (TryBuildProPayload(payload, out var proPayload))
                {
                    try
                    {
                        await TrySetCookieWithBrowserStorageAsync(client, proPayload, ct).ConfigureAwait(false);
                        var proResult = await CdpClient.SendAsync(socket, cmdId++, "Network.setCookie", proPayload).ConfigureAwait(false);
                        var proOk = proResult.TryGetProperty("success", out var psp) && psp.GetBoolean();
                        if (!proOk)
                        {
                            var fb = new Dictionary<string, object?>(proPayload);
                            fb.Remove("sourceScheme");
                            fb.Remove("sourcePort");
                            try { await CdpClient.SendAsync(socket, cmdId++, "Network.setCookie", fb).ConfigureAwait(false); } catch { }
                        }
                    }
                    catch { /* copy .pro chỉ là best-effort; .com vẫn là bản chính */ }
                }

                if (ok) succeeded++;
            }
            catch (Exception ex)
            {
                var name = payload.TryGetValue("name", out var nv) ? nv as string ?? "" : "";
                log?.Invoke($"Cookie {name}: {ex.Message}");
            }
        }

        return succeeded;
    }

    private static async Task<bool> TrySetCookieWithBrowserStorageAsync(
        CdpClient client, Dictionary<string, object?> payload, CancellationToken ct)
    {
        try
        {
            using var browser = new ClientWebSocket();
            await browser.ConnectAsync(
                new Uri(await client.GetBrowserWebSocketUrlAsync().ConfigureAwait(false)), ct).ConfigureAwait(false);
            await CdpClient.SendAsync(browser, 700, "Storage.setCookies", new { cookies = new[] { payload } }).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    private static async Task NavigateBigSellerTabsAsync(CdpClient client, string targetUrl)
    {
        try
        {
            using var response = await AppServices.DirectHttp
                .GetAsync($"http://127.0.0.1:{client.Port}/json/list").ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var navigated = false;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (!string.Equals(type, "page", StringComparison.OrdinalIgnoreCase))
                    continue;

                var url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                if (!IsBigSellerUrl(url))
                    continue;

                var ws = item.TryGetProperty("webSocketDebuggerUrl", out var wsProp) ? wsProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(ws))
                    continue;

                using var page = new ClientWebSocket();
                await page.ConnectAsync(new Uri(ws), CancellationToken.None).ConfigureAwait(false);
                await CdpClient.SendAsync(page, 92, "Page.navigate", new { url = targetUrl }).ConfigureAwait(false);
                navigated = true;
            }

            if (!navigated)
                await client.EnsurePageTargetAsync(IsBigSellerUrl, targetUrl).ConfigureAwait(false);
        }
        catch
        {
            // navigation is best-effort
        }
    }
}
