using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Shopee.Core.BigSeller;

/// <summary>
/// Tự mở hộp thư Hotmail/Outlook (Playwright headless) để ĐỌC mã xác minh 6 số BigSeller gửi về EMAIL của tài
/// khoản, thay cho việc chờ admin gõ tay. PORT logic điều hướng form đăng nhập Microsoft từ module Đơn hàng
/// (ShopeeLoginService.LoginHotmailAsync) — GIỮ các nhánh khó (landing → "Đăng nhập"; form Fluent "Xác minh
/// email" mới → "Các cách khác để đăng nhập" → tile "Nhập mật khẩu"; KMSI "Duy trì đăng nhập?" → "Có") nhưng
/// BỎ các helper "human-like" (di chuột cong / gõ từng ký tự) vì chạy headless trên server, thay bằng locator
/// API thẳng. Phần ĐỌC MÃ viết mới: KHÔNG click link (khác luồng Shopee), chỉ đọc text + regex <c>\b\d{6}\b</c>.
/// BẤT BIẾN: KHÔNG ném (trừ hủy) — mọi lỗi trả <c>null</c> để caller fallback về đường admin-gõ-tay; KHÔNG log
/// giá trị mật khẩu.
/// </summary>
public static class HotmailOtpReader
{
    // Chỉ tự đọc khi email thuộc các domain Microsoft (so đuôi sau '@', không phân biệt hoa/thường). Email khác
    // (gmail, tên miền riêng…) → trả null ngay để fallback gõ tay.
    private static readonly string[] AllowedMailDomains =
        { "outlook.com", "hotmail.com", "live.com", "live.vn", "msn.com" };

    // --- Selector đăng nhập Microsoft/Outlook (đổi thường xuyên → luôn nhiều fallback, timeout ngắn bỏ qua được).
    //     PORT từ ShopeeLoginService (MsUserSelectors…MsSignInSelectors). ---
    private static readonly string[] MsUserSelectors =
        { "input[type='email']", "input[name='loginfmt']", "#i0116" };
    private static readonly string[] MsPasswordSelectors =
        { "input[name='passwd']", "input[type='password']", "#i0118" };
    private static readonly string[] MsSubmitSelectors =
        { "#idSIButton9", "input[type='submit']", "button[type='submit']" };
    // Tile "Nhập mật khẩu"/"Sử dụng mật khẩu" (khớp KHÔNG dấu "mat khau"/"password"/"contrasena" trong đám clickable).
    private static readonly string[] MsUsePasswordSelectors =
        { "#idA_PWD_SwitchToPassword", "a", "[role='button']", "button", "span" };
    // Link "Các cách khác để đăng nhập" trên form mới "Xác minh email của bạn" (Fluent UI).
    private static readonly string[] MsOtherWaysSelectors =
        { "span[role='button']", "[role='button']", "a", "button" };
    // KMSI ("Duy trì đăng nhập?") chỉ dùng ID cho bản Outlook cũ; form Fluent mới nhận diện qua testid rồi bấm primaryButton.
    private static readonly string[] MsKmsiYesSelectors =
        { "#acceptButton", "#idSIButton9" };
    // Nút "Đăng nhập"/"Sign in" ở trang landing (khi chưa nhảy thẳng vào form nhập email).
    private static readonly string[] MsSignInSelectors =
        { "a[data-task='signin']", "a[href*='login.live.com']", "a[href*='login.microsoftonline']", "a[href*='login']", "a", "button", "[role='button']" };

    private static readonly Regex SignInRegex =
        new(@"sign\s*in|đăng nhập|dang nhap", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Mã xác minh BigSeller: đúng 6 chữ số, không dính số dài hơn (\b biên từ).
    private static readonly Regex SixDigitRegex =
        new(@"\b(\d{6})\b", RegexOptions.Compiled);

    /// <summary>Trả mã 6 số BigSeller gửi về hòm mail Hotmail/Outlook của <paramref name="email"/>, hoặc null nếu
    /// không lấy được (email không phải outlook/hotmail/live, sai mật khẩu, form MS đổi, không thấy mail trong hạn…).
    /// Mở 1 tab mail MỚI trong context đang có (khác domain BigSeller nên không ảnh hưởng cookie muc_token), đọc xong
    /// tự đóng tab. KHÔNG ném (trừ hủy) — mọi lỗi → null để caller fallback chờ admin. KHÔNG log giá trị mật khẩu.</summary>
    public static async Task<string?> TryReadCodeAsync(
        IBrowserContext context, string email, string emailPassword,
        Action<string>? log, CancellationToken ct)
    {
        void L(string m) => log?.Invoke(m);

        // Gate: chỉ tự đọc với hòm thư Microsoft + có mật khẩu email. Ngoài phạm vi → null (fallback gõ tay).
        if (string.IsNullOrWhiteSpace(email) || !IsSupportedMailDomain(email))
        {
            L("• Email không phải Hotmail/Outlook → không tự đọc mã (chờ admin gõ tay).");
            return null;
        }
        if (string.IsNullOrWhiteSpace(emailPassword))
        {
            L("• Chưa có mật khẩu email → không tự đọc mã (chờ admin gõ tay).");
            return null;
        }

        IPage? mailPage = null;
        try
        {
            mailPage = await context.NewPageAsync().ConfigureAwait(false);
            L("Mở trang đăng nhập Microsoft để tự đọc mã…");
            try
            {
                await mailPage.GotoAsync("https://login.microsoftonline.com/",
                    new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 }).ConfigureAwait(false);
            }
            catch { /* nuốt lỗi điều hướng — các bước dưới poll selector tự lo */ }

            if (!await LoginOutlookAsync(mailPage, email, emailPassword, log, ct).ConfigureAwait(false))
            {
                L("• Không đăng nhập được hộp thư → chờ admin gõ tay.");
                return null;
            }

            // Vào HỘP THƯ để đọc mail (login.microsoftonline hạ cánh ở portal, không phải hộp thư).
            L("Vào hộp thư Outlook để tìm mã…");
            try
            {
                await mailPage.GotoAsync("https://outlook.live.com/mail/0/",
                    new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 }).ConfigureAwait(false);
            }
            catch { /* nuốt lỗi điều hướng — bước dưới poll tự lo */ }
            await Task.Delay(2500, ct).ConfigureAwait(false);

            // Vẫn ở trang đăng nhập = chưa vào được hộp thư (chưa thực sự login) → khỏi poll 3' vô ích.
            var url = mailPage.Url ?? string.Empty;
            if (url.Contains("login.live.com", StringComparison.OrdinalIgnoreCase)
                || url.Contains("login.microsoftonline", StringComparison.OrdinalIgnoreCase))
            {
                L("• Vẫn ở trang đăng nhập Microsoft (chưa vào được hộp thư) → chờ admin gõ tay.");
                return null;
            }

            return await ReadBigSellerCodeAsync(mailPage, log, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            L("• Tự đọc mã lỗi: " + ex.Message);
            return null;
        }
        finally
        {
            if (mailPage is not null) { try { await mailPage.CloseAsync().ConfigureAwait(false); } catch { } }
        }
    }

    /// <summary>True nếu đuôi sau '@' của <paramref name="email"/> thuộc <see cref="AllowedMailDomains"/> (IgnoreCase).</summary>
    private static bool IsSupportedMailDomain(string email)
    {
        var at = email.LastIndexOf('@');
        if (at < 0 || at == email.Length - 1) return false;
        var domain = email[(at + 1)..].Trim().ToLowerInvariant();
        return AllowedMailDomains.Contains(domain, StringComparer.Ordinal);
    }

    /// <summary>Đăng nhập hộp thư Microsoft: username → (form Fluent "Xác minh email" → "Các cách khác" → tile
    /// "Nhập mật khẩu") → password → KMSI "Có". MỖI bước "thấy thì làm, không thấy thì sang bước sau" (timeout
    /// ngắn). Trả false khi phát hiện lỗi tài khoản/mật khẩu (#usernameError/#passwordError); best-effort true
    /// còn lại (caller kiểm URL sau khi vào Outlook để chắc đã login). KHÔNG log giá trị mật khẩu.</summary>
    private static async Task<bool> LoginOutlookAsync(
        IPage page, string email, string emailPassword, Action<string>? log, CancellationToken ct)
    {
        void L(string m) => log?.Invoke(m);

        // 0) Có thể mở ra trang landing (chưa vào form nhập email) → bấm "Đăng nhập"/"Sign in" trước.
        var userField = await WaitFirstVisibleAsync(page, MsUserSelectors, 6000, ct).ConfigureAwait(false);
        if (userField is null)
        {
            var signIn = await FindByRegexTextAsync(page, MsSignInSelectors, SignInRegex, 4000, ct).ConfigureAwait(false);
            if (signIn is not null)
            {
                L("Chưa vào form đăng nhập — bấm 'Đăng nhập'…");
                try { await signIn.ClickAsync().ConfigureAwait(false); } catch { }
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
            userField = await WaitFirstVisibleAsync(page, MsUserSelectors, 15000, ct).ConfigureAwait(false);
        }

        // 1) Username.
        if (userField is not null)
        {
            L("Nhập email đăng nhập hộp thư…");
            try { await userField.FillAsync(email).ConfigureAwait(false); } catch { }
            var next = await WaitFirstVisibleAsync(page, MsSubmitSelectors, 3000, ct).ConfigureAwait(false);
            if (next is not null) { try { await next.ClickAsync().ConfigureAwait(false); } catch { } }
            await Task.Delay(2000, ct).ConfigureAwait(false);

            if (await IsSelectorVisibleAsync(page, "#usernameError").ConfigureAwait(false))
            {
                L("Email hộp thư không hợp lệ (Microsoft báo lỗi tài khoản).");
                return false;
            }
        }

        // 2) Đưa về Ô MẬT KHẨU. Microsoft redirect nhiều bước + form Fluent "Xác minh email" render trễ → POLL ~45s,
        //    mỗi vòng: (a) thấy ô mật khẩu → xong; (b) thấy tile "Nhập mật khẩu"/"Dùng mật khẩu" → click; (c) thấy
        //    "Các cách khác để đăng nhập" (form passwordless) → click (vòng sau sẽ thấy tile mật khẩu). Khớp text
        //    KHÔNG dấu để tránh lỗi NFC/NFD (text MS dạng tổ hợp dấu).
        IElementHandle? passField = null;
        var passDeadline = DateTime.UtcNow.AddSeconds(45);
        var clickedOtherWays = false;
        while (DateTime.UtcNow < passDeadline)
        {
            ct.ThrowIfCancellationRequested();

            passField = await FindFirstVisibleHandleAsync(page, MsPasswordSelectors, 1500, ct).ConfigureAwait(false);
            if (passField is not null) break;

            var usePwd = await FindByNormalizedTextInFramesAsync(
                page, MsUsePasswordSelectors, new[] { "mat khau", "password", "contrasena" }, 1200, ct).ConfigureAwait(false);
            if (usePwd is not null)
            {
                L("Chọn 'Dùng mật khẩu' / 'Nhập mật khẩu'…");
                try { await usePwd.ClickAsync().ConfigureAwait(false); } catch { }
                await Task.Delay(1500, ct).ConfigureAwait(false);
                continue;
            }

            var otherWays = await FindByNormalizedTextInFramesAsync(
                page, MsOtherWaysSelectors,
                new[] { "cach khac de dang nhap", "other ways to sign in", "otras formas de iniciar sesion" },
                1200, ct).ConfigureAwait(false);
            if (otherWays is not null)
            {
                L("Form 'Xác minh email' — bấm 'Các cách khác để đăng nhập'…");
                try { await otherWays.ClickAsync().ConfigureAwait(false); } catch { }
                clickedOtherWays = true;
                await Task.Delay(1500, ct).ConfigureAwait(false);
                continue;
            }

            await Task.Delay(1500, ct).ConfigureAwait(false);
        }

        if (passField is null)
        {
            L($"Không đưa được về ô mật khẩu sau 45s ({(clickedOtherWays ? "đã bấm 'Các cách khác' nhưng không thấy tile Mật khẩu" : "không thấy 'Các cách khác'/ô mật khẩu")}; URL: {page.Url}).");
            return true; // best-effort — caller kiểm URL sau Outlook; có thể phiên đã sẵn đăng nhập.
        }

        // 3) Password (KHÔNG log giá trị).
        L("Nhập mật khẩu hộp thư…");
        try { await passField.FillAsync(emailPassword).ConfigureAwait(false); } catch { }
        var signInBtn = await FindFirstVisibleHandleAsync(page, MsSubmitSelectors, 3000, ct).ConfigureAwait(false);
        if (signInBtn is not null) { try { await signInBtn.ClickAsync().ConfigureAwait(false); } catch { } }
        await Task.Delay(3000, ct).ConfigureAwait(false);

        if (await IsSelectorVisibleAsync(page, "#passwordError").ConfigureAwait(false))
        {
            L("Sai mật khẩu hộp thư (Microsoft báo lỗi).");
            return false;
        }

        // 4) "Duy trì đăng nhập?" (KMSI) → bấm "Có". Form Fluent MỚI: nút "Có" là [data-testid='primaryButton'] —
        //    nhưng nhiều form khác cũng có primaryButton (vd "Gửi mã") nên CHỈ bấm khi CHẮC đang ở KMSI, nhận diện
        //    qua testid ỔN ĐỊNH kmsiVideo/kmsiImage (không phụ thuộc ngôn ngữ). Bản cũ: #acceptButton/#idSIButton9.
        await Task.Delay(1500, ct).ConfigureAwait(false);
        var kmsiDeadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < kmsiDeadline)
        {
            ct.ThrowIfCancellationRequested();
            var onKmsi = await IsAnyVisibleByClientRectsAsync(
                page, new[] { "[data-testid='kmsiVideo']", "[data-testid='kmsiImage']" }, ct).ConfigureAwait(false);
            var kmsiSelectors = onKmsi
                ? new[] { "[data-testid='primaryButton']", "#acceptButton", "#idSIButton9" }
                : MsKmsiYesSelectors;
            var kmsi = await FindFirstVisibleHandleAsync(page, kmsiSelectors, 1000, ct).ConfigureAwait(false);
            if (kmsi is not null)
            {
                L("Bấm 'Có' để giữ đăng nhập hộp thư…");
                try { await kmsi.ClickAsync().ConfigureAwait(false); } catch { }
                await Task.Delay(2000, ct).ConfigureAwait(false);
                break;
            }
            await Task.Delay(700, ct).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>Trong hộp thư Outlook: poll ~3' tìm mã 6 số BigSeller mới nhất. Mỗi vòng: (1) quay lại outlook nếu
    /// bị đẩy sang m365; (2) đọc innerText toàn trang danh sách + trích mã; (3) mở mail BigSeller trên cùng, đọc
    /// nội dung (kể cả trong iframe reading-pane) + trích mã; (4) reload + chờ mail tới. Không thấy trong hạn → null.</summary>
    private static async Task<string?> ReadBigSellerCodeAsync(IPage page, Action<string>? log, CancellationToken ct)
    {
        void L(string m) => log?.Invoke(m);
        var deadline = DateTime.UtcNow.AddMinutes(3);
        await Task.Delay(3000, ct).ConfigureAwait(false); // chờ danh sách mail render lần đầu

        var round = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            round++;

            // Sau đăng nhập, Microsoft đôi khi đẩy KHỎI Outlook sang home M365 (m365.cloud.microsoft) → quay lại.
            var url = page.Url ?? string.Empty;
            if (!url.Contains("outlook", StringComparison.OrdinalIgnoreCase))
            {
                L("Không ở Outlook (m365?) — điều hướng lại hộp thư…");
                try
                {
                    await page.GotoAsync("https://outlook.live.com/mail/0/",
                        new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 }).ConfigureAwait(false);
                }
                catch { /* nuốt lỗi điều hướng */ }
                await Task.Delay(2500, ct).ConfigureAwait(false);
            }

            // (2) Đọc thẳng innerText toàn trang danh sách — nhanh, bền; preview mail thường đã kèm mã. CHỈ trích khi
            // trang danh sách THẬT SỰ chứa "bigseller" (siết false-positive: tránh nhặt số 6 chữ số của mail khác khi
            // trang chỉ tình cờ có chữ "code"/"verification" — điền mã SAI có thể khiến BigSeller khoá/nghi ngờ). Mã chỉ
            // nằm trong thân mail (không lộ ở preview) → nhường nhánh (3) mở đúng mail đọc.
            var listText = await ReadBodyTextAsync(page).ConfigureAwait(false);
            if (listText.Contains("bigseller", StringComparison.OrdinalIgnoreCase))
            {
                var codeFromList = ExtractBigSellerCode(listText);
                if (codeFromList is not null) { L("Đã đọc được mã BigSeller từ danh sách mail."); return codeFromList; }
            }

            // (3) Mở mail BigSeller trên cùng để đọc nội dung đầy đủ (mã có thể chỉ nằm trong thân mail).
            var row = await FindTopBigSellerRowAsync(page, ct).ConfigureAwait(false);
            if (row is not null)
            {
                try { await row.ClickAsync().ConfigureAwait(false); } catch { }
                await Task.Delay(2000, ct).ConfigureAwait(false);
                var paneText = await ReadAllFramesTextAsync(page).ConfigureAwait(false);
                var code = ExtractBigSellerCode(paneText);
                if (code is not null) { L("Đã đọc được mã BigSeller từ nội dung mail."); return code; }
            }

            // (4) Chưa thấy → reload chờ mail (mới) tới.
            L($"Vòng {round}: chưa thấy mã BigSeller — tải lại, chờ mail…");
            try
            {
                await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 }).ConfigureAwait(false);
            }
            catch { /* nuốt lỗi reload */ }
            await Task.Delay(10000, ct).ConfigureAwait(false);
        }

        L("Hết thời gian chờ mã BigSeller trong hộp thư.");
        return null;
    }

    /// <summary>Trích mã 6 số từ <paramref name="text"/> CHỈ khi text có dấu hiệu là mail xác minh BigSeller
    /// (chứa "bigseller"/"verification"/"verify"/"code"/"otp"/"mã xác"). Nhiều số 6 chữ số → ưu tiên số gần cụm
    /// từ khoá mã. Không thoả → null (tránh nhặt nhầm số ngày/giờ trong danh sách).</summary>
    private static string? ExtractBigSellerCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lower = text.ToLowerInvariant();
        var looksBigSeller = lower.Contains("bigseller")
            || lower.Contains("verification") || lower.Contains("verify")
            || lower.Contains("code") || lower.Contains("otp")
            || lower.Contains("ma xac") || lower.Contains("mã xác");
        if (!looksBigSeller) return null;

        var matches = SixDigitRegex.Matches(text);
        if (matches.Count == 0) return null;
        if (matches.Count == 1) return matches[0].Groups[1].Value;

        // Nhiều mã ứng viên → chọn số 6 chữ số GẦN NHẤT một cụm từ khoá mã.
        string[] keywords = { "code", "verification", "verify", "otp", "ma xac", "mã xác" };
        var bestDist = int.MaxValue;
        string? best = null;
        foreach (Match m in matches)
        {
            foreach (var kw in keywords)
            {
                var ki = lower.IndexOf(kw, StringComparison.Ordinal);
                while (ki >= 0)
                {
                    var dist = Math.Abs(m.Index - ki);
                    if (dist < bestDist) { bestDist = dist; best = m.Groups[1].Value; }
                    ki = lower.IndexOf(kw, ki + 1, StringComparison.Ordinal);
                }
            }
        }
        return best ?? matches[0].Groups[1].Value;
    }

    /// <summary>Dòng mail BigSeller đầu tiên (mới nhất) ĐANG HIỂN THỊ — người gửi/tiêu đề/preview chứa "bigseller"
    /// hoặc từ khoá mã. Đổi hành động so với luồng Shopee: chỉ TRẢ dòng để caller mở đọc text (KHÔNG click link).</summary>
    private static async Task<IElementHandle?> FindTopBigSellerRowAsync(IPage page, CancellationToken ct)
    {
        foreach (var sel in new[] { "div[role='option']", "div[role='listitem']", "div[role='row']", "[data-convid]" })
        {
            IReadOnlyList<IElementHandle> els;
            try { els = await page.QuerySelectorAllAsync(sel).ConfigureAwait(false); }
            catch { continue; }

            foreach (var el in els)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (!await IsElementVisibleByClientRectsAsync(el).ConfigureAwait(false)) continue;
                    var txt = ((await el.InnerTextAsync().ConfigureAwait(false)) ?? string.Empty).ToLowerInvariant();
                    if (txt.Contains("bigseller")
                        || txt.Contains("verification") || txt.Contains("verify")
                        || txt.Contains("mã xác") || txt.Contains("ma xac"))
                    {
                        return el; // dòng đầu tiên = mới nhất theo thứ tự DOM
                    }
                }
                catch { /* detached / lỗi đọc — bỏ qua dòng này */ }
            }
        }
        return null;
    }

    // ===================== Helper dò/đọc (port rút gọn từ ShopeeLoginService) =====================

    /// <summary>innerText của body trang chính (rỗng nếu lỗi).</summary>
    private static async Task<string> ReadBodyTextAsync(IPage page)
    {
        try { return await page.EvaluateAsync<string>("() => document.body ? document.body.innerText : ''").ConfigureAwait(false) ?? ""; }
        catch { return ""; }
    }

    /// <summary>Nối innerText body của MỌI frame (thân mail Outlook thường nằm trong iframe reading-pane).</summary>
    private static async Task<string> ReadAllFramesTextAsync(IPage page)
    {
        var sb = new StringBuilder();
        foreach (var frame in page.Frames)
        {
            try { sb.Append(await frame.EvaluateAsync<string>("() => document.body ? document.body.innerText : ''").ConfigureAwait(false)).Append('\n'); }
            catch { /* frame cross-origin / detached — bỏ qua */ }
        }
        return sb.ToString();
    }

    /// <summary>Locator đầu tiên (theo thứ tự <paramref name="selectors"/>) đang HIỂN THỊ, poll tới hết
    /// <paramref name="timeoutMs"/>. Dùng cho ô/nút đơn giản (email/password/submit) — locator API thẳng.</summary>
    private static async Task<ILocator?> WaitFirstVisibleAsync(IPage page, string[] selectors, int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        do
        {
            ct.ThrowIfCancellationRequested();
            foreach (var sel in selectors)
            {
                try
                {
                    var loc = page.Locator(sel).First;
                    if (await loc.IsVisibleAsync().ConfigureAwait(false)) return loc;
                }
                catch { /* selector không dùng được — thử selector kế */ }
            }
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        while (DateTime.UtcNow < deadline);
        return null;
    }

    /// <summary>Element handle đầu tiên đang HIỂN THỊ (getClientRects) khớp một trong <paramref name="selectors"/>,
    /// poll tới hết <paramref name="timeoutMs"/>. Trả handle (cần cho các bước dùng chung với text-finder).</summary>
    private static async Task<IElementHandle?> FindFirstVisibleHandleAsync(IPage page, string[] selectors, int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        do
        {
            ct.ThrowIfCancellationRequested();
            foreach (var sel in selectors)
            {
                try
                {
                    var el = await page.QuerySelectorAsync(sel).ConfigureAwait(false);
                    if (el is not null && await IsElementVisibleByClientRectsAsync(el).ConfigureAwait(false)) return el;
                }
                catch { /* selector không dùng được — thử selector kế */ }
            }
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        while (DateTime.UtcNow < deadline);
        return null;
    }

    /// <summary>Element hiển thị đầu tiên khớp selector VÀ innerText khớp <paramref name="textRegex"/> (chỉ frame
    /// chính) — dùng cho nút "Đăng nhập" trang landing.</summary>
    private static async Task<IElementHandle?> FindByRegexTextAsync(IPage page, string[] selectors, Regex textRegex, int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        do
        {
            ct.ThrowIfCancellationRequested();
            foreach (var sel in selectors)
            {
                IReadOnlyList<IElementHandle> els;
                try { els = await page.QuerySelectorAllAsync(sel).ConfigureAwait(false); }
                catch { continue; }
                foreach (var el in els)
                {
                    try
                    {
                        if (!await IsElementVisibleByClientRectsAsync(el).ConfigureAwait(false)) continue;
                        var txt = await el.InnerTextAsync().ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(txt) && textRegex.IsMatch(txt)) return el;
                    }
                    catch { /* detached — bỏ qua */ }
                }
            }
            await Task.Delay(300, ct).ConfigureAwait(false);
        }
        while (DateTime.UtcNow < deadline);
        return null;
    }

    /// <summary>Element hiển thị đầu tiên (quét MỌI frame) khớp selector VÀ innerText CHUẨN HÓA không dấu CHỨA một
    /// trong <paramref name="normalizedNeedles"/> (đã ở dạng không dấu, chữ thường). TRỊ lỗi NFC/NFD của text
    /// tiếng Việt trên form Microsoft — dùng cho "Các cách khác" / tile "Nhập mật khẩu".</summary>
    private static async Task<IElementHandle?> FindByNormalizedTextInFramesAsync(
        IPage page, string[] selectors, string[] normalizedNeedles, int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        do
        {
            ct.ThrowIfCancellationRequested();
            foreach (var frame in page.Frames)
            {
                foreach (var sel in selectors)
                {
                    IReadOnlyList<IElementHandle> els;
                    try { els = await frame.QuerySelectorAllAsync(sel).ConfigureAwait(false); }
                    catch { continue; }
                    foreach (var el in els)
                    {
                        try
                        {
                            if (!await IsElementVisibleByClientRectsAsync(el).ConfigureAwait(false)) continue;
                            var txt = NormalizeForMatch(await el.InnerTextAsync().ConfigureAwait(false));
                            if (txt.Length > 0 && Array.Exists(normalizedNeedles, n => txt.Contains(n, StringComparison.Ordinal)))
                                return el;
                        }
                        catch { /* detached — bỏ qua */ }
                    }
                }
            }
            await Task.Delay(300, ct).ConfigureAwait(false);
        }
        while (DateTime.UtcNow < deadline);
        return null;
    }

    /// <summary>True nếu có ÍT NHẤT một phần tử khớp một trong <paramref name="selectors"/> đang HIỂN THỊ
    /// (getClientRects &gt; 0). Một lượt quét, không poll. Dùng nhận diện form KMSI qua testid.</summary>
    private static async Task<bool> IsAnyVisibleByClientRectsAsync(IPage page, string[] selectors, CancellationToken ct)
    {
        foreach (var sel in selectors)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var visible = await page.EvaluateAsync<bool>(
                    @"(sel) => { for (const el of document.querySelectorAll(sel)) { const rs = el.getClientRects();"
                    + " for (const r of rs) { if (r.width > 0 && r.height > 0) return true; } } return false; }",
                    sel).ConfigureAwait(false);
                if (visible) return true;
            }
            catch { /* selector không dùng được — thử selector kế */ }
        }
        return false;
    }

    /// <summary>True nếu <paramref name="el"/> đang HIỂN THỊ (getClientRects có kích thước &gt; 0). Chạy trong
    /// document của frame chứa el (kể cả iframe).</summary>
    private static async Task<bool> IsElementVisibleByClientRectsAsync(IElementHandle el)
    {
        try
        {
            return await el.EvaluateAsync<bool>(
                "(node) => { const rs = node.getClientRects(); for (const r of rs) { if (r.width > 0 && r.height > 0) return true; } return false; }")
                .ConfigureAwait(false);
        }
        catch { return false; }
    }

    /// <summary>True nếu <paramref name="selector"/> có phần tử ĐANG HIỂN THỊ (dùng cho error box Microsoft).</summary>
    private static async Task<bool> IsSelectorVisibleAsync(IPage page, string selector)
    {
        try
        {
            var el = await page.QuerySelectorAsync(selector).ConfigureAwait(false);
            return el is not null && await IsElementVisibleByClientRectsAsync(el).ConfigureAwait(false);
        }
        catch { return false; }
    }

    /// <summary>Chuẩn hóa text để so khớp bền: bỏ dấu tiếng Việt (kể cả đ→d), gộp khoảng trắng, hạ chữ thường.
    /// PORT từ ShopeeLoginService.NormalizeForMatch — trị lỗi NFC/NFD của text form Microsoft.</summary>
    private static string NormalizeForMatch(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;

        var collapsed = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var decomposed = collapsed.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            switch (ch)
            {
                case 'đ': sb.Append('d'); break;
                case 'Đ': sb.Append('D'); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }
}
