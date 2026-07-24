using Microsoft.Playwright;
using Shopee.Core.Ai;
using Shopee.Core.BigSeller;
using Shopee.Hub;

namespace Shopee.Hub.Web.Services;

/// <summary>Trạng thái 1 phiên login BigSeller trên hub (cho web UI hiển thị + điều khiển).</summary>
public sealed class LoginState
{
    public string Status { get; set; } = "idle";   // idle | running | needsOtp | success | failed
    public bool NeedsOtp => Status == "needsOtp";
    public List<string> Log { get; } = new();
    public string Message { get; set; } = "";
}

/// <summary>
/// Login BigSeller NGAY TRÊN HUB bằng Playwright/Chromium headless: điền email/mật khẩu → OpenAI giải captcha →
/// submit; nếu BigSeller đòi mã email (thiết bị mới) thì DỪNG chờ admin nhập OTP trên web → điền tiếp → khi vào
/// được thì bắt TOÀN BỘ cookie bigseller (gồm device-trust) ghi vào kho <c>cookies/{acctId}.json</c>. Client kéo
/// device-trust về → tự re-login captcha-only, KHÔNG bao giờ dính verify code. Port logic từ BigSellerAutoLogin.
/// Mỗi acc 1 phiên; giữ browser mở trong lúc chờ OTP (timeout 5').
/// </summary>
public sealed class BigSellerLoginService : IAsyncDisposable
{
    private const string LoginUrl = "https://www.bigseller.com/en_US/login.htm";
    private const string AuthCookieName = "muc_token";
    // Cookie KHÔNG seed lại trước khi login: muc_token + JSESSIONID là auth theo-ngữ-cảnh — nạp lại thì BigSeller
    // BOUNCE (Test A 2026-07-03) làm login hỏng. Bỏ chúng để ép form-login captcha-only (mint token mới) NHƯNG giữ
    // fingerPrint & phần còn lại → BigSeller không coi là thiết bị mới → KHÔNG đòi mã email (OTP).
    private static readonly string[] SkipSeedCookies = { AuthCookieName, "JSESSIONID" };

    private readonly HubDatabase _db;
    private readonly FileStoreConfigService _config;
    private readonly ILogger<BigSellerLoginService> _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = new();

    private IPlaywright? _pw;
    private IBrowser? _browser;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public BigSellerLoginService(HubDatabase db, FileStoreConfigService config, ILogger<BigSellerLoginService> log)
    { _db = db; _config = config; _log = log; }

    private sealed class Session
    {
        public LoginState State { get; } = new();
        public TaskCompletionSource<string> Otp { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Cts { get; } = new();
    }

    public LoginState? GetState(string acctId)
    {
        lock (_gate) return _sessions.TryGetValue(acctId, out var s) ? s.State : null;
    }

    /// <summary>Có phiên login nào đang chạy/chờ OTP không — trang Config dùng để chỉ re-render khi cần.</summary>
    public bool AnyActive
    {
        get { lock (_gate) return _sessions.Values.Any(s => s.State.Status is "running" or "needsOtp"); }
    }

    /// <summary>Bắt đầu login cho 1 acc (fire-and-forget). Trả false nếu đang chạy. <paramref name="emailPassword"/>
    /// (mật khẩu hòm thư Hotmail/Outlook của acc) để hub TỰ đọc mã verify — rỗng thì fallback chờ admin gõ tay.</summary>
    public bool Start(string acctId, string email, string password, string emailPassword)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(acctId, out var ex) && ex.State.Status is "running" or "needsOtp") return false;
            var s = new Session();
            s.State.Status = "running";
            _sessions[acctId] = s;
            _ = Task.Run(() => RunAsync(acctId, email, password, emailPassword, s));
            return true;
        }
    }

    /// <summary>Admin nhập mã OTP (email code) → tiếp tục phiên đang chờ.</summary>
    public void SubmitOtp(string acctId, string code)
    {
        lock (_gate)
            if (_sessions.TryGetValue(acctId, out var s) && s.State.Status == "needsOtp")
                s.Otp.TrySetResult(code.Trim());
    }

    public void Cancel(string acctId)
    {
        lock (_gate)
            if (_sessions.TryGetValue(acctId, out var s)) { s.Cts.Cancel(); s.Otp.TrySetCanceled(); }
    }

    private void Say(Session s, string msg)
    {
        lock (_gate) { s.State.Log.Add($"{DateTimeOffset.Now:HH:mm:ss} {msg}"); if (s.State.Log.Count > 200) s.State.Log.RemoveAt(0); s.State.Message = msg; }
        _log.LogInformation("bs-login: {Msg}", msg);
    }

    private async Task EnsureBrowserAsync(CancellationToken ct)
    {
        if (_browser is { IsConnected: true }) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_browser is { IsConnected: true }) return;
            // Chromium đã crash/rớt kết nối → đóng bản chết rồi launch lại (thay vì hỏng mọi login tới khi restart app).
            if (_browser is not null) { try { await _browser.CloseAsync(); } catch { } _browser = null; }
            _pw ??= await Playwright.CreateAsync();
            _browser = await _pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = new[] { "--no-sandbox", "--disable-dev-shm-usage", "--disable-gpu" },
            });
        }
        finally { _initLock.Release(); }
    }

    private async Task RunAsync(string acctId, string email, string password, string emailPassword, Session s)
    {
        var ct = s.Cts.Token;
        IBrowserContext? ctx = null;
        try
        {
            Say(s, "Khởi động Chromium…");
            await EnsureBrowserAsync(ct);
            ctx = await _browser!.NewContextAsync(new BrowserNewContextOptions
            {
                Locale = "en-US",
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
                ViewportSize = new ViewportSize { Width = 1366, Height = 768 },
            });
            // Nạp lại device-trust đã lưu TRƯỚC khi login (điểm A) → tránh BigSeller coi là thiết bị mới → hết OTP oan.
            await SeedDeviceTrustCookiesAsync(acctId, ctx, s);
            var page = await ctx.NewPageAsync();

            var ai = _config.Ai();
            if (!ai.HasActiveKey) { Say(s, "✘ Chưa cấu hình API key AI (tab AI) để giải captcha."); Fail(s); return; }

            // RunFormLoginAsync tự điều hướng tới trang login + phát hiện "đã đăng nhập sẵn" (url không chứa
            // "login" → Success ngay, khỏi captcha) → FillLoginLoop trả true → xuống CaptureAndSave như cũ.
            var ok = await FillLoginLoopAsync(page, email, password, emailPassword, ai, s, ct);
            if (!ok) return;   // FillLoginLoop tự set trạng thái (needsOtp→success, hoặc failed)

            await CaptureAndSaveAsync(acctId, ctx, s);
        }
        catch (OperationCanceledException) { Say(s, "■ Đã huỷ."); Fail(s); }
        catch (Exception ex) { Say(s, "✘ Lỗi: " + ex.Message); Fail(s); }
        finally { if (ctx is not null) { try { await ctx.CloseAsync(); } catch { } } }
    }

    /// <summary>Nạp lại device-trust từ kho <c>cookies/{acctId}.json</c> (format do CaptureAndSaveAsync ghi) vào
    /// context TRƯỚC khi login. Verify 2026-07-03: nạp bộ cookie cũ TRỪ muc_token/JSESSIONID → login chỉ captcha,
    /// KHÔNG đòi mã email; giữ fingerPrint = không bị coi thiết bị mới. Lỗi đọc/parse → log + đi tiếp (context trắng
    /// như cũ, có thể phải nhập OTP). Bỏ cookie đã hết hạn (Playwright coi vô nghĩa/ném).</summary>
    private async Task SeedDeviceTrustCookiesAsync(string acctId, IBrowserContext ctx, Session s)
    {
        try
        {
            var bytes = _db.ReadFile($"cookies/{acctId}.json");
            if (bytes is null || bytes.Length == 0) { Say(s, "• Chưa có cookie đã lưu → login từ đầu (có thể cần mã email)."); return; }
            using var doc = JsonDocument.Parse(bytes);
            if (!doc.RootElement.TryGetProperty("cookies", out var arr) || arr.ValueKind != JsonValueKind.Array)
            { Say(s, "• File cookie không có mảng 'cookies' → login từ đầu."); return; }

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var seed = new List<Cookie>();
            var skippedExpired = 0;
            foreach (var c in arr.EnumerateArray())
            {
                var name = c.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                if (name.Length == 0) continue;
                if (SkipSeedCookies.Contains(name, StringComparer.Ordinal)) continue;   // bỏ auth token (bounce)
                var domain = c.TryGetProperty("domain", out var dEl) ? dEl.GetString() ?? "" : "";
                if (domain.Length == 0) continue;

                var cookie = new Cookie
                {
                    Name = name,
                    Value = c.TryGetProperty("value", out var vEl) ? vEl.GetString() ?? "" : "",
                    Domain = domain,
                    Path = c.TryGetProperty("path", out var pEl) ? pEl.GetString() ?? "/" : "/",
                    Secure = c.TryGetProperty("secure", out var secEl) && secEl.ValueKind == JsonValueKind.True,
                    HttpOnly = c.TryGetProperty("httpOnly", out var hoEl) && hoEl.ValueKind == JsonValueKind.True,
                };
                // expires: null/<=0 = session cookie (không set). >0 mà đã quá hạn → BỎ (nạp lại vô nghĩa/gây lỗi).
                if (c.TryGetProperty("expires", out var exEl) && exEl.ValueKind == JsonValueKind.Number)
                {
                    var exp = exEl.GetInt64();
                    if (exp > 0)
                    {
                        if (exp < now) { skippedExpired++; continue; }
                        cookie.Expires = exp;
                    }
                }
                // sameSite string → enum; rỗng/không map được → bỏ qua field (dùng mặc định của Playwright).
                if (c.TryGetProperty("sameSite", out var ssEl) && ssEl.ValueKind == JsonValueKind.String)
                    cookie.SameSite = ssEl.GetString() switch
                    {
                        "Strict" => SameSiteAttribute.Strict,
                        "Lax" => SameSiteAttribute.Lax,
                        "None" => SameSiteAttribute.None,
                        _ => null,
                    };
                seed.Add(cookie);
            }

            if (seed.Count == 0) { Say(s, "• Cookie đã lưu rỗng/hết hạn hết → login từ đầu."); return; }
            await ctx.AddCookiesAsync(seed);
            Say(s, $"• Nạp {seed.Count} cookie device-trust (bỏ muc_token/JSESSIONID{(skippedExpired > 0 ? $" + {skippedExpired} cookie hết hạn" : "")}) → login captcha-only nếu trust còn.");
        }
        catch (Exception ex) { Say(s, "• Không nạp được cookie đã lưu (" + ex.Message + ") → login từ đầu."); }
    }

    /// <summary>Vòng điền form + captcha — nay ủy cho lõi chung <see cref="BigSellerLoginForm.RunFormLoginAsync"/>.
    /// Rẽ nhánh trang "security" (thiết bị mới) đưa về <see cref="HandleOtpAsync"/> (dừng chờ admin nhập OTP).
    /// Trả true nếu vào được (caller bắt cookie); false nếu đã Fail.</summary>
    private async Task<bool> FillLoginLoopAsync(IPage page, string email, string password, string emailPassword, AiConfig ai, Session s, CancellationToken ct)
    {
        var outcome = await BigSellerLoginForm.RunFormLoginAsync(
            page, email, password, ai, msg => Say(s, msg), ct,
            maxAttempts: 5, onSecurityChallenge: (p, c) => HandleOtpAsync(p, s, email, emailPassword, c));
        if (outcome == BigSellerLoginOutcome.Success) return true;
        Fail(s);
        return false;
    }

    /// <summary>Trang security đòi mã email: bấm "Send Code" → THỬ tự mở Hotmail đọc mã (nếu có mật khẩu email) →
    /// ra mã thì tự điền + submit; KHÔNG tự đọc được thì FALLBACK về đường cũ (dump DOM, chờ admin gõ tay 10',
    /// điền + submit). Cả 2 nhánh dùng chung <see cref="SubmitCodeAsync"/>.</summary>
    private async Task<bool> HandleOtpAsync(IPage page, Session s, string email, string emailPassword, CancellationToken ct)
    {
        // Trang "Account Verification": 6 ô mã (.el-input__inner maxlength=1) + Cloudflare Turnstile + nút
        // Send Code→Confirm + countdown gửi-lại. Bước 1: bấm "Send Code" (nếu còn) để gửi mã 6 số về EMAIL acc.
        var sendBtn = page.Locator("button.submit-btn-send, button:has-text('Send Code')");
        if (await sendBtn.CountAsync() > 0)
        {
            try { await sendBtn.First.ClickAsync(new() { Timeout = 4000 }); } catch { }
            await Task.Delay(1500, ct);
        }

        // NHÁNH TỰ ĐỌC (THÊM, không thay thế): có mật khẩu email → thử hub tự mở Hotmail đọc mã trước khi phiền admin.
        if (!string.IsNullOrWhiteSpace(emailPassword))
        {
            Say(s, "Thử tự mở Hotmail đọc mã…");
            string? auto = null;
            try { auto = await HotmailOtpReader.TryReadCodeAsync(page.Context, email, emailPassword, m => Say(s, m), ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Say(s, "• Tự đọc mã lỗi: " + ex.Message); }
            if (!string.IsNullOrWhiteSpace(auto))
            {
                var autoDigits = new string(auto.Where(char.IsLetterOrDigit).ToArray());
                Say(s, "✔ Tự đọc được mã từ hộp thư — điền tự động (không cần admin gõ tay).");
                return await SubmitCodeAsync(page, s, autoDigits, ct);
            }
            Say(s, "• Không tự đọc được mã → chờ admin nhập tay.");
        }

        // FALLBACK (BẤT BIẾN — giữ nguyên đường cũ): chờ admin gõ tay.
        lock (_gate) s.State.Status = "needsOtp";
        Say(s, "⚠ Mã 6 số đã gửi tới EMAIL của tài khoản. Mở email lấy mã, nhập vào đây rồi bấm 'Gửi mã'. (Nhập từ từ được — trang không hết hạn nhanh.)");
        await DumpDomAsync(page, s, "DOM (trang OTP)");

        // Chờ admin nhập OTP (tối đa 10').
        string otp;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try { otp = (await s.Otp.Task.WaitAsync(timeout.Token)).Trim(); }
        catch (OperationCanceledException) { Say(s, "✘ Hết giờ chờ OTP (10')."); Fail(s); return false; }

        lock (_gate) s.State.Status = "running";
        var digits = new string(otp.Where(char.IsLetterOrDigit).ToArray());
        return await SubmitCodeAsync(page, s, digits, ct);
    }

    /// <summary>Điền mã <paramref name="digits"/> vào 6 ô + chờ Cloudflare Turnstile/nút Confirm bật + click Confirm
    /// + kiểm URL đã qua. Dùng chung cho cả nhánh tự-đọc và nhánh admin-gõ-tay. True nếu đã qua trang security.</summary>
    private async Task<bool> SubmitCodeAsync(IPage page, Session s, string digits, CancellationToken ct)
    {
        Say(s, $"Đang điền mã '{digits}'…");
        try
        {
            var boxes = page.Locator(".verification-input-box input, .code-input input");
            var n = await boxes.CountAsync();
            if (n == 0)
            {
                await DumpDomAsync(page, s, "DOM (không thấy ô mã)");
                Say(s, "✘ Không thấy ô nhập mã. Gửi tôi DOM để chỉnh selector.");
                Fail(s); return false;
            }
            // Điền từng ô 1 số (maxlength=1). PressSequentially phát sự kiện keydown/input thật cho Vue nhận.
            for (var i = 0; i < digits.Length && i < n; i++)
            {
                try { await boxes.Nth(i).ClickAsync(new() { Timeout = 2000 }); await boxes.Nth(i).PressSequentiallyAsync(digits[i].ToString(), new() { Delay = 60 }); }
                catch { }
            }
            await Task.Delay(1200, ct);

            // Chờ Turnstile pass + nút Confirm (submit-btn, KHÁC send) BẬT. Tối đa 45s.
            Say(s, "Đã điền mã. Chờ Cloudflare Turnstile + nút Confirm bật…");
            var confirm = page.Locator("button.submit-btn:not(.submit-btn-send)");
            var enabled = false;
            for (var i = 0; i < 45 && !ct.IsCancellationRequested; i++)
            {
                try { if (await confirm.CountAsync() > 0 && !await confirm.First.IsDisabledAsync()) { enabled = true; break; } } catch { }
                await Task.Delay(1000, ct);
            }
            if (!enabled)
            {
                await DumpDomAsync(page, s, "DOM (Confirm vẫn khoá)");
                Say(s, "✘ Nút Confirm KHÔNG bật sau 45s — nhiều khả năng Cloudflare Turnstile chặn trình duyệt tự động (headless). Báo tôi để chuyển sang chạy Chromium có màn hình ảo (Xvfb) — Turnstile dễ pass hơn.");
                Fail(s); return false;
            }
            await confirm.First.ClickAsync(new() { Timeout = 5000 });
            await Task.Delay(5000, ct);

            var url = page.Url ?? "";
            if (!url.Contains("login", StringComparison.OrdinalIgnoreCase) && !url.Contains("security", StringComparison.OrdinalIgnoreCase))
            { Say(s, "✔ Xác nhận thành công — đã tạo device-trust."); return true; }

            await DumpDomAsync(page, s, "DOM (sau Confirm)");
            Say(s, "✘ Chưa qua sau khi bấm Confirm (mã sai hoặc còn 'security'). Gửi tôi DOM để soi.");
            Fail(s); return false;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Say(s, "✘ Lỗi nhập OTP: " + ex.Message); Fail(s); return false; }
    }

    private async Task DumpDomAsync(IPage page, Session s, string label)
    {
        try
        {
            var dom = await page.EvaluateAsync<string>(@"() => {
                const inp = [...document.querySelectorAll('input')].map(i => `[name=${i.name} type=${i.type} val='${i.value}']`).join(' ');
                const btn = [...document.querySelectorAll('button')].map(b => `[btn '${(b.innerText||'').trim()}' cls=${b.className} ${b.disabled?'disabled':''}]`).join(' ');
                return 'INPUTS ' + inp + ' || BUTTONS ' + btn;
            }");
            Say(s, label + ": " + dom);
        }
        catch { }
    }

    /// <summary>Bắt toàn bộ cookie bigseller từ context → ghi kho cookies/{acctId}.json (client kéo về).</summary>
    private async Task CaptureAndSaveAsync(string acctId, IBrowserContext ctx, Session s)
    {
        // Chờ cookie tracker nạp đủ (giống client: chờ số cookie ngừng tăng ~ vài giây) để phiên bền.
        await Task.Delay(4000, s.Cts.Token);
        var all = await ctx.CookiesAsync();
        var bs = all.Where(c => (c.Domain ?? "").Contains("bigseller", StringComparison.OrdinalIgnoreCase)).ToList();
        var hasAuth = bs.Any(c => c.Name == AuthCookieName && (c.Value?.Length ?? 0) > 5);
        if (!hasAuth) { Say(s, "✘ Vào được nhưng KHÔNG thấy muc_token — chưa lưu."); Fail(s); return; }

        var payload = new
        {
            exportedAt = DateTimeOffset.Now,
            cookies = bs.Select(c => new Dictionary<string, object?>
            {
                ["name"] = c.Name, ["value"] = c.Value, ["domain"] = c.Domain, ["path"] = c.Path,
                ["secure"] = c.Secure, ["httpOnly"] = c.HttpOnly,
                ["sameSite"] = c.SameSite == SameSiteAttribute.None ? "" : c.SameSite.ToString(),
                ["expires"] = c.Expires <= 0 ? (object?)null : (long)c.Expires,
            }).ToList(),
        };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        var bytes = new UTF8Encoding(false).GetBytes(json);
        var name = $"cookies/{acctId}.json";
        var res = _db.PutFile(name, bytes, null, "hub-web-login");
        if (res.Ok)
        {
            // Ghi CookieFile = TÊN FILE THUẦN vào config để client (kể cả Linux) RelinkCookie nối được cookie.
            try { _config.SetBigSellerCookieFile(acctId, $"{acctId}.json"); } catch { }
            Say(s, $"✔ Đã lưu {bs.Count} cookie → {name} (v{res.Version}). Client sẽ kéo device-trust về.");
            lock (_gate) s.State.Status = "success";
        }
        else { Say(s, "✘ Lỗi ghi cookie: " + res.Conflict); Fail(s); }
    }

    private void Fail(Session s) { lock (_gate) if (s.State.Status != "success") s.State.Status = "failed"; }

    /// <summary>Chẩn đoán không cần OTP: launch Chromium → mở trang login BigSeller → đọc captcha → giải bằng AI.
    /// Verify toàn bộ pipeline hạ tầng (Chromium + Playwright + captcha + OpenAI) trên VM.</summary>
    public async Task<string> DiagnoseAsync()
    {
        var sb = new StringBuilder();
        IBrowserContext? ctx = null;
        try
        {
            sb.AppendLine("launching chromium…");
            await EnsureBrowserAsync(CancellationToken.None);
            sb.AppendLine("browser OK: " + _browser!.Version);
            ctx = await _browser.NewContextAsync(new BrowserNewContextOptions { Locale = "en-US" });
            var page = await ctx.NewPageAsync();
            await page.GotoAsync(LoginUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 25000 });
            await Task.Delay(2500);
            sb.AppendLine($"url={page.Url}");
            sb.AppendLine($"title={await page.TitleAsync()}");
            var src = await page.GetAttributeAsync("img.comb-code-img", "src");
            var hasCaptcha = src?.Contains("base64,") == true;
            sb.AppendLine($"captcha-img-present={hasCaptcha}");
            var ai = _config.Ai();
            sb.AppendLine($"ai-provider={ai.Provider} ai-key={ai.HasActiveKey}");
            if (hasCaptcha && ai.HasActiveKey && src is not null)
            {
                var png = Convert.FromBase64String(src[(src.IndexOf("base64,", StringComparison.Ordinal) + 7)..]);
                var code = await BigSellerCaptchaSolver.SolveAsync(ai, png);
                sb.AppendLine($"captcha-solved='{code}'");
            }
        }
        catch (Exception ex) { sb.AppendLine("ERROR: " + ex.Message + "\n" + ex); }
        finally { if (ctx is not null) { try { await ctx.CloseAsync(); } catch { } } }
        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) { try { await _browser.CloseAsync(); } catch { } }
        _pw?.Dispose();
    }
}
