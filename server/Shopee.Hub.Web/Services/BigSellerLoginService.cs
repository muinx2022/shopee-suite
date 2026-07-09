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

    /// <summary>Bắt đầu login cho 1 acc (fire-and-forget). Trả false nếu đang chạy.</summary>
    public bool Start(string acctId, string email, string password)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(acctId, out var ex) && ex.State.Status is "running" or "needsOtp") return false;
            var s = new Session();
            s.State.Status = "running";
            _sessions[acctId] = s;
            _ = Task.Run(() => RunAsync(acctId, email, password, s));
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

    private async Task RunAsync(string acctId, string email, string password, Session s)
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
            var page = await ctx.NewPageAsync();

            var ai = _config.Ai();
            if (!ai.HasActiveKey) { Say(s, "✘ Chưa cấu hình API key AI (tab AI) để giải captcha."); Fail(s); return; }

            // RunFormLoginAsync tự điều hướng tới trang login + phát hiện "đã đăng nhập sẵn" (url không chứa
            // "login" → Success ngay, khỏi captcha) → FillLoginLoop trả true → xuống CaptureAndSave như cũ.
            var ok = await FillLoginLoopAsync(page, email, password, ai, s, ct);
            if (!ok) return;   // FillLoginLoop tự set trạng thái (needsOtp→success, hoặc failed)

            await CaptureAndSaveAsync(acctId, ctx, s);
        }
        catch (OperationCanceledException) { Say(s, "■ Đã huỷ."); Fail(s); }
        catch (Exception ex) { Say(s, "✘ Lỗi: " + ex.Message); Fail(s); }
        finally { if (ctx is not null) { try { await ctx.CloseAsync(); } catch { } } }
    }

    /// <summary>Vòng điền form + captcha — nay ủy cho lõi chung <see cref="BigSellerLoginForm.RunFormLoginAsync"/>.
    /// Rẽ nhánh trang "security" (thiết bị mới) đưa về <see cref="HandleOtpAsync"/> (dừng chờ admin nhập OTP).
    /// Trả true nếu vào được (caller bắt cookie); false nếu đã Fail.</summary>
    private async Task<bool> FillLoginLoopAsync(IPage page, string email, string password, AiConfig ai, Session s, CancellationToken ct)
    {
        var outcome = await BigSellerLoginForm.RunFormLoginAsync(
            page, email, password, ai, msg => Say(s, msg), ct,
            maxAttempts: 5, onSecurityChallenge: (p, c) => HandleOtpAsync(p, s, c));
        if (outcome == BigSellerLoginOutcome.Success) return true;
        Fail(s);
        return false;
    }

    /// <summary>Trang security đòi mã email: dump gợi ý DOM ra log, chờ admin nhập OTP (5'), điền + submit, kiểm tra vào được.</summary>
    private async Task<bool> HandleOtpAsync(IPage page, Session s, CancellationToken ct)
    {
        // Trang "Account Verification": 6 ô mã (.el-input__inner maxlength=1) + Cloudflare Turnstile + nút
        // Send Code→Confirm + countdown gửi-lại. Bước 1: bấm "Send Code" (nếu còn) để gửi mã 6 số về EMAIL acc.
        var sendBtn = page.Locator("button.submit-btn-send, button:has-text('Send Code')");
        if (await sendBtn.CountAsync() > 0)
        {
            try { await sendBtn.First.ClickAsync(new() { Timeout = 4000 }); } catch { }
            await Task.Delay(1500, ct);
        }

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
