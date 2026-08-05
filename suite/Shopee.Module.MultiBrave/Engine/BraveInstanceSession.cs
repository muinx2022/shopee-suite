using Shopee.Core.Browser;
using Shopee.Core.Cdp;

namespace OpenMultiBraveLauncherV3;

/// <summary>
/// VÒNG ĐỜI MỘT CỬA SỔ BRAVE scrape — nay là lớp ĐIỀU PHỐI: giữ trạng thái phiên (đang chạy/bận/dừng,
/// profile, proxy hiện hành) và quyết định KHI NÀO làm gì, còn CÁCH làm nằm ở 5 cộng tác viên:
/// <list type="bullet">
/// <item><see cref="BraveProcessController"/> — phóng/kill/teardown tiến trình Brave.</item>
/// <item><see cref="KiotProxyRotator"/> — chọn &amp; xoay proxy KiotProxy.</item>
/// <item><see cref="ShopeeSessionBootstrapper"/> — đưa profile vào trạng thái đã đăng nhập Shopee.</item>
/// <item><see cref="BigSellerTokenGuard"/> — giữ <c>muc_token</c> BigSeller sống.</item>
/// <item><see cref="SessionMonitor"/> — hai đồng hồ giám sát (kẹt runner / proxy chết) + đồng bộ tiến độ.</item>
/// </list>
/// Phần thân của phiên chia theo trách nhiệm sang các file partial: <c>.RunnerLoop</c> (vòng chạy runner),
/// <c>.Profile</c> (dựng/mở lại profile), <c>.Progress</c> (đồng bộ tiến độ extension).
/// </summary>
internal sealed partial class BraveInstanceSession : IDisposable, ISessionMonitorHost
{
    private readonly int _cdpPort;
    private readonly CdpClient _cdpClient;
    private readonly CookieService _cookieService;
    private readonly Action<string> _log;

    private readonly BraveProcessController _brave;
    private readonly KiotProxyRotator _proxy;
    private readonly ShopeeSessionBootstrapper _shopee;
    private readonly BigSellerTokenGuard _bigSeller;
    private readonly SessionMonitor _monitor;

    private DirectoryInfo? _profileRoot;
    private string? _currentProxyFingerprint;
    private bool _running;
    private bool _busy;
    private bool _stopping;
    private InstanceConfig? _config;
    private string _braveExe = "";
    private string _sourceUserData = "";
    private string _statusText = "Dừng";
    private string _proxySummary = "";
    private bool _extensionAutomationEnabled = true;

    public event Action? StatusChanged;
    public event Action<string>? LogLine;
    public event Action? ExtensionProgressSynced;
    /// <summary>Runner loop kết thúc (xong / dừng / lỗi) — dùng cho chạy lượt.</summary>
    public event Action<string>? RunnerLoopEnded;

    // Cổng warmup (do ScrapeRunner cấp): GIỚI HẠN số instance đang "dựng SW" (cold-start) ĐỒNG THỜI.
    // Acquire trước khi chờ SW (onBeforeExtensionReady), THẢ ngay khi SW lên (onAfterExtensionReady) —
    // KHÔNG giữ suốt phiên scrape. Nhờ vậy tổng Brave chạy vẫn nhiều, nhưng chỉ vài cái cold-start SW
    // cùng lúc → máy yếu không nghẽn → SW lên ổn định (kể cả khi định chạy 30-50 Brave).
    public Func<CancellationToken, Task>? WarmupAcquire { get; set; }
    public Action? WarmupRelease { get; set; }

    public bool IsRunning => _running;
    public bool IsBusy => _busy;
    public bool IsRunnerLoopActive => _runnerLoopActive;
    public bool IsRunnerLoopPending => _runnerLoopActive || _runnerLoopRequested != 0 || _runnerResuming;
    /// <summary>Đang relaunch+resume runner (cancel→mở lại profile→chạy tiếp) — KHÔNG coi là kết thúc thật.</summary>
    public bool IsRunnerResuming => _runnerResuming;
    public string StatusText => _statusText;
    public string ProxySummary => _proxySummary;
    public DirectoryInfo? ProfileRoot => _profileRoot;

    /// <summary>Đưa cửa sổ Brave của instance này lên trước toàn bộ (gọi khi click dòng tiến trình).</summary>
    public void BringWindowToFront() => _brave.BringWindowToFront();

    public BraveInstanceSession(int cdpPort, Action<string> log)
    {
        _cdpPort = cdpPort;
        _cdpClient = new CdpClient(cdpPort);
        _cookieService = new CookieService(_cdpClient);
        _log = log;

        _brave = new BraveProcessController(
            cdpPort, () => _profileRoot, () => _cdpClient.GetBrowserWebSocketUrlAsync(), Log);
        _proxy = new KiotProxyRotator(() => _config, Log);
        _shopee = new ShopeeSessionBootstrapper(
            _cdpClient, _cookieService, () => _config, () => _running,
            ct => WaitForCdpReadyAsync(cancellationToken: ct), Log,
            () => ExtensionProgressSynced?.Invoke());
        _bigSeller = new BigSellerTokenGuard(cdpPort, () => _config, () => _running, Log);
        _monitor = new SessionMonitor(this, cdpPort, _proxy);
    }

    public void ApplyConfig(InstanceConfig config) => _config = config;

    // ── Cookie BigSeller (uỷ quyền cho BigSellerTokenGuard) ────────────────────────────────────────
    public void SetBigSellerCookieFile(string? cookieFile) => _bigSeller.SetCookieFile(cookieFile);

    /// <summary>Browser của instance này HIỆN có muc_token BigSeller sống không (qua CDP).</summary>
    public Task<bool> HasBigSellerAuthAsync() => _bigSeller.HasAuthAsync();

    // ── Đăng nhập Shopee (uỷ quyền cho ShopeeSessionBootstrapper) ──────────────────────────────────
    /// <summary>Thư mục profile (Edge) ĐÃ đăng nhập Shopee của tk này — để import nguyên session (SPC_ST/
    /// SPC_EC…) sang Brave, khỏi điền form. Trống/không hợp lệ → bỏ qua, login thường.</summary>
    public void SetShopeeSessionProfileDir(string? dir) => _shopee.SetSessionProfileDir(dir);

    /// <summary>Đảm bảo profile đã đăng nhập Shopee trước khi scrape.</summary>
    public Task<bool> EnsureShopeeLoggedInAsync(CancellationToken cancellationToken = default) =>
        _shopee.EnsureLoggedInAsync(cancellationToken);

    public async Task<bool> WaitForCdpReadyAsync(
        int attempts = 20,
        int delayMs = 500,
        CancellationToken cancellationToken = default)
    {
        if (!_running)
            return false;

        for (var i = 0; i < attempts; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_brave.HasExited)
            {
                StopSwPinner();
                _running = false;
                _statusText = "Da tat";
                StatusChanged?.Invoke();
                return false;
            }

            if (await _cdpClient.WaitForReadyAsync(1, delayMs, cancellationToken).ConfigureAwait(false))
                return true;
        }

        return false;
    }

    public Task StartAsync(string braveExe, string sourceUserData) =>
        StartProfileAsync(braveExe, sourceUserData, enableRunnerExtension: true);

    private async Task StartProfileAsync(string braveExe, string sourceUserData, bool enableRunnerExtension)
    {
        if (_busy || _config is null) return;
        _busy = true;
        _extensionAutomationEnabled = enableRunnerExtension;
        _braveExe = braveExe.Trim();
        _sourceUserData = sourceUserData.Trim();
        SetStatus("Đang khởi động…");

        try
        {
            if (!File.Exists(_braveExe))
                throw new FileNotFoundException("Không tìm thấy brave.exe.", _braveExe);

            var (proxyServer, proxyData) = await _proxy.ResolveForLaunchAsync().ConfigureAwait(false);
            await BringUpProfileAsync(
                proxyServer,
                proxyData is not null ? KiotProxyRotator.BuildFingerprint(proxyData) : proxyServer ?? "",
                ensureProfile: true).ConfigureAwait(false);

            _monitor.StartWatching();
            if (enableRunnerExtension)
            {
                _monitor.StartProgressSync();
                ScheduleDeferredSyncAfterStart();
            }
        }
        catch (Exception ex)
        {
            _running = false;
            SetStatus($"Lỗi: {ex.Message}");
            throw;
        }
        finally
        {
            _busy = false;
            RaiseStatusChanged();
        }
    }

    /// <summary>Đóng nhanh (khi thoát app) - không chờ CDP.</summary>
    public void Stop()
    {
        try { _runnerLoopCts?.Cancel(); } catch (ObjectDisposedException) { }
        StopSwPinner();
        _monitor.StopTimers();
        _running = false;
        _brave.Kill(maxWaitMs: 1500);
        TrySyncFromFileOnly(silent: true);
        _proxySummary = "";
        SetStatus("Dừng");
        RaiseStatusChanged();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_running || _stopping)
            return;

        _stopping = true;
        StopSwPinner();
        _monitor.StopTimers();
        SetStatus("Đang đóng profile…");
        RaiseStatusChanged();

        try
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));

                if (ShouldStopExtensionRunnerBeforeExit())
                {
                    await TryStopRunnerBeforeBraveExitAsync(timeout.Token).ConfigureAwait(false);
                    await Task.Delay(350, CancellationToken.None).ConfigureAwait(false);
                }
                // timeout.Token (KHÔNG phải cancellationToken=None) — bound luôn bước sync tiến độ vào 5s để
                // StopAsync không kéo dài nếu CDP chậm; sync chỉ là best-effort trước khi đóng Brave.
                await SyncExtensionProgressAsync(silent: true, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Log("Hết thời gian chờ extension — đang tắt Brave…");
            }
            catch (Exception ex)
            {
                Log($"Dừng extension: {ex.Message}");
            }

            // 4000ms: cho Brave đóng GRACEFUL kịp flush Cookies (LevelDB) xuống đĩa trước khi reaper
            // diệt tận gốc — giữ cookie đăng nhập Shopee để lần sau KHÔNG phải login lại (tránh captcha).
            await Task.Run(() => _brave.Kill(maxWaitMs: 4000), CancellationToken.None);

            _running = false;
            _proxySummary = "";
            SetStatus("Dừng");
            RaiseStatusChanged();
        }
        finally
        {
            _stopping = false;
        }
    }

    private bool ShouldStopExtensionRunnerBeforeExit()
    {
        if (_runnerLoopActive || _runnerLoopRequested != 0)
            return true;

        return _config is not null &&
               (_config.RunnerRunning == true ||
                string.Equals(_config.RunnerPhase, "starting", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_config.RunnerPhase, "opening", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_config.RunnerPhase, "scraping", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_config.RunnerPhase, "saving", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_config.RunnerPhase, "paused", StringComparison.OrdinalIgnoreCase));
    }

    private async Task TryStopRunnerBeforeBraveExitAsync(CancellationToken cancellationToken)
    {
        var profileRoot = ResolveProfileRoot();
        if (profileRoot is null)
            return;

        try
        {
            await ExtensionRunnerAutomation.StopRunAsync(_cdpPort, profileRoot, Log, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"Không gửi được lệnh dừng extension: {ex.Message}");
        }
    }

    public void Dispose()
    {
        // Trước đây chỉ dừng+huỷ _monitorTimer → mỗi session đóng lại rò _progressTimer và hai
        // CancellationTokenSource (linked CTS còn giữ đăng ký trên token cha) → tích luỹ qua nhiều
        // lần mở/đóng profile → góp phần đơ máy. Dọn hết ở đây.
        _monitor.Dispose();

        try { _runnerLoopCts?.Cancel(); } catch { }
        try { _runnerLoopCts?.Dispose(); } catch { }
        _runnerLoopCts = null;

        try { _swPinnerCts?.Cancel(); } catch { }
        try { _swPinnerCts?.Dispose(); } catch { }
        _swPinnerCts = null;

        _brave.Kill();

        // Gỡ đăng ký SAU khi đã giết Brave của profile → nếu còn sót tiến trình nào, lần sweep kế coi là
        // mồ côi và dọn nốt (không để rò qua các vòng xoay tk).
        if (_profileRoot is not null)
            BraveFleet.UnregisterActiveProfile(_profileRoot.FullName);
    }

    private void SetStatus(string text)
    {
        _statusText = text;
        RaiseStatusChanged();
    }

    private void RaiseStatusChanged() => StatusChanged?.Invoke();

    private void Log(string message)
    {
        LogLine?.Invoke(message);
        _log(message);
        // Ghi thêm ra file cố định để chẩn đoán (xem ScrapeFileLog) — không cần copy từ UI.
        ScrapeFileLog.Write(_config?.DisplayName, message);
    }

    // ── ISessionMonitorHost: cửa hẹp cho SessionMonitor, KHÔNG mở rộng API công khai của phiên ─────
    InstanceConfig? ISessionMonitorHost.Config => _config;
    bool ISessionMonitorHost.IsExtensionAutomationEnabled => _extensionAutomationEnabled;
    bool ISessionMonitorHost.HasProfileAndProcess => _profileRoot is not null && _brave.HasProcess;

    string? ISessionMonitorHost.CurrentProxyFingerprint
    {
        get => _currentProxyFingerprint;
        set => _currentProxyFingerprint = value;
    }

    void ISessionMonitorHost.SetStatus(string text) => SetStatus(text);
    void ISessionMonitorHost.RaiseStatusChanged() => RaiseStatusChanged();
    void ISessionMonitorHost.Log(string message) => Log(message);
    void ISessionMonitorHost.LogRaw(string message) => _log(message);

    Task ISessionMonitorHost.RestartProfileForProxyErrorAndResumeRunnerAsync() =>
        RestartProfileForProxyErrorAndResumeRunnerAsync();

    Task ISessionMonitorHost.RelaunchProfileAndResumeRunnerAsync(string? proxyServer, string? proxyFingerprint) =>
        RelaunchProfileAndResumeRunnerAsync(proxyServer, proxyFingerprint);

    Task ISessionMonitorHost.RestartWithFreshProxyAsync() => RestartWithFreshProxyAsync();
}
