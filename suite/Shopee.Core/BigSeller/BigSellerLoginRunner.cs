using Shopee.Core.Browser;
using Shopee.Core.Cdp;

namespace Shopee.Core.BigSeller;

/// <summary>
/// Mở Brave (KHÔNG proxy, IP máy thật) tới trang BigSeller để người dùng tự đăng nhập, rồi poll
/// cookie <c>muc_token</c>. Khi phát hiện đăng nhập, lưu toàn bộ cookie domain bigseller ra file
/// (dùng chung cho scrape + update). Port từ BigSellerLoginRunner của v31, dùng primitives Core.
/// </summary>
public static class BigSellerLoginRunner
{
    public const string ListingUrl = "https://www.bigseller.com/web/listing/shopee/index.htm?bsStatus=1";
    public const string AuthCookieName = "muc_token";

    /// <summary>
    /// Chạy toàn bộ luồng đăng nhập: mở Brave → chờ login → lưu cookie. Trả về true nếu lấy được
    /// cookie. Hủy qua <paramref name="ct"/> (nút Dừng). Tự đóng Brave khi xong.
    /// </summary>
    public static async Task<bool> RunLoginAsync(
        string cookieFile, string profileDir, Action<string> log, CancellationToken ct, Action? onSaved = null,
        string? proxyServer = null)
    {
        if (string.IsNullOrWhiteSpace(cookieFile))
        {
            log("✘ Chưa cấu hình đường dẫn file cookie.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(profileDir))
        {
            log("✘ Thiếu thư mục profile cho tài khoản BigSeller.");
            return false;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cookieFile))!);
        // Profile RIÊNG theo từng tài khoản (caller truyền theo account.Id) → mỗi tk BigSeller có phiên
        // đăng nhập độc lập, KHÔNG dùng chung 1 profile khiến đăng nhập nhầm vào tài khoản cũ.
        Directory.CreateDirectory(profileDir);
        var launcher = new BrowserLauncher(BrowserKind.Brave);
        CdpSession? cdp = null;
        var saved = false;
        try
        {
            // Có proxy riêng cho tk BigSeller → ĐĂNG NHẬP cũng qua proxy đó (cùng IP với lúc scrape, vì
            // scrape route bigseller.com qua proxy này) → token lưu ra khớp IP, tránh bị nghi phiên lạ.
            // Không có proxy → mở IP máy như cũ.
            if (string.IsNullOrWhiteSpace(proxyServer))
                log("Đang mở Brave (IP máy, không proxy) tới BigSeller…");
            else
                log($"Đang mở Brave QUA PROXY {proxyServer} tới BigSeller (khớp IP với scrape)…");
            launcher.Launch(profileDir, proxyServer, ListingUrl);
            var port = launcher.CdpPort;

            // Kết nối cấp BROWSER (không gắn vào 1 page) → đọc cookie không bị đứt khi trang
            // login điều hướng/redirect. Đây là điểm khác bản cũ (page-session chết lúc login).
            cdp = await CdpSession.ConnectToBrowserAsync(port, ct);

            // KHÔNG nạp token cũ từ file vào profile rồi navigate lại nữa! Đó CHÍNH là nguyên nhân mất
            // cookie: nạp muc_token cũ + tải lại trang xác thực BigSeller → server (cơ chế remember/refresh)
            // TÁI CẤP token mới + VÔ HIỆU token cũ server-side; rồi poll thấy token-vừa-nạp tưởng "đã đăng
            // nhập" → ghi đè file bằng token CŨ (đã chết) → lần scrape sau báo "log in BigSeller first".

            // XÓA SẠCH cookie cũ trong profile login TRƯỚC khi đăng nhập → ép đăng nhập THẬT. LÝ DO: profile
            // bền có thể còn muc_token CŨ (đã chết/hết hạn). HasAuthCookie chỉ xét "có cookie dài >5 ký tự",
            // không phân biệt sống/chết → poll thấy token chết cũng báo "✔ thành công" + lưu token CHẾT ra
            // file → lần scrape sau cả 2 Brave của tk đó fail "log in BigSeller first" NGAY (đúng kiểu tk 2,4
            // chết còn 1,3 sống tùy token cũ còn hạn hay không). Xóa sạch → muc_token CHỈ xuất hiện sau khi
            // user đăng nhập thật → token lưu ra file LUÔN là token đang sống. (Đánh đổi: mỗi lần mở phải đăng
            // nhập lại — chấp nhận được để đổi lấy token chắc chắn sống.)
            try
            {
                await cdp.SendAsync("Storage.clearCookies", null, ct);
                var pg = await CdpSession.ConnectToPageAsync(port, ct);
                await pg.SendNoReplyAsync("Page.navigate", new { url = ListingUrl }, ct);
                await pg.DisposeAsync();
                log("Đã xóa phiên cũ — hãy đăng nhập BigSeller để lưu token mới (đảm bảo còn sống).");
            }
            catch { }

            log("Đăng nhập BigSeller trong cửa sổ Brave. Cookie sẽ tự lưu khi phát hiện đăng nhập.");
            log("File cookie: " + cookieFile);
            log("ⓘ Cửa sổ sẽ KHÔNG tự đóng — bấm Dừng khi muốn đóng.");

            var pollsBeforeLogin = 0;
            var lastCookieCount = -1;
            var stablePolls = 0;
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(3000, ct);

                var (cookies, sessionOk) = await GetBigSellerCookiesAsync(cdp, ct);
                if (!sessionOk)
                {
                    // Session đứt → thử kết nối lại NHANH. Nếu endpoint CDP không còn (người dùng
                    // tự đóng Brave) thì reconnect ném timeout → dừng (giữ kết quả đã lưu nếu có).
                    try
                    {
                        await cdp.DisposeAsync();
                        cdp = await CdpSession.ConnectToBrowserAsync(port, ct, waitTimeoutMs: 6000);
                        continue;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch
                    {
                        log("✘ Cửa sổ Brave đã đóng.");
                        return saved;
                    }
                }

                if (HasAuthCookie(cookies))
                {
                    // CHỜ BỘ COOKIE ỔN ĐỊNH rồi mới lưu. LÝ DO: ngay khi muc_token vừa xuất hiện, phiên
                    // BigSeller MỚI có vài cookie (vd ~9), CHƯA đủ (~19). Nếu lưu ngay (như trước) → file
                    // thiếu cookie phiên → scrape báo "log in BigSeller first" dù vừa "đăng nhập thành công".
                    // Đợi số cookie KHÔNG tăng nữa qua 2 vòng (≈6s) = phiên đã nạp đủ → mới lưu BỘ ĐẦY ĐỦ.
                    var n = cookies.Count;
                    if (n == lastCookieCount) stablePolls++;
                    else { stablePolls = 0; lastCookieCount = n; }

                    if (stablePolls >= 2)
                    {
                        // Lưu (và refresh mỗi vòng để file luôn mới). KHÔNG tự đóng cửa sổ, KHÔNG timeout.
                        var wrote = TryWriteCookieFile(cookieFile, cookies, log);
                        if (wrote && !saved)
                        {
                            saved = true;
                            log($"✔ Đăng nhập thành công! Đã lưu {n} cookie (phiên đầy đủ). Bấm Dừng để đóng.");
                            try { onSaved?.Invoke(); } catch { }
                        }
                    }
                    else if (!saved)
                    {
                        log($"Đã đăng nhập — đang chờ phiên nạp đủ cookie ({n})…");
                    }
                    continue;
                }

                if (!saved)
                {
                    pollsBeforeLogin++;
                    if (pollsBeforeLogin >= 200) // ~10 phút chưa đăng nhập → bỏ
                    {
                        log("✘ Không phát hiện đăng nhập (hết thời gian).");
                        return false;
                    }
                    if (pollsBeforeLogin % 10 == 0)
                        log($"Đang chờ đăng nhập… ({pollsBeforeLogin * 3}s)");
                }
            }
            return saved;
        }
        catch (OperationCanceledException)
        {
            // Bấm Dừng → đóng Brave GRACEFUL (CDP Browser.close) để Chromium flush phiên xuống
            // profile → lần đăng nhập sau KHÔNG hiện lại form login. Hard-kill ngay sẽ mất phiên.
            log("■ Đang đóng cửa sổ BigSeller…");
            await CloseBrowserGracefullyAsync(cdp);
            return saved;
        }
        catch (Exception ex)
        {
            log("✘ Lỗi: " + ex.Message);
            return saved;
        }
        finally
        {
            if (cdp is not null) await cdp.DisposeAsync();
            launcher.Kill();
        }
    }

    /// <summary>Đóng Brave bằng CDP <c>Browser.close</c> + chờ flush, để profile giữ phiên đăng nhập.</summary>
    private static async Task CloseBrowserGracefullyAsync(CdpSession? cdp)
    {
        if (cdp is null) return;
        try
        {
            await cdp.SendNoReplyAsync("Browser.close");
            await Task.Delay(2500, CancellationToken.None);
        }
        catch { }
    }

    /// <summary>Nạp cookie đã lưu (file) vào browser qua <c>Storage.setCookies</c> (cấp browser).
    /// Trả về số cookie đã nạp. Dùng để mở login mà đăng-nhập-sẵn (không phải login lại).</summary>
    private static async Task<int> ImportCookiesFromFileAsync(CdpSession cdp, string cookieFile, CancellationToken ct)
    {
        try
        {
            var json = await File.ReadAllTextAsync(cookieFile, ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("cookies", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return 0;

            var list = new List<Dictionary<string, object?>>();
            foreach (var c in arr.EnumerateArray())
            {
                var p = BuildSetCookieParam(c);
                if (p is not null) list.Add(p);
            }
            if (list.Count == 0) return 0;

            await cdp.SendAsync("Storage.setCookies", new { cookies = list }, ct);
            return list.Count;
        }
        catch { return 0; }
    }

    private static Dictionary<string, object?>? BuildSetCookieParam(JsonElement cookie)
    {
        var p = new Dictionary<string, object?>();
        foreach (var k in new[] { "name", "value", "domain", "path", "secure", "httpOnly", "sameSite", "expires" })
        {
            if (!cookie.TryGetProperty(k, out var v)) continue;
            p[k] = v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.TryGetInt64(out var i) ? i : v.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
        }
        if (!p.ContainsKey("name") || !p.ContainsKey("value") || !p.ContainsKey("domain"))
            return null;

        var ds = ((p["domain"] as string) ?? "").TrimStart('.');
        if (!string.IsNullOrEmpty(ds)) p["url"] = $"https://{ds}/";

        // sameSite rỗng → CDP báo lỗi; bỏ. expires<=0 (session cookie) → bỏ để set dạng session.
        if (p.TryGetValue("sameSite", out var ss) && string.IsNullOrEmpty(ss as string)) p.Remove("sameSite");
        if (p.TryGetValue("expires", out var ex) &&
            ((ex is long el && el <= 0) || (ex is double ed && ed <= 0))) p.Remove("expires");

        return p;
    }

    /// <summary>
    /// Đọc cookie domain bigseller qua <c>Storage.getCookies</c> (cấp browser, bền với điều hướng).
    /// Trả về (danh sách cookie, sessionOk). sessionOk=false khi session CDP đứt (cần reconnect).
    /// </summary>
    private static async Task<(List<JsonElement> cookies, bool sessionOk)> GetBigSellerCookiesAsync(
        CdpSession cdp, CancellationToken ct)
    {
        var list = new List<JsonElement>();
        try
        {
            var result = await cdp.SendAsync("Storage.getCookies", null, ct);
            if (result.TryGetProperty("cookies", out var cookies))
                foreach (var c in cookies.EnumerateArray())
                {
                    var domain = c.TryGetProperty("domain", out var d) ? d.GetString() ?? "" : "";
                    if (domain.Contains("bigseller", StringComparison.OrdinalIgnoreCase))
                        list.Add(c.Clone());
                }
            return (list, true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // SendAsync timeout/hủy (session đứt) → báo cần reconnect.
            return (list, false);
        }
    }

    private static bool HasAuthCookie(IEnumerable<JsonElement> cookies) =>
        cookies.Any(c =>
            c.TryGetProperty("name", out var n) && n.GetString() == AuthCookieName &&
            c.TryGetProperty("value", out var v) && (v.GetString()?.Length ?? 0) > 5);

    private static bool TryWriteCookieFile(string cookieFile, IReadOnlyCollection<JsonElement> cookies, Action<string> log)
    {
        try
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("exportedAt", DateTimeOffset.UtcNow.ToString("o"));
                writer.WritePropertyName("cookies");
                writer.WriteStartArray();
                foreach (var c in cookies) c.WriteTo(writer);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            File.WriteAllBytes(cookieFile, stream.ToArray());
            return true;
        }
        catch (Exception ex)
        {
            log("  (không ghi được file cookie: " + ex.Message + ")");
            return false;
        }
    }
}
