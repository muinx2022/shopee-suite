using System.Net.WebSockets;
using System.Text.Json;
using Shopee.Core.Accounts;
using Shopee.Core.Cdp;

namespace OpenMultiBraveLauncherV3;

/// <summary>
/// ĐƯA PROFILE VÀO TRẠNG THÁI ĐÃ ĐĂNG NHẬP SHOPEE trước khi scrape: ưu tiên import nguyên session từ
/// profile đã đăng nhập (khỏi điền form — login lại nhiều lần từ IP khác nhau CHÍNH là nguyên nhân dính
/// captcha), rồi mới nạp cookie SPC_F + mở trang login + điền form bằng JS gõ-như-người.
/// Tách khỏi <see cref="BraveInstanceSession"/>; JS <c>typeHuman</c> giữ NGUYÊN XI (vùng anti-bot).
/// </summary>
internal sealed class ShopeeSessionBootstrapper(
    CdpClient cdpClient,
    CookieService cookieService,
    Func<InstanceConfig?> config,
    Func<bool> isRunning,
    Func<CancellationToken, Task<bool>> waitForCdpReady,
    Action<string> log,
    Action onLoginFlagsCleared)
{
    private string? _sessionProfileDir;

    /// <summary>Thư mục profile (Edge) ĐÃ đăng nhập Shopee của tk này — để import nguyên session (SPC_ST/
    /// SPC_EC…) sang Brave, khỏi điền form. Trống/không hợp lệ → bỏ qua, login thường.</summary>
    public void SetSessionProfileDir(string? dir) => _sessionProfileDir = dir;

    /// <summary>
    /// Kiểm tra cookie phiên Shopee (SPC_ST / SPC_EC) — có giá trị thật nghĩa là đã đăng nhập.
    /// </summary>
    private async Task<bool> IsLoggedInAsync()
    {
        try
        {
            var cookies = await cookieService.GetShopeeCookiesAsync().ConfigureAwait(false);
            return cookies.Any(c => ShopeeAuth.IsSessionCookie(
                c.TryGetValue("domain", out var d) ? d?.ToString() ?? "" : "",
                c.TryGetValue("name", out var n) ? n?.ToString() ?? "" : "",
                c.TryGetValue("value", out var v) ? v?.ToString() ?? "" : ""));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Đảm bảo profile đã đăng nhập Shopee trước khi scrape.
    /// Chưa đăng nhập + có chuỗi tài khoản → tự mở trang login và điền form,
    /// rồi chờ cookie phiên xuất hiện (tối đa ~90s).
    /// </summary>
    public async Task<bool> EnsureLoggedInAsync(CancellationToken cancellationToken = default)
    {
        var cfg = config();
        if (cfg is null || !isRunning())
            return false;

        // Không có chuỗi tài khoản → giữ hành vi cũ (profile đã login thủ công từ trước).
        if (string.IsNullOrWhiteSpace(cfg.ShopeeAccountLogin))
            return true;

        if (!await waitForCdpReady(cancellationToken).ConfigureAwait(false))
        {
            log("Shopee: CDP chưa sẵn sàng — không kiểm tra được đăng nhập.");
            return false;
        }

        // Import session đã đăng nhập của tk (từ profile Edge của tab "Kiểm tra tài khoản") sang Brave →
        // IsLoggedInAsync thấy SPC_ST/SPC_EC → KHỎI điền form. Login lại nhiều lần từ IP khác nhau
        // chính là nguyên nhân dính captcha; import cookie tránh được điều đó.
        try
        {
            var injected = await InjectSessionCookiesAsync().ConfigureAwait(false);
            if (injected > 0)
                log($"Shopee: import {injected} cookie từ profile đã đăng nhập (khỏi điền form).");
        }
        catch (Exception ex) { log("Shopee: import cookie session lỗi (sẽ thử login thường): " + ex.Message); }

        // Thử vài nhịp trước khi kết luận "chưa đăng nhập": cookie store có thể chưa nạp xong từ đĩa
        // ngay sau khi CDP ready → tránh login lại THỪA (login liên tục là nguyên nhân dính captcha).
        for (var i = 0; i < 5; i++)
        {
            if (await IsLoggedInAsync().ConfigureAwait(false))
            {
                log("Shopee: đã đăng nhập sẵn (giữ cookie từ phiên trước).");
                ClearLoginPendingFlag();
                return true;
            }
            if (i < 4) await Task.Delay(800, cancellationToken).ConfigureAwait(false);
        }

        log("Shopee: chưa đăng nhập — tự đăng nhập bằng tài khoản đã lưu…");
        await OpenAccountLoginAsync().ConfigureAwait(false);

        for (var i = 0; i < 30; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(3000, cancellationToken).ConfigureAwait(false);
            if (await IsLoggedInAsync().ConfigureAwait(false))
            {
                log("Shopee: đăng nhập thành công.");
                ClearLoginPendingFlag();
                return true;
            }
        }

        log("Shopee: không xác nhận được đăng nhập sau 90s (có thể vướng captcha/OTP) — cần xử lý thủ công.");
        return false;
    }

    private void ClearLoginPendingFlag()
    {
        var cfg = config();
        if (cfg is null)
            return;

        var changed = cfg.OpenWithShopeeAccount || cfg.CreateNewProfileOnNextStart;
        cfg.OpenWithShopeeAccount = false;
        cfg.CreateNewProfileOnNextStart = false;
        if (changed)
            onLoginFlagsCleared();
    }

    public async Task<bool> OpenAccountLoginAsync()
    {
        var cfg = config();
        if (cfg is null || !isRunning())
            return false;

        try
        {
            var login = ShopeeAuth.ParseLoginLine(cfg.ShopeeAccountLogin, ShopeeLoginLineOptions.Strict);
            if (!login.Ok)
            {
                log($"Shopee login: {login.Error}");
                return false;
            }

            var cdpReady = false;
            for (var i = 0; i < 20 && isRunning(); i++)
            {
                try
                {
                    _ = await GetBrowserWebSocketUrlAsync().ConfigureAwait(false);
                    cdpReady = true;
                    break;
                }
                catch
                {
                    await Task.Delay(500).ConfigureAwait(false);
                }
            }

            if (!cdpReady || !isRunning())
            {
                log("Shopee login: CDP không sẵn sàng — profile có thể đã đóng.");
                return false;
            }

            await SetSpcFCookieAsync(login).ConfigureAwait(false);
            if (!isRunning())
                return false;

            await OpenLoginPageAsync().ConfigureAwait(false);
            if (!isRunning())
                return false;

            await FillLoginFormAsync(login).ConfigureAwait(false);
            log($"Shopee login: đã mở trang đăng nhập và điền tài khoản {login.Username}.");
            return true;
        }
        catch (Exception ex)
        {
            log($"Shopee login lỗi: {ex.Message}");
            return false;
        }
    }

    /// <summary>Đọc + giải mã cookie shopee từ profile đã đăng nhập rồi inject vào Brave qua CDP. Trả số
    /// cookie đã nạp (0 = không có profile / không giải mã được → caller login thường).</summary>
    private async Task<int> InjectSessionCookiesAsync()
    {
        var dir = _sessionProfileDir;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return 0;

        var cookies = ChromiumCookieReader.ReadCookies(dir, "shopee");
        if (cookies.Count == 0)
            return 0;

        var payloads = cookies.Select(c =>
        {
            var d = new Dictionary<string, object?>
            {
                ["name"] = c.Name,
                ["value"] = c.Value,
                ["domain"] = c.Domain,
                ["path"] = string.IsNullOrEmpty(c.Path) ? "/" : c.Path,
                ["secure"] = c.Secure,
                ["httpOnly"] = c.HttpOnly,
            };
            // sameSite="None" bắt buộc secure=true; nếu không, bỏ sameSite để CDP không từ chối cả batch.
            if (c.SameSite is not null && !(c.SameSite == "None" && !c.Secure)) d["sameSite"] = c.SameSite;
            if (c.ExpiresUnix is not null) d["expires"] = c.ExpiresUnix.Value;
            return d;
        }).ToArray();

        using var browser = new ClientWebSocket();
        await browser.ConnectAsync(new Uri(await GetBrowserWebSocketUrlAsync().ConfigureAwait(false)), CancellationToken.None);
        await CdpClient.SendAsync(browser, 716, "Storage.setCookies", new { cookies = payloads });
        return payloads.Length;
    }

    private async Task SetSpcFCookieAsync(ShopeeLoginLine login)
    {
        var payload = ShopeeAuth.BuildSpcFCookie(login.CookieDomain, login.SpcF);

        using var browser = new ClientWebSocket();
        await browser.ConnectAsync(new Uri(await GetBrowserWebSocketUrlAsync().ConfigureAwait(false)), CancellationToken.None);
        await CdpClient.SendAsync(browser, 710, "Storage.setCookies", new { cookies = new[] { payload } });
    }

    private async Task OpenLoginPageAsync()
    {
        var wsUrl = await cdpClient.EnsurePageTargetAsync(
            url => url.StartsWith(ShopeeAuth.LoginUrlPrefix, StringComparison.OrdinalIgnoreCase),
            ShopeeAuth.LoginUrl).ConfigureAwait(false);

        using var page = new ClientWebSocket();
        await page.ConnectAsync(new Uri(wsUrl), CancellationToken.None).ConfigureAwait(false);
        await CdpClient.SendAsync(page, 721, "Page.navigate", new { url = ShopeeAuth.LoginUrl });
    }

    private async Task FillLoginFormAsync(ShopeeLoginLine login)
    {
        var usernameJson = JsonSerializer.Serialize(login.Username);
        var passwordJson = JsonSerializer.Serialize(login.Password);
        var expression =
            "(async () => {" +
            $"const username = {usernameJson};" +
            $"const password = {passwordJson};" +
            "const sleep = ms => new Promise(r => setTimeout(r, ms));" +
            "const rand = (a, b) => a + Math.floor(Math.random() * (b - a + 1));" +
            "const nativeSet = (el, value) => {" +
            "  const proto = Object.getPrototypeOf(el);" +
            "  const desc = Object.getOwnPropertyDescriptor(proto, 'value') || Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value');" +
            "  desc.set.call(el, value);" +
            "};" +
            // Gõ từng ký tự với delay ngẫu nhiên + sự kiện bàn phím cho giống người gõ, không paste thẳng.
            "const typeHuman = async (el, text) => {" +
            "  el.focus();" +
            "  el.dispatchEvent(new MouseEvent('mousedown', { bubbles: true }));" +
            "  el.dispatchEvent(new MouseEvent('mouseup', { bubbles: true }));" +
            "  el.click();" +
            "  nativeSet(el, '');" +
            "  el.dispatchEvent(new Event('input', { bubbles: true }));" +
            "  await sleep(rand(150, 400));" +
            "  let cur = '';" +
            "  for (const ch of text) {" +
            "    el.dispatchEvent(new KeyboardEvent('keydown', { key: ch, bubbles: true }));" +
            "    cur += ch;" +
            "    nativeSet(el, cur);" +
            "    el.dispatchEvent(new InputEvent('input', { bubbles: true, data: ch, inputType: 'insertText' }));" +
            "    el.dispatchEvent(new KeyboardEvent('keyup', { key: ch, bubbles: true }));" +
            "    await sleep(rand(45, 160));" +
            "  }" +
            "  el.dispatchEvent(new Event('change', { bubbles: true }));" +
            "  el.dispatchEvent(new Event('blur', { bubbles: true }));" +
            "};" +
            "for (let i = 0; i < 80; i++) {" +
            "  const u = document.querySelector('input[name=\"loginKey\"]');" +
            "  const p = document.querySelector('input[name=\"password\"]');" +
            "  if (u && p) {" +
            "    await typeHuman(u, username);" +
            "    await sleep(rand(300, 700));" +
            "    await typeHuman(p, password);" +
            "    await sleep(rand(500, 1000));" +
            "    const buttons = [...document.querySelectorAll('button')];" +
            "    const loginButton = buttons.find(b => /log\\s*in|đăng\\s*nhập/i.test((b.textContent || '').trim())) || buttons.find(b => b.type === 'submit') || buttons.at(-1);" +
            "    if (loginButton) { loginButton.removeAttribute('disabled'); loginButton.click(); }" +
            "    return { ok: true };" +
            "  }" +
            "  await sleep(250);" +
            "}" +
            "return { ok: false, message: 'Không tìm thấy form login Shopee.' };" +
            "})()";

        Exception? lastError = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                var wsUrl = await cdpClient.FindPageWebSocketUrlAsync(url =>
                    url.StartsWith(ShopeeAuth.LoginUrlPrefix, StringComparison.OrdinalIgnoreCase) ||
                    url.StartsWith("https://shopee.vn/", StringComparison.OrdinalIgnoreCase)).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(wsUrl))
                {
                    await Task.Delay(700).ConfigureAwait(false);
                    continue;
                }

                using var page = new ClientWebSocket();
                await page.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
                await CdpClient.SendAsync(page, 730, "Runtime.enable", null);
                await Task.Delay(500).ConfigureAwait(false);
                await CdpClient.SendAsync(page, 731, "Runtime.evaluate", new
                {
                    expression,
                    awaitPromise = true,
                    returnByValue = true,
                });
                return;
            }
            catch (Exception ex) when (CdpErrors.IsTransientNavigationError(ex))
            {
                lastError = ex;
                await Task.Delay(900).ConfigureAwait(false);
            }
        }

        if (lastError is not null)
            throw lastError;
    }

    private Task<string> GetBrowserWebSocketUrlAsync() => cdpClient.GetBrowserWebSocketUrlAsync();
}
