using Shopee.Core.Accounts;
using Shopee.Core.Browser;
using Shopee.Core.Cdp;

namespace Shopee.Modules.CheckAccount;

public enum CheckOutcome
{
    /// <summary>Đăng nhập thành công (đã có cookie phiên SPC_ST/SPC_EC).</summary>
    Success,
    /// <summary>Sai tài khoản/mật khẩu (Shopee báo "incorrect").</summary>
    WrongPassword,
    /// <summary>Cần xử lý tay: captcha/OTP/verify, hoặc không xác định được trong thời gian chờ.</summary>
    NeedsManual,
    /// <summary>Lỗi kỹ thuật (proxy/CDP/không thấy form).</summary>
    Error,
}

public readonly record struct CheckResult(CheckOutcome Outcome, string Message);

/// <summary>
/// Mở Brave với kiotproxy, điền form đăng nhập Shopee theo kiểu human (di chuột có quán
/// tính, gõ từng ký tự, độ trễ ngẫu nhiên) rồi phân loại kết quả: thành công / sai mật
/// khẩu / cần xử lý tay. Dùng Brave (KHÔNG Edge) để profile/cookie đồng nhất với Scrape & Search.
/// </summary>
public sealed class ShopeeAccountChecker
{
    /// <summary>Trang đăng nhập Shopee — public để caller (vd "kiểm tra tk lỗi" double-click) mở cửa sổ
    /// THẲNG tại đây rồi bàn giao cho <see cref="LoginThenNavigateAsync"/> auto-login.</summary>
    public const string LoginUrl = ShopeeAuth.LoginUrl;

    private readonly Random _rng = new();
    // Chuyển động/độ trễ kiểu người dùng chung với Search (Core) — chỉ khác bộ hằng. Dùng CHUNG _rng
    // để chuỗi ngẫu nhiên của cả luồng không đổi so với lúc code này còn nằm trong lớp này.
    private readonly CdpHumanInput _input;

    public event Action<string>? Log;

    public ShopeeAccountChecker()
    {
        _input = new CdpHumanInput(HumanInputProfile.CheckAccount, _rng);
    }

    /// <summary>
    /// <paramref name="proxy"/> là proxy đã resolve sẵn (đã xoay vòng ở ngoài); null = không proxy.
    /// <paramref name="profileDir"/>: thư mục user-data-dir (Brave) cho tài khoản này — KHÔNG bị xoá
    /// ở đây; caller quyết định giữ (login OK, để copy sang kho profile dùng chung) hay xoá.
    /// <paramref name="holdMs"/>: giữ trình duyệt mở thêm bấy nhiêu ms sau khi có kết quả (bất kể
    /// thành công/thất bại) rồi mới đóng — giả lập người dùng, tránh login dồn dập.
    /// </summary>
    public async Task<CheckResult> CheckAsync(
        string accountLine, string? proxy, string profileDir, int holdMs, CancellationToken ct)
    {
        var login = ShopeeAuth.ParseLoginLine(accountLine, ShopeeLoginLineOptions.AllowMissingCookie);
        if (!login.Ok)
            return new CheckResult(CheckOutcome.Error, "Sai định dạng (cần tối thiểu user|pass).");
        var username = login.Username;

        // Mở Brave với profile của tài khoản (caller chuẩn bị & quyết định giữ/xoá thư mục). Dùng Brave
        // (KHÔNG Edge) để profile/cookie đăng nhập DÙNG CHUNG được với Scrape & Search (cùng Chromium).
        var launcher = new BrowserLauncher(BrowserKind.Brave);
        CdpSession? cdp = null;
        try
        {
            launcher.Launch(profileDir, proxy, LoginUrl);

            cdp = await CdpSession.ConnectToPageAsync(launcher.CdpPort, ct);
            await TrySend(cdp, "Network.enable", null, ct);
            await TrySend(cdp, "Page.enable", null, ct);

            if (!string.IsNullOrWhiteSpace(login.SpcF))
                await SetSpcFCookieAsync(cdp, login.CookieDomain, login.SpcF, ct);

            // Điều hướng lại cho chắc (cookie SPC_F đã set trước khi tải form)
            await TrySend(cdp, "Page.navigate", new { url = LoginUrl }, ct);

            // 3) Chờ form đăng nhập XUẤT HIỆN, hoặc phát hiện ĐÃ ĐĂNG NHẬP SẴN. Nếu profile còn phiên,
            //    trang login redirect thẳng ra home → KHÔNG có form → coi là thành công, KHÔNG cần điền.
            CheckResult result;
            switch (await WaitForFormOrLoginAsync(cdp, ct))
            {
                case FormWait.LoggedIn:
                    Log?.Invoke("  đã đăng nhập sẵn (redirect ra home) — bỏ qua điền form.");
                    result = new CheckResult(CheckOutcome.Success, $"Đã đăng nhập sẵn: {username}");
                    break;

                case FormWait.None:
                    result = new CheckResult(CheckOutcome.NeedsManual, "Không thấy form đăng nhập (có thể đã chặn/đổi giao diện).");
                    break;

                default: // FormWait.Form → điền form kiểu human rồi chờ kết quả
                    await HumanFillAsync(cdp, username, login.Password, ct);
                    result = await WaitOutcomeAsync(cdp, username, ct);
                    break;
            }

            // Giữ trình duyệt mở thêm holdMs (bất kể thành công/thất bại) rồi mới đóng profile.
            if (holdMs > 0)
            {
                Log?.Invoke($"  giữ trình duyệt {holdMs / 1000.0:0.#}s rồi đóng…");
                try { await Task.Delay(holdMs, ct); } catch { }
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            return new CheckResult(CheckOutcome.Error, "Đã dừng.");
        }
        catch (Exception ex)
        {
            return new CheckResult(CheckOutcome.Error, ex.Message);
        }
        finally
        {
            if (cdp is not null) await cdp.DisposeAsync();
            launcher.Kill();                       // đóng sạch Brave của profile này (không xoá thư mục)
            await Task.Delay(400, CancellationToken.None);
        }
    }

    /// <summary>
    /// ĐẢM BẢO PROFILE ĐÃ ĐĂNG NHẬP trên phiên CDP đang mở — dùng CHUNG cho luồng "kiểm tra tk lỗi": nếu
    /// còn cookie phiên (SPC_ST/SPC_EC) → thôi (0 captcha); chưa có → TỰ ĐĂNG NHẬP GIỐNG "Check Shopee
    /// Account" (set SPC_F + điền form human) rồi chờ tới khi login xong / gặp captcha (thoát sớm). Trả
    /// true nếu chắc chắn đã đăng nhập. <paramref name="accountLine"/> rỗng / sai định dạng → trả false.
    /// </summary>
    private async Task<bool> EnsureLoggedInAsync(CdpSession cdp, string accountLine, CancellationToken ct)
    {
        if (await IsLoggedInAsync(cdp, ct)) return true;                       // còn phiên → khỏi login
        var login = ShopeeAuth.ParseLoginLine(accountLine, ShopeeLoginLineOptions.AllowMissingCookie);
        if (!login.Ok)
            return false;                                                      // không có tk hợp lệ → chịu

        Log?.Invoke("  chưa có phiên → tự đăng nhập trước…");
        if (!string.IsNullOrWhiteSpace(login.SpcF))
            await SetSpcFCookieAsync(cdp, login.CookieDomain, login.SpcF, ct);
        await TrySend(cdp, "Page.navigate", new { url = LoginUrl }, ct);       // set SPC_F xong mới tải form

        switch (await WaitForFormOrLoginAsync(cdp, ct))
        {
            case FormWait.LoggedIn: return true;                              // redirect ra home → đã đăng nhập
            case FormWait.Form:
                await HumanFillAsync(cdp, login.Username, login.Password, ct);
                return (await WaitOutcomeAsync(cdp, login.Username, ct)).Outcome == CheckOutcome.Success;
            default: return false;                                            // không thấy form (bị chặn/đổi UI)
        }
    }

    /// <summary>
    /// Luồng GIẢI TAY double-click ("kiểm tra tk lỗi"): trên MỘT cửa sổ Brave ĐÃ được caller mở sẵn (caller
    /// GIỮ <paramref name="launcher"/> để tự Kill() bất cứ lúc nào — kể cả GIỮA lúc auto-login), nếu profile
    /// CHƯA có phiên thì TỰ ĐĂNG NHẬP trước (giống "Check Shopee Account") rồi điều hướng tới <paramref
    /// name="url"/> cho user GIẢI TAY. KHÔNG tạo/đóng cửa sổ ở đây — caller quản lý vòng đời để
    /// KillCheckBrowser LUÔN với tới được (không rò cửa sổ có phiên login). <paramref name="accountLine"/>
    /// rỗng → bỏ qua login, chỉ mở URL. Nuốt mọi lỗi CDP/login (giữ cửa sổ mở cho user tự đăng nhập/giải tay).
    /// </summary>
    public async Task LoginThenNavigateAsync(
        BrowserLauncher launcher, string accountLine, string url, CancellationToken ct)
    {
        CdpSession? cdp = null;
        try
        {
            cdp = await CdpSession.ConnectToPageAsync(launcher.CdpPort, ct);
            await TrySend(cdp, "Network.enable", null, ct);
            await TrySend(cdp, "Page.enable", null, ct);
            if (!string.IsNullOrWhiteSpace(accountLine)) await EnsureLoggedInAsync(cdp, accountLine, ct);
            await TrySend(cdp, "Page.navigate", new { url }, ct);              // đã login → mở URL lỗi để giải tay
        }
        catch { /* CDP/login lỗi giữa chừng → vẫn ĐỂ cửa sổ mở cho user tự đăng nhập/giải tay */ }
        finally
        {
            if (cdp is not null) await cdp.DisposeAsync();                     // đóng phiên CDP; Brave vẫn mở
        }
    }

    // ── Bước điền form (human) ─────────────────────────────────────────────────

    private async Task HumanFillAsync(CdpSession cdp, string username, string password, CancellationToken ct)
    {
        Log?.Invoke("  điền tài khoản…");
        await ClickSelectorAsync(cdp, "input[name=\"loginKey\"], input[type=\"text\"]", ct);
        await TypeHumanAsync(cdp, username, ct);
        await DelayAsync(280, 620, ct);

        Log?.Invoke("  điền mật khẩu…");
        await ClickSelectorAsync(cdp, "input[name=\"password\"], input[type=\"password\"]", ct);
        await TypeHumanAsync(cdp, password, ct);
        await DelayAsync(350, 760, ct);

        Log?.Invoke("  bấm đăng nhập…");
        var btn = await GetButtonRectAsync(cdp, ct);
        if (btn is { } b)
        {
            await ClickAtAsync(cdp, b.X, b.Y, ct);
        }
        else
        {
            // Fallback: Enter trong ô mật khẩu
            await PressEnterAsync(cdp, ct);
        }
    }

    /// <summary>Kết quả chờ: form đăng nhập đã hiện / đã đăng nhập sẵn (redirect home) / không thấy gì.</summary>
    private enum FormWait { Form, LoggedIn, None }

    private async Task<FormWait> WaitForFormOrLoginAsync(CdpSession cdp, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            // Đã có cookie phiên (SPC_ST/SPC_EC) → profile còn đăng nhập, trang login đã/ sẽ redirect ra
            // home và KHÔNG hiện form → coi như đăng nhập sẵn, không cần điền.
            if (await IsLoggedInAsync(cdp, ct)) return FormWait.LoggedIn;

            var has = await EvalBoolAsync(cdp,
                "!!(document.querySelector('input[name=\"loginKey\"]') || document.querySelector('input[type=\"password\"]'))",
                ct);
            if (has) { await DelayAsync(500, 1100, ct); return FormWait.Form; }

            await Task.Delay(700, ct);
        }
        return FormWait.None;
    }

    private async Task<CheckResult> WaitOutcomeAsync(CdpSession cdp, string username, CancellationToken ct)
    {
        Log?.Invoke("  chờ kết quả…");
        var deadline = DateTime.UtcNow.AddSeconds(70);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(2000, ct);

            // a) Đăng nhập thành công?
            if (await IsLoggedInAsync(cdp, ct))
                return new CheckResult(CheckOutcome.Success, $"OK: {username}");

            // b) Sai mật khẩu / verify / captcha?
            var (url, alert) = await ReadPageStateAsync(cdp, ct);
            var a = alert.ToLowerInvariant();
            if (a.Contains("incorrect") || a.Contains("không chính xác") || a.Contains("khong chinh xac")
                || a.Contains("sai") || a.Contains("không đúng") || a.Contains("khong dung"))
                return new CheckResult(CheckOutcome.WrongPassword, "Sai tài khoản/mật khẩu.");

            var u = url.ToLowerInvariant();
            if (u.Contains("/verify") || u.Contains("captcha")
                || a.Contains("otp") || a.Contains("mã xác") || a.Contains("xác minh") || a.Contains("xac minh"))
                return new CheckResult(CheckOutcome.NeedsManual, "Cần OTP/verify/captcha — xử lý tay.");
        }

        return new CheckResult(CheckOutcome.NeedsManual, "Quá thời gian chờ, không rõ kết quả — xử lý tay.");
    }

    // ── CDP helpers ────────────────────────────────────────────────────────────

    private async Task<bool> IsLoggedInAsync(CdpSession cdp, CancellationToken ct)
    {
        try
        {
            var result = await cdp.SendAsync("Network.getAllCookies", null, ct);
            if (!result.TryGetProperty("cookies", out var cookies)) return false;
            foreach (var cookie in cookies.EnumerateArray())
            {
                var domain = cookie.TryGetProperty("domain", out var d) ? d.GetString() ?? "" : "";
                var name = cookie.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var value = cookie.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
                if (ShopeeAuth.IsSessionCookie(domain, name, value)) return true;
            }
        }
        catch { }
        return false;
    }

    private async Task<(string Url, string Alert)> ReadPageStateAsync(CdpSession cdp, CancellationToken ct)
    {
        var js = """
            (() => {
                const alerts = Array.from(document.querySelectorAll('div[role="alert"]'));
                const alertText = alerts.map(a => a.innerText || '').join(' | ');
                return JSON.stringify({ url: location.href, alert: alertText });
            })()
            """;
        var raw = await EvalStringAsync(cdp, js, ct);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            return (
                root.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                root.TryGetProperty("alert", out var a) ? a.GetString() ?? "" : "");
        }
        catch { return ("", ""); }
    }

    private static async Task SetSpcFCookieAsync(CdpSession cdp, string domain, string spcF, CancellationToken ct)
    {
        try { await cdp.SendAsync("Network.setCookie", ShopeeAuth.BuildSpcFCookie(domain, spcF), ct); }
        catch { }
    }

    private async Task ClickSelectorAsync(CdpSession cdp, string selector, CancellationToken ct)
    {
        var rect = await GetRectAsync(cdp, selector, ct);
        if (rect is { } r) await ClickAtAsync(cdp, r.X, r.Y, ct);
    }

    private async Task<(double X, double Y)?> GetRectAsync(CdpSession cdp, string selector, CancellationToken ct)
    {
        var js = $$"""
            (() => {
                const el = document.querySelector({{JsonSerializer.Serialize(selector)}});
                if (!el) return 'null';
                const r = el.getBoundingClientRect();
                if (r.width <= 0 || r.height <= 0) return 'null';
                return JSON.stringify({ x: r.left + r.width / 2, y: r.top + r.height / 2 });
            })()
            """;
        return ParseRect(await EvalStringAsync(cdp, js, ct));
    }

    private async Task<(double X, double Y)?> GetButtonRectAsync(CdpSession cdp, CancellationToken ct)
    {
        var js = """
            (() => {
                const btns = Array.from(document.querySelectorAll('button'));
                const b = btns.find(b => /log\s*in|đăng\s*nhập|dang\s*nhap/i.test((b.textContent || '').trim()))
                        || document.querySelector('button[type="submit"]');
                if (!b) return 'null';
                const r = b.getBoundingClientRect();
                if (r.width <= 0 || r.height <= 0) return 'null';
                return JSON.stringify({ x: r.left + r.width / 2, y: r.top + r.height / 2 });
            })()
            """;
        return ParseRect(await EvalStringAsync(cdp, js, ct));
    }

    private static (double X, double Y)? ParseRect(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "null") return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.TryGetProperty("x", out var x) && root.TryGetProperty("y", out var y))
                return (x.GetDouble(), y.GetDouble());
        }
        catch { }
        return null;
    }

    private async Task<string> EvalStringAsync(CdpSession cdp, string expression, CancellationToken ct)
    {
        try
        {
            var result = await cdp.SendAsync("Runtime.evaluate", new
            {
                expression, awaitPromise = true, returnByValue = true,
            }, ct);
            if (result.TryGetProperty("result", out var r) && r.TryGetProperty("value", out var v))
                return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString();
        }
        catch { }
        return "";
    }

    private async Task<bool> EvalBoolAsync(CdpSession cdp, string expression, CancellationToken ct)
    {
        try
        {
            var result = await cdp.SendAsync("Runtime.evaluate", new
            {
                expression, awaitPromise = true, returnByValue = true,
            }, ct);
            if (result.TryGetProperty("result", out var r) && r.TryGetProperty("value", out var v))
                return v.ValueKind == JsonValueKind.True;
        }
        catch { }
        return false;
    }

    // ── Trusted input (human-like) ──────────────────────────────────────────────

    private Task ClickAtAsync(CdpSession cdp, double tx, double ty, CancellationToken ct) =>
        _input.ClickAsync(CdpSender.For(cdp), tx, ty, ct: ct);

    private Task TypeHumanAsync(CdpSession cdp, string text, CancellationToken ct) =>
        _input.TypeTextAsync(CdpSender.For(cdp), text, ct: ct);

    private Task PressEnterAsync(CdpSession cdp, CancellationToken ct) =>
        _input.PressKeyAsync(CdpSender.For(cdp), "Enter", ct);

    private static async Task TrySend(CdpSession cdp, string method, object? @params, CancellationToken ct)
    {
        try { await cdp.SendAsync(method, @params, ct); } catch { }
    }

    private Task DelayAsync(int minMs, int maxMs, CancellationToken ct) => Task.Delay(_rng.Next(minMs, maxMs + 1), ct);
}
