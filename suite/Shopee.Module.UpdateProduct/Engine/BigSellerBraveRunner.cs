using System.Diagnostics;
using Microsoft.Playwright;
using Shopee.Core.BigSeller;
using Shopee.Core.Browser;
using Shopee.Core.Cdp;

namespace UpdateProduct;

/// <summary>
/// Nền chung của 2 runner Playwright chạy trên Brave + BigSeller (Update SP + Import to Store):
/// phóng Brave (profile persistent, đăng ký fleet, Job Object chống rò), chờ CDP, kết nối Playwright qua
/// CDP, đảm bảo cookie/phiên BigSeller, throttle token write-back, và dọn dẹp (kill Brave + reaper) khi
/// dispose. Điểm khác giữa 2 runner (URL mở, xoá session-tab trước khi phóng, detach listener khi dispose,
/// cơ chế hồi phục/abort riêng) để lại subclass qua hook <see cref="StartUrl"/> /
/// <see cref="PrepareProfileBeforeLaunch"/> / <see cref="OnBeforeDispose"/>.
/// </summary>
internal abstract class BigSellerBraveRunner : IAsyncDisposable
{
    protected readonly BigSellerWorkflowSettings _settings;
    protected readonly Action<string> _log;
    protected readonly WorkflowPauseToken? _pauseToken;
    protected readonly bool _exportCookie;
    protected readonly BigSellerTokenWriteBack _tokenWriteBack = new();
    protected IPlaywright? _playwright;
    protected IBrowser? _browser;
    protected Process? _braveProcess;

    protected BigSellerBraveRunner(
        BigSellerWorkflowSettings settings, Action<string> log, WorkflowPauseToken? pauseToken, bool exportCookie)
    {
        _settings = settings;
        _log = log;
        _pauseToken = pauseToken;
        _exportCookie = exportCookie;
    }

    /// <summary>URL BigSeller mở lúc phóng Brave + dùng để probe/nạp phiên (Update: trang Listing; Import: Crawl List).</summary>
    protected abstract string StartUrl { get; }

    /// <summary>Hook trước khi phóng Brave (sau khi tạo thư mục profile). Mặc định no-op; Import xoá session-tab.</summary>
    protected virtual void PrepareProfileBeforeLaunch() { }

    /// <summary>Hook đầu <see cref="DisposeAsync"/> trước khi giết Brave. Mặc định no-op; Import detach listener API.</summary>
    protected virtual void OnBeforeDispose() { }

    protected void StartBrave()
    {
        Directory.CreateDirectory(_settings.ProfileDir);
        PrepareProfileBeforeLaunch();
        // Runner CDP lắp cờ theo thứ tự riêng (KHÔNG dùng khối cửa sổ Window: không profile-directory/new-window,
        // dùng --disable-session-crashed-bubble thay --hide-crash-restore-bubble). Giữ nguyên từng cờ gốc.
        var args = BraveArgsBuilder.Create()
            .RemoteDebuggingPort(_settings.DebugPort)
            .UserDataDir(_settings.ProfileDir)
            .NoFirstRun()
            .NoDefaultBrowserCheck()
            .Add("--no-session-restore")
            .Add("--restore-last-session=false")
            .Add("--disable-session-crashed-bubble")
            // Chặn dialog "Brave Browser quit unexpectedly / send diagnostic" (browser-chrome, Playwright không
            // click được) hiện đè sau lần crash trước.
            .Add("--noerrdialogs")
            // KHÔNG '--start-maximized': mở cỡ cửa sổ mặc định (WindowSize dưới), không chiếm cả màn hình.
            .WindowSize(1920, 1080)
            .DisableGpu()
            .Add("--disable-dev-shm-usage")
            .Add("--disable-software-rasterizer")
            // Chromium bóp timer/renderer khi cửa sổ bị che/thu nhỏ → Update/Import chậm/treo ngầm khi user
            // tự thu nhỏ hoặc cửa sổ bị đè. Tắt 3 cơ chế tiết kiệm đó (giống scrape) để chạy nền ổn định.
            .Add("--disable-backgrounding-occluded-windows")
            .Add("--disable-renderer-backgrounding")
            .Add("--disable-background-timer-throttling")
            .DiskCacheLimit()
            .StartUrl(StartUrl)
            .Build();
        // Đăng ký profile vào "fleet" TRƯỚC khi phóng → trình dọn Brave mồ côi (BraveFleet) CHỪA cửa sổ này.
        // Thiếu bước này = Brave bị quét-giết như mồ côi giữa chừng (còn vòng lặp thì cứ mở lại rồi lại bị giết).
        BraveFleet.RegisterActiveProfile(_settings.ProfileDir);

        _log("Mở Brave BigSeller profile...");
        // Phóng qua BraveJobObject (KILL_ON_JOB_CLOSE): app tắt/crash → OS tự giết Brave này. Vẫn cần
        // reaper ở DisposeAsync vì Brave fork browser thật rồi stub thoát (job chỉ dọn khi app chết hẳn).
        // startMinimized: TẮT theo yêu cầu user 2026-07-11 — mở BÌNH THƯỜNG; bản thu-nhỏ cũ kèm watchdog
        // BraveWindowMinimizer đè cửa sổ ~10s gây "nhấp nháy mở lên mở xuống" (Brave tự bung, watchdog lại đè).
        _braveProcess = BraveJobObject.Start(_settings.BravePath, args, startMinimized: false);
    }

    /// <summary>Chờ CDP port sẵn sàng (số lần thử + thông điệp lỗi do caller quyết → giữ nguyên timing/log của
    /// từng call-site: Update 90 lần, Import 30 lần, khởi động lại 30 lần).</summary>
    protected async Task EnsureCdpReadyAsync(int attempts, string failureMessage, CancellationToken ct)
    {
        if (!await new CdpClient(_settings.DebugPort).WaitForReadyAsync(attempts, 500, ct).ConfigureAwait(false))
            throw new InvalidOperationException(failureMessage);
    }

    /// <summary>Kết nối Playwright sang Brave qua CDP (tạo Playwright nếu chưa có; dọn browser cũ nếu đang rớt —
    /// dùng cả cho lần đầu lẫn khi hồi phục). Thử tối đa 8 lần, mỗi lần chờ 3s.</summary>
    protected async Task ConnectBrowserAsync(CancellationToken ct)
    {
        _playwright ??= await Playwright.CreateAsync();
        if (_browser is not null)
        {
            try { await _browser.DisposeAsync(); } catch { }
            _browser = null;
        }
        for (var attempt = 0; attempt < 8; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                _browser = await _playwright.Chromium.ConnectOverCDPAsync(
                    $"http://127.0.0.1:{_settings.DebugPort}",
                    new() { Timeout = 30000 });
                return;
            }
            catch
            {
                await DelayAsync(3000, ct);
            }
        }
        throw new InvalidOperationException(
            "Không kết nối được Brave qua CDP. Kiểm tra BigSeller profile đã đăng nhập chưa.");
    }

    /// <summary>
    /// Đảm bảo phiên BigSeller sống trong profile: còn token + probe app OK thì GIỮ phiên (chỉ lane 0 ghi cookie
    /// ra file, tránh lane phụ đá token — rotation-war); mất phiên thì import cookie từ file account rồi probe lại.
    /// Profile persistent nên phiên trong profile luôn "tươi" hơn file tĩnh → chỉ ghi đè bằng file khi mất phiên.
    /// </summary>
    protected async Task EnsureCookieAsync(CancellationToken ct)
    {
        var hasLiveSession = false;
        try
        {
            hasLiveSession = BigSellerCookieEngine.HasAuthCookie(
                await BigSellerCookieEngine.GetBigSellerCookiesAsync(_settings.DebugPort).ConfigureAwait(false));
        }
        catch { }

        if (hasLiveSession &&
            await BigSellerCookieEngine.ProbeLoggedInAsync(_settings.DebugPort, StartUrl, _log, ct).ConfigureAwait(false) == false)
        {
            hasLiveSession = false;
            _log("Token BigSeller trong profile đã bị thu hồi — nạp lại cookie từ file account.");
        }

        if (hasLiveSession)
        {
            _log("Profile đã đăng nhập BigSeller — giữ phiên hiện tại.");
            // Chỉ lane 0 ghi cookie ra file (tránh các lane phụ đá token nhau — rotation-war).
            if (_exportCookie)
                await BigSellerCookieEngine.TryExportProfileCookiesToFileAsync(
                    _settings.DebugPort, _settings.BigSellerCookieFile, _log).ConfigureAwait(false);
        }
        else
        {
            _log("Đang import cookie BigSeller từ file...");
            await BigSellerCookieEngine.ImportFromFileAsync(
                _settings.DebugPort, _settings.BigSellerCookieFile ?? "", _log,
                reloadBigSellerTabs: false, navigateUrl: StartUrl, ct).ConfigureAwait(false);
            if (await BigSellerCookieEngine.ProbeLoggedInAsync(_settings.DebugPort, StartUrl, _log, ct).ConfigureAwait(false) == false)
            {
                _log("Cookie từ file cũng hết hạn — xóa dấu TTL phiên để bước auto-login ngay sau ĐĂNG NHẬP LẠI thật.");
                // Phiên CHẾT THẬT (profile lẫn file đều hỏng — vd BigSeller đá token giữa chừng → lane restart).
                // Không Invalidate thì EnsureFreshSessionAsync bị guard TTL 4h chặn ("phiên còn tươi — dùng lại")
                // → lane cứ restart vô ích tới hết TTL mới chịu login. Xóa dấu → login lại ngay (cần Email +
                // Mật khẩu; acc thiếu credential thì hành vi như cũ: chờ login tay).
                BigSellerSessionRegistry.Invalidate(_settings.AccountId);
            }
        }
    }

    protected Task WaitIfNotPausedAsync(CancellationToken ct) =>
        _pauseToken?.WaitWhileRunningAsync(ct) ?? Task.CompletedTask;

    protected Task DelayAsync(int ms, CancellationToken ct) =>
        _pauseToken?.DelayAsync(ms, ct) ?? Task.Delay(ms, ct);

    /// <summary>
    /// Ghi NGƯỢC muc_token (server vừa xoay) từ browser ra file ĐỊNH KỲ trong lúc chạy — chỉ lane 0
    /// (<see cref="_exportCookie"/>) để tránh rotation-war, throttle 90s. Đây là điều Scrape làm sau MỖI link
    /// mà Update trước đây THIẾU (chỉ export lúc đầu + lúc đóng) → file thiu giữa chừng → lane khác / lần chạy
    /// sau import token CŨ → BigSeller đá phiên ("log in first"). Dùng engine cookie DÙNG CHUNG ở Core.
    /// </summary>
    protected Task MaybeWriteBackBigSellerTokenAsync(CancellationToken ct)
        => _tokenWriteBack.MaybeWriteBackAsync(
            _exportCookie, _settings.DebugPort, _settings.BigSellerCookieFile, _log, ct);

    public async ValueTask DisposeAsync()
    {
        OnBeforeDispose();

        // Lưu cookie CHỈ lane 0 (tránh lane phụ đá token) + TIMEOUT (Brave có thể treo → không để chặn việc kill).
        if (_exportCookie && _braveProcess is { HasExited: false })
        {
            try
            {
                await Task.WhenAny(
                    BigSellerCookieEngine.TryExportProfileCookiesToFileAsync(
                        _settings.DebugPort, _settings.BigSellerCookieFile, _log, verifySessionAlive: true),
                    Task.Delay(6000)).ConfigureAwait(false);
            }
            catch { }
        }

        // KILL Brave NGAY (đóng profile + giải phóng RAM) — KHÔNG phụ thuộc dispose browser/playwright (có thể treo).
        if (_braveProcess is not null)
        {
            try { if (!_braveProcess.HasExited) _braveProcess.Kill(entireProcessTree: true); } catch { }
            try { _braveProcess.Dispose(); } catch { }
            _braveProcess = null;
        }
        // Fallback: Brave hay fork browser thật rồi để stub thoát ngay → _braveProcess.HasExited=true,
        // Kill ở trên no-op, browser thật thành orphan (giữ profile lock + RAM). Diệt theo --user-data-dir.
        try { BraveProcessReaper.KillByUserDataDir(_settings.ProfileDir, _log); } catch { }

        // Gỡ đăng ký SAU khi đã giết → còn sót tiến trình nào thì lần sweep kế dọn nốt.
        BraveFleet.UnregisterActiveProfile(_settings.ProfileDir);

        if (_browser is not null)
        {
            try { await Task.WhenAny(_browser.DisposeAsync().AsTask(), Task.Delay(3000)).ConfigureAwait(false); } catch { }
            _browser = null;
        }
        try { _playwright?.Dispose(); } catch { }
        _playwright = null;
    }
}
