using System.Text.Json.Nodes;
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
    /// <param name="tryAutoLogin">Khi phát hiện CHƯA đăng nhập: hàm tự-đăng-nhập (điền form + giải captcha) chạy
    /// TRONG Brave này (nhận cdpPort). Trả true nếu thành công (vòng poll sẽ lưu cookie). null = đợi đăng nhập TAY
    /// như cũ. Truyền từ Suite để Core khỏi phụ thuộc Playwright.</param>
    public static async Task<bool> RunLoginAsync(
        string cookieFile, string profileDir, Action<string> log, CancellationToken ct, Action? onSaved = null,
        string? proxyServer = null, Func<int, CancellationToken, Task<bool>>? tryAutoLogin = null)
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
            // Cho profile login "hành xử như browser thường": TẮT Brave Shields cho bigseller → cookie
            // analytics/tracking (_ga/_fbp/_tt/ttcsid…) nạp được → lưu BỘ COOKIE ĐẦY ĐỦ như v31 (profile
            // mới mặc định Shields BẬT → chặn tracker → chỉ ~9 cookie lõi → phiên trống trải dễ bị đá).
            EnsureBraveShieldsDown(profileDir);
            launcher.Launch(profileDir, proxyServer, ListingUrl);
            var port = launcher.CdpPort;

            // Kết nối cấp BROWSER (không gắn vào 1 page) → đọc cookie không bị đứt khi trang
            // login điều hướng/redirect. Đây là điểm khác bản cũ (page-session chết lúc login).
            cdp = await CdpSession.ConnectToBrowserAsync(port, ct);

            // GIỮ COOKIE qua các lần mở (đúng nhu cầu "login 1 lần → instance nạp cookie → scrape"):
            // profile login là thư mục BỀN nên Chromium tự giữ muc_token qua các phiên. Khi mở lại →
            // vào trang listing rồi PROBE xem CÒN đăng nhập không:
            //  • CÒN SỐNG  → GIỮ phiên, KHÔNG bắt login lại (vòng poll bên dưới sẽ lưu lại token đang sống).
            //  • ĐÃ CHẾT / chưa có → XÓA sạch rồi ép login TƯƠI (tránh lưu nhầm token CHẾT ra file → scrape
            //    báo "log in BigSeller first"). Đây là lý do trước đây phải xóa: HasAuthCookie chỉ xét "có
            //    cookie >5 ký tự", không phân biệt sống/chết. PROBE phân biệt được nên KHÔNG cần xóa khi sống.
            // (Trước đây xóa VÔ ĐIỀU KIỆN mỗi lần mở → lần nào cũng phải đăng nhập lại — chính là lỗi bạn gặp.)
            var aliveKept = false;
            try
            {
                if (await ProbeLoggedInAsync(port, ct))
                {
                    aliveKept = true;
                    log("✔ Phiên BigSeller còn sống — GIỮ cookie, không cần đăng nhập lại. Bấm Dừng để đóng.");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            // CHƯA sống nhưng ĐÃ có file cookie (điển hình: CLIENT vừa SYNC cookie từ Hub về — profile Brave
            // KHÔNG sync giữa máy, chỉ cookie FILE sync) → nạp cookie file vào profile rồi probe LẠI. Nhờ vậy
            // "Mở profile" trên client thấy ĐÃ đăng nhập thay vì hiện form login. Trên máy Hub (profile đã
            // sống) nhánh này không chạy vì probe đầu đã alive. Nếu nạp xong vẫn không sống → rơi xuống login.
            if (!aliveKept && File.Exists(cookieFile))
            {
                try
                {
                    var seeded = await ImportCookiesFromFileAsync(cdp, cookieFile, ct);
                    if (seeded > 0)
                    {
                        log($"Đã nạp {seeded} cookie đã đồng bộ vào profile — đang kiểm tra phiên…");
                        if (await ProbeLoggedInAsync(port, ct))
                        {
                            aliveKept = true;
                            log("✔ Phiên BigSeller (từ cookie đã đồng bộ) còn sống — không cần đăng nhập lại. Bấm Dừng để đóng.");
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }

            if (!aliveKept)
            {
                try
                {
                    await cdp.SendAsync("Storage.clearCookies", null, ct);
                    var pg = await CdpSession.ConnectToPageAsync(port, ct);
                    await pg.SendNoReplyAsync("Page.navigate", new { url = ListingUrl }, ct);
                    await pg.DisposeAsync();
                    log("Chưa có phiên sống — hãy đăng nhập BigSeller để lưu token mới (đảm bảo còn sống).");
                }
                catch { }

                // CHƯA đăng nhập → TỰ đăng nhập nếu Suite có cung cấp (điền email/mật khẩu + giải captcha AI).
                // Thành công → vòng poll bên dưới bắt được cookie & lưu. Thất bại/NeedsOtp → rơi về đăng nhập TAY.
                if (tryAutoLogin is not null)
                {
                    log("Đang thử TỰ đăng nhập BigSeller (điền tài khoản + giải captcha)…");
                    try
                    {
                        if (await tryAutoLogin(port, ct))
                            log("✔ Tự đăng nhập được — đang chờ lưu cookie…");
                        else
                            log("Tự đăng nhập chưa xong — hãy đăng nhập TAY trong cửa sổ Brave.");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { log("Tự đăng nhập lỗi: " + ex.Message + " — hãy đăng nhập tay."); }
                }
            }

            log("Đăng nhập BigSeller trong cửa sổ Brave. Cookie sẽ tự lưu khi phát hiện đăng nhập.");
            log("File cookie: " + cookieFile);
            log("ⓘ Cửa sổ sẽ KHÔNG tự đóng — bấm Dừng khi muốn đóng.");

            var pollsBeforeLogin = 0;
            var lastCookieCount = -1;
            var stablePolls = 0;
            var pollsSinceLogin = 0;
            var lastSavedCount = -1;
            // Cookie analytics/tracking (_ga, _fbp, _tt, ttcsid, _gcl_au, __lt__cid…) nạp BẤT ĐỒNG BỘ,
            // chậm hơn cookie phiên. Phải chờ số cookie NGỪNG TĂNG đủ lâu (~15s) rồi mới lưu — nếu lưu vội
            // (sau ~6s) thì file chỉ có ~9 cookie lõi (thiếu ~30) → phiên import vào browser "trống trải"
            // → BigSeller nghi & đá sau một lúc. v31 lưu được bộ ~39-43 đầy đủ nên phiên bền.
            const int requiredStablePolls = 5;   // ~15s không tăng cookie nữa = đã nạp đủ bộ
            const int maxWaitPolls = 12;          // ~36s: fallback lưu kẻo kẹt nếu cookie cứ refresh
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(3000, ct);

                // Người dùng TỰ ĐÓNG cửa sổ Brave (không bấm Dừng) → endpoint CDP chết. Probe nhanh
                // (/json/version, 3s) để THOÁT NGAY thay vì để Storage.getCookies treo tới 20s mới biết
                // → ViewModel chạy finally (IsLoggingIn=false) → nút "Open Bigseller" hiện/bật lại kịp thời.
                if (!await CdpSession.IsBrowserAliveAsync(port, ct))
                {
                    log("✘ Cửa sổ Brave đã đóng.");
                    return saved;
                }

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
                    // Chờ bộ cookie NẠP ĐỦ (số cookie ngừng tăng ~15s) rồi mới lưu — tránh lưu thiếu (~9).
                    pollsSinceLogin++;
                    var n = cookies.Count;
                    if (n == lastCookieCount) stablePolls++;
                    else { stablePolls = 0; lastCookieCount = n; }

                    var fullyLoaded = stablePolls >= requiredStablePolls || pollsSinceLogin >= maxWaitPolls;
                    if (fullyLoaded && (!saved || n > lastSavedCount))
                    {
                        // Lưu BỘ ĐẦY ĐỦ; nếu sau đó cookie còn tăng thì lưu đè (file luôn = bộ lớn nhất).
                        if (TryWriteCookieFile(cookieFile, cookies, log))
                        {
                            lastSavedCount = n;
                            if (!saved)
                            {
                                saved = true;
                                log($"✔ Đăng nhập thành công! Đã lưu {n} cookie (bộ đầy đủ). Bấm Dừng để đóng.");
                                try { onSaved?.Invoke(); } catch { }
                            }
                            else log($"Cập nhật cookie BigSeller → {n}.");
                        }
                    }
                    else if (!saved)
                    {
                        log($"Đã đăng nhập — đang chờ nạp đủ cookie ({n})…");
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

    /// <summary>
    /// Ghi setting "Brave Shields DOWN" cho bigseller vào Preferences của profile login TRƯỚC khi mở Brave
    /// (đúng giá trị Brave tự ghi khi bấm tắt Shields: content-setting <c>braveShields</c>, key
    /// <c>"www.bigseller.com,*"</c>, <c>setting=2</c>). Nhờ vậy tracker (_ga/_fbp/_tt…) KHÔNG bị chặn →
    /// đăng nhập lưu được bộ cookie đầy đủ. Best-effort: lỗi gì cũng bỏ qua (không chặn login).
    /// </summary>
    private static void EnsureBraveShieldsDown(string profileDir)
    {
        try
        {
            var def = Path.Combine(Path.GetFullPath(profileDir), "Default");
            Directory.CreateDirectory(def);
            var prefPath = Path.Combine(def, "Preferences");

            var root = File.Exists(prefPath)
                ? JsonNode.Parse(File.ReadAllText(prefPath)) as JsonObject ?? new JsonObject()
                : new JsonObject();

            var profile = root["profile"] as JsonObject ?? new JsonObject();
            root["profile"] = profile;
            var cs = profile["content_settings"] as JsonObject ?? new JsonObject();
            profile["content_settings"] = cs;
            var ex = cs["exceptions"] as JsonObject ?? new JsonObject();
            cs["exceptions"] = ex;
            var shields = ex["braveShields"] as JsonObject ?? new JsonObject();
            ex["braveShields"] = shields;

            // setting=2 = Shields DOWN cho site (giá trị Brave ghi khi tắt Shields). Phủ cả .com/.pro.
            foreach (var pat in new[] { "www.bigseller.com,*", "bigseller.com,*", "www.bigseller.pro,*", "bigseller.pro,*" })
                shields[pat] = new JsonObject { ["setting"] = 2 };

            File.WriteAllText(prefPath, root.ToJsonString());
        }
        catch { }
    }

    /// <summary>
    /// Vào trang listing rồi đọc <c>location.href</c> để biết CÒN đăng nhập không: bị đẩy về trang
    /// login/passport → phiên đã hết; ở lại khu <c>/web/</c> → còn đăng nhập. Trả true nếu còn sống.
    /// (Dùng page-session riêng; KHÔNG đụng tới browser-session đang poll cookie.)
    /// </summary>
    private static async Task<bool> ProbeLoggedInAsync(int port, CancellationToken ct)
    {
        CdpSession? pg = null;
        try
        {
            pg = await CdpSession.ConnectToPageAsync(port, ct);
            await pg.SendNoReplyAsync("Page.navigate", new { url = ListingUrl }, ct);
            for (var i = 0; i < 20; i++)   // ~10s
            {
                await Task.Delay(500, ct);
                string href = "", ready = "";
                try
                {
                    var r = await pg.SendAsync("Runtime.evaluate", new
                    {
                        expression = "JSON.stringify({href:location.href, ready:document.readyState})",
                        returnByValue = true,
                    }, ct);
                    if (r.TryGetProperty("result", out var rv) && rv.TryGetProperty("value", out var vv) &&
                        vv.ValueKind == JsonValueKind.String)
                    {
                        using var d = JsonDocument.Parse(vv.GetString() ?? "{}");
                        href = d.RootElement.TryGetProperty("href", out var h) ? h.GetString() ?? "" : "";
                        ready = d.RootElement.TryGetProperty("ready", out var rd) ? rd.GetString() ?? "" : "";
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { continue; }

                if (IsLoginUrl(href)) return false;
                if (string.Equals(ready, "complete", StringComparison.OrdinalIgnoreCase) &&
                    href.Contains("/web/", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
        finally { if (pg is not null) await pg.DisposeAsync(); }
    }

    private static bool IsLoginUrl(string url) =>
        url.Contains("login", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("passport", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("signin", StringComparison.OrdinalIgnoreCase);

    private static bool HasAuthCookie(IEnumerable<JsonElement> cookies) =>
        cookies.Any(c =>
            c.TryGetProperty("name", out var n) && n.GetString() == AuthCookieName &&
            c.TryGetProperty("value", out var v) && (v.GetString()?.Length ?? 0) > 5);

    // Ghi file cookie qua engine chung (atomic tmp+move, có retry) — file này bị Hub sync + importer đọc
    // đồng thời trong lúc login đang poll & ghi lặp; ghi trực tiếp trước đây gây torn-read cookie hỏng đa máy.
    private static bool TryWriteCookieFile(string cookieFile, IReadOnlyCollection<JsonElement> cookies, Action<string> log)
        => BigSellerCookieEngine.TryWriteCookieFile(cookieFile, cookies, log);
}
