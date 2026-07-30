using Shopee.Core.Cdp;

namespace Shopee.Core.BigSeller;

// Partial của BigSellerCookieEngine: nạp cookie file → browser qua CẢ HAI transport CDP (CdpSession port-based
// và CdpClient WebSocket dùng-một-lần) + ghi NGƯỢC cookie sống từ browser ra file. Pure move.
public static partial class BigSellerCookieEngine
{
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
            var navigated = false;
            foreach (var target in await CdpClient.ListTargetsAsync(client.Port).ConfigureAwait(false))
            {
                if (!target.IsPage || !IsBigSellerUrl(target.Url) || !target.HasWsUrl)
                    continue;

                using var page = new ClientWebSocket();
                await page.ConnectAsync(new Uri(target.WsUrl!), CancellationToken.None).ConfigureAwait(false);
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
