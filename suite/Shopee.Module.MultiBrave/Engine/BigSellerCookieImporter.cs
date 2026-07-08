using System.Net.WebSockets;
using System.Text.Json;
using Shopee.Core.Cdp;

namespace OpenMultiBraveLauncherV3;

internal static class BigSellerCookieImporter
{
    public const string DefaultListingUrl =
        "https://www.bigseller.com/web/listing/shopee/index.htm?bsStatus=1";

    /// <summary>Cookie giữ phiên đăng nhập BigSeller — còn giá trị nghĩa là browser đang có phiên sống.</summary>
    public const string AuthCookieName = "muc_token";

    public static bool IsBigSellerCookie(Dictionary<string, object?> cookie) =>
        (cookie.GetValueOrDefault("domain") as string ?? "")
            .Contains("bigseller", StringComparison.OrdinalIgnoreCase);

    public static bool HasAuthCookie(IEnumerable<Dictionary<string, object?>> cookies) =>
        cookies.Any(c =>
            IsBigSellerCookie(c) &&
            string.Equals(c.GetValueOrDefault("name") as string, AuthCookieName, StringComparison.OrdinalIgnoreCase) &&
            (c.GetValueOrDefault("value") as string ?? "").Length > 5);

    public static async Task<List<Dictionary<string, object?>>> GetBigSellerCookiesAsync(int debugPort)
    {
        var all = await GetAllCookiesFromBrowserAsync(debugPort).ConfigureAwait(false);
        return all.Where(IsBigSellerCookie).ToList();
    }

    /// <summary>Kiểm tra browser (qua CDP) đã CÓ cookie đăng nhập BigSeller (muc_token) chưa — để xác nhận import thành công.</summary>
    public static async Task<bool> HasAuthCookieInBrowserAsync(int debugPort)
    {
        try
        {
            var cookies = await GetBigSellerCookiesAsync(debugPort).ConfigureAwait(false);
            return HasAuthCookie(cookies);
        }
        catch { return false; }
    }

    /// <summary>
    /// Chuỗi CHẨN ĐOÁN muc_token đang có trong browser: giá trị rút gọn (để so token có ĐỔI không) +
    /// hạn dùng (để biết HẾT HẠN không). Dùng để trả lời "login first thì token MẤT ĐI ĐÂU":
    ///  • "(không có muc_token)" → token bị mất/clobber/chưa import (lỗi phía client).
    ///  • có token + còn hạn nhưng server vẫn báo login-first → server ĐÁ phiên (lỗi phía server: nhiều phiên/IP).
    ///  • có token nhưng hết hạn → token GIÀ đi (cần refresh/ghi ngược token mới).
    /// </summary>
    public static async Task<string> GetAuthCookieDebugAsync(int debugPort)
    {
        try
        {
            var cookies = await GetBigSellerCookiesAsync(debugPort).ConfigureAwait(false);
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
                    var left = exp - DateTimeOffset.Now;
                    expStr = $"{exp.LocalDateTime:dd/MM HH:mm} (còn {left.TotalHours:0.0}h)";
                }
            }
            return $"muc_token={prefix}…(len {val.Length}) hạn={expStr}";
        }
        catch (Exception ex) { return $"(lỗi đọc token: {ex.Message})"; }
    }

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

    /// <summary>muc_token ĐANG có trong BROWSER (qua CDP). null nếu chưa có. Dùng để quyết định có nên
    /// nạp đè token từ file không (đừng đè token server vừa xoay = giết phiên → "log in first").</summary>
    public static async Task<AuthTokenInfo?> GetBrowserAuthTokenInfoAsync(int debugPort)
    {
        try
        {
            var cookies = await GetBigSellerCookiesAsync(debugPort).ConfigureAwait(false);
            var c = cookies.FirstOrDefault(x =>
                string.Equals(x.GetValueOrDefault("name") as string, AuthCookieName, StringComparison.OrdinalIgnoreCase));
            return ToAuthTokenInfo(c);
        }
        catch { return null; }
    }

    /// <summary>muc_token trong FILE cookie (đọc trực tiếp file, không qua browser). null nếu thiếu.</summary>
    public static AuthTokenInfo? GetFileAuthTokenInfo(string cookieFile)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cookieFile) || !File.Exists(cookieFile)) return null;
            var json = File.ReadAllText(cookieFile);
            using var doc = JsonDocument.Parse(json);
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
                new { exportedAt = DateTimeOffset.Now, cookies = bigSellerCookies },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tmp, json);

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tmp, cookieFile, overwrite: true);
                    return true;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(150);
                }
            }
        }
        catch (Exception ex)
        {
            try { File.Delete(tmp); } catch { }
            log?.Invoke($"BigSeller cookie: không lưu được ra file: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Import BigSeller cookie từ file vào browser đang chạy qua CDP.
    /// KHÔNG dùng proxy của instance — CDP là kết nối local đến browser.
    /// </summary>
    public static async Task<int> ImportFromFileAsync(
        int debugPort,
        string cookieFile,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
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

        var json = await File.ReadAllTextAsync(cookieFile, cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var cookiesEl = doc.RootElement.TryGetProperty("cookies", out var cp) ? cp : doc.RootElement;
        if (cookiesEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("File cookie BigSeller không hợp lệ (mảng cookies không tìm thấy).");

        log?.Invoke("Đang nạp cookie BigSeller vào browser...");
        var count = await SetBigSellerCookiesToBraveAsync(debugPort, cookiesEl, log).ConfigureAwait(false);
        log?.Invoke($"BigSeller: đã import {count} cookie.");
        return count;
    }

    private static async Task<List<Dictionary<string, object?>>> GetAllCookiesFromBrowserAsync(int debugPort)
    {
        var wsUrl = await new CdpClient(debugPort).GetBrowserWebSocketUrlAsync().ConfigureAwait(false);
        using var browser = new ClientWebSocket();
        await browser.ConnectAsync(new Uri(wsUrl), CancellationToken.None).ConfigureAwait(false);
        var result = await CdpClient.SendAsync(browser, 1, "Storage.getCookies", null).ConfigureAwait(false);
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
            list.Add(map);
        }
        return list;
    }

    private static async Task<int> SetBigSellerCookiesToBraveAsync(
        int debugPort,
        JsonElement cookiesArray,
        Action<string>? log)
    {
        var wsUrl = await new CdpClient(debugPort).GetPageWebSocketUrlAsync().ConfigureAwait(false);
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(wsUrl), CancellationToken.None).ConfigureAwait(false);
        await CdpClient.SendAsync(socket, 1, "Network.enable", new { }).ConfigureAwait(false);

        var attempted = 0;
        var succeeded = 0;
        var cmdId = 2000;

        foreach (var cookie in cookiesArray.EnumerateArray())
        {
            if (cookie.ValueKind != JsonValueKind.Object)
                continue;

            var domain = cookie.TryGetProperty("domain", out var dp) ? (dp.GetString() ?? "") : "";
            if (!domain.Contains("bigseller", StringComparison.OrdinalIgnoreCase))
                continue;

            var payload = BuildCookiePayload(cookie);
            if (payload is null)
                continue;

            attempted++;
            try
            {
                await TrySetCookieWithBrowserStorageAsync(debugPort, payload).ConfigureAwait(false);

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

                // Copy sang bigseller.pro cho compatibility
                if (TryBuildProPayload(payload, out var proPayload))
                {
                    await TrySetCookieWithBrowserStorageAsync(debugPort, proPayload).ConfigureAwait(false);
                    try { await CdpClient.SendAsync(socket, cmdId++, "Network.setCookie", proPayload).ConfigureAwait(false); } catch { }
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
            if (!cookie.TryGetProperty(k, out var v))
                continue;
            payload[k] = v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.TryGetInt64(out var i) ? i : v.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
        }

        if (!payload.ContainsKey("name") || !payload.ContainsKey("value"))
            return null;

        if (!payload.ContainsKey("url") && payload.TryGetValue("domain", out var dv))
        {
            var ds = (dv as string ?? "").TrimStart('.');
            if (!string.IsNullOrEmpty(ds))
                payload["url"] = $"https://{ds}/";
        }

        if (!payload.ContainsKey("url") && !payload.ContainsKey("domain"))
            return null;

        CookieCdpWriter.SanitizeCookiePayloadForCdp(payload, persistSessionCookie: true);
        return payload;
    }

    private static bool TryBuildProPayload(
        Dictionary<string, object?> source,
        out Dictionary<string, object?> payload)
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

    private static async Task<bool> TrySetCookieWithBrowserStorageAsync(int debugPort, Dictionary<string, object?> payload)
    {
        try
        {
            using var browser = new ClientWebSocket();
            await browser.ConnectAsync(
                new Uri(await new CdpClient(debugPort).GetBrowserWebSocketUrlAsync().ConfigureAwait(false)),
                CancellationToken.None).ConfigureAwait(false);
            await CdpClient.SendAsync(browser, 700, "Storage.setCookies", new { cookies = new[] { payload } })
                .ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }
}
