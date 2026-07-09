using System.Diagnostics;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shopee.Core.BigSeller;
using Shopee.Core.Browser;
using Shopee.Core.Cdp;
using Shopee.Core.Infrastructure;
using Shopee.Core.Proxy;

namespace OpenMultiBraveLauncherV3;

internal sealed class BraveInstanceSession : IDisposable
{
    private readonly int _cdpPort;
    private readonly CdpClient _cdpClient;
    private readonly CookieService _cookieService;
    private readonly Action<string> _log;

    private Process? _braveProcess;
    private DirectoryInfo? _profileRoot;
    private string? _currentProxyFingerprint;
    private bool _running;
    private bool _busy;
    private bool _restarting;
    private bool _stopping;
    private InstanceConfig? _config;
    private string _braveExe = "";
    private string _sourceUserData = "";
    private string _statusText = "Dừng";
    private string _proxySummary = "";
    private string? _lastInterruptLogSignature;

    private CancellationTokenSource? _runnerLoopCts;
    private Task? _runnerLoopTask;
    private volatile bool _runnerLoopActive;

    private CancellationTokenSource? _swPinnerCts;
    private Task? _swPinnerTask;

    // Watchdog: phát hiện runner "đang chạy nhưng đứng im" (SW chết giữa chừng / tab treo).
    // Ngưỡng 8 phút > nghỉ tối đa giữa link (5 phút) nên không báo nhầm lúc nghỉ.
    private int? _watchdogLastRow;
    private DateTime _watchdogStaleSince = DateTime.UtcNow;
    private bool _watchdogRecoveredThisStall;
    private static readonly TimeSpan WatchdogStallTimeout = TimeSpan.FromMinutes(8);

    // SW runner hay không lên ở vài vòng đầu khi mở profile mới — relaunch lại tới 4 lần (mỗi vòng đã
    // nhanh hơn nhờ MaxPopupReopenBeforeRelaunch=2) để profile "lì" vẫn lên thay vì bị bỏ cuộc/đánh lỗi.
    private const int MaxExtensionRelaunchRetries = 4;

    private readonly System.Timers.Timer _monitorTimer;
    private readonly System.Timers.Timer _progressTimer;

    private bool _runnerLoopRequested;
    // Đang trong khe cancel→relaunch→resume runner (watchdog/proxy mở lại profile rồi chạy tiếp). Lúc này
    // _runnerLoopActive/_runnerLoopRequested tạm = false nên scheduler tưởng profile rảnh → mở thêm profile
    // = VƯỢT MAX. Cờ này giữ profile vẫn "đang làm việc" (IsRunnerLoopPending) suốt khe đó.
    private volatile bool _runnerResuming;
    private bool _extensionAutomationEnabled = true;

    /// <summary>
    /// File cookie BigSeller của account này — được lưu từ phiên đăng nhập không proxy (IP máy thật).
    /// Khi instance chạy, cookies này sẽ được inject vào browser qua CDP (kết nối local, không qua proxy).
    /// </summary>
    private string _bigSellerCookieFile = "";

    public void SetBigSellerCookieFile(string? cookieFile) =>
        _bigSellerCookieFile = cookieFile?.Trim() ?? "";

    /// <summary>
    /// Proxy RIÊNG cho tk BigSeller (KiotProxy key/region/type). Có key → traffic bigseller.com đi qua
    /// proxy này (split-tunnel PAC, xem BraveProfileManager) thay vì IP máy → mỗi tk BigSeller 1 IP.
    /// Phân giải LẠI mỗi lần mở Brave (ưu tiên /current sticky) để không dùng IP đã hết hạn.
    /// </summary>
    private string _bigSellerProxyKey = "";
    private string _bigSellerProxyRegion = "random";
    private string _bigSellerProxyType = "http";

    public void SetBigSellerProxy(string? key, string? region, string? proxyType)
    {
        _bigSellerProxyKey = key?.Trim() ?? "";
        _bigSellerProxyRegion = string.IsNullOrWhiteSpace(region) ? "random" : region!.Trim();
        _bigSellerProxyType = string.IsNullOrWhiteSpace(proxyType) ? "http" : proxyType!.Trim();
    }

    // BIGSELLER LUÔN ĐI IP MÁY (direct/bypass) — CHỦ ĐÍCH tắt proxy riêng cho BigSeller.
    // Lý do: proxy riêng (/current·/new) phân giải LẠI mỗi lần mở Brave, không đồng bộ giữa các lane →
    // IP đổi giữa chừng / nhiều IP trên 1 token → server đá phiên. Đây vốn là bản vá muộn không hiệu quả.
    // Trả null → BraveProfileManager dùng --proxy-bypass-list cho bigseller.* (đi IP máy ổn định).
    // (Muốn bật lại proxy riêng theo tk: khôi phục bản gọi BigSellerProxyResolver.ResolveServerAsync.)
    private Task<string?> ResolveBigSellerProxyServerAsync() => Task.FromResult<string?>(null);

    public event Action? StatusChanged;
    public event Action<string>? LogLine;
    public event Action? ExtensionProgressSynced;
    public event Action<InstanceConfig>? ExtensionInterrupted;
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
    public bool IsRunnerLoopPending => _runnerLoopActive || _runnerLoopRequested || _runnerResuming;
    /// <summary>Đang relaunch+resume runner (cancel→mở lại profile→chạy tiếp) — KHÔNG coi là kết thúc thật.</summary>
    public bool IsRunnerResuming => _runnerResuming;
    public string StatusText => _statusText;
    public string ProxySummary => _proxySummary;
    public DirectoryInfo? ProfileRoot => _profileRoot;

    /// <summary>Đưa cửa sổ Brave của instance này lên trước toàn bộ (gọi khi click dòng tiến trình).</summary>
    public void BringWindowToFront() => WindowFocus.BringProcessWindowToFront(_braveProcess);

    public BraveInstanceSession(int cdpPort, Action<string> log)
    {
        _cdpPort = cdpPort;
        _cdpClient = new CdpClient(cdpPort);
        _cookieService = new CookieService(_cdpClient);
        _log = log;
        _monitorTimer = new System.Timers.Timer { Interval = 30_000, AutoReset = true };
        _monitorTimer.Elapsed += async (_, _) =>
        {
            await CheckRunnerStallAndRecoverAsync();
            await CheckProxyAndRestartIfNeededAsync();
        };
        _progressTimer = new System.Timers.Timer { Interval = 20_000, AutoReset = true };
        _progressTimer.Elapsed += (_, _) =>
        {
            if (!_running || !_extensionAutomationEnabled)
                return;
            _ = SyncExtensionProgressAsync(silent: true);
        };
    }

    private int _syncBusy;

    public async Task<bool> SyncExtensionProgressAsync(bool silent = false, CancellationToken cancellationToken = default)
    {
        if (!_extensionAutomationEnabled)
            return false;

        if (_config is null)
            return false;

        if (Interlocked.CompareExchange(ref _syncBusy, 1, 0) != 0)
            return false;

        try
        {
            return await SyncExtensionProgressCoreAsync(silent, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _syncBusy, 0);
        }
    }

    /// <summary>Đồng bộ từ file profile — không CDP, dùng khi Stop đồng bộ.</summary>
    private bool TrySyncFromFileOnly(bool silent)
    {
        if (_config is null)
            return false;

        var profileRoot = ResolveProfileRoot();
        if (profileRoot is null)
            return false;

        if (!ExtensionProgressReader.TryRead(profileRoot, out var state) || !HasMeaningfulProgress(state))
            return false;

        _config.ApplyExtensionProgress(state);
        ExtensionProgressSynced?.Invoke();
        return true;
    }

    private async Task<bool> SyncExtensionProgressCoreAsync(bool silent, CancellationToken cancellationToken)
    {
        if (_runnerLoopActive && _config is not null)
        {
            _config.ProgressSyncedAt = DateTimeOffset.Now;
            ExtensionProgressSynced?.Invoke();
            return true;
        }

        var profileRoot = ResolveProfileRoot();
        if (profileRoot is null)
        {
            if (!silent)
                Log("Chưa có profile — bấm Start instance này ít nhất một lần (hoặc tạo profile mới).");
            return false;
        }

        try
        {
            var state = await ExtensionProgressCoordinator.ReadProgressAsync(
                _running,
                _cdpPort,
                profileRoot,
                silent,
                Log,
                cancellationToken).ConfigureAwait(false);
            if (state is null || !HasMeaningfulProgress(state))
            {
                if (!silent)
                    Log("Extension chưa có tiến độ (chưa chạy lần nào trên profile này).");
                return false;
            }

            _config!.ApplyExtensionProgress(state);

            if (state.Running == true)
                _lastInterruptLogSignature = null;

            if (state.IsInterruptedMidRun())
            {
                var signature =
                    $"{state.Phase}|{state.Running}|{state.CurrentRow}|{state.LastCompletedRow}|{state.StoppedAtRow}";
                if (!string.Equals(signature, _lastInterruptLogSignature, StringComparison.Ordinal))
                {
                    _lastInterruptLogSignature = signature;
                    var at = _config.StoppedAtRow;
                    var resume = _config.SuggestedResumeRow;
                    var sheet = string.IsNullOrWhiteSpace(_config.DataSheet) ? "?" : _config.DataSheet;
                    var sku = string.IsNullOrWhiteSpace(_config.LastSku) ? "" : $", SKU {_config.LastSku}";
                    var phase = string.IsNullOrWhiteSpace(_config.RunnerPhase) ? "" : $" ({_config.RunnerPhase})";
                    Log(
                        $"Bị dừng giữa chừng tại dòng {at} — sheet \"{sheet}\"{sku}{phase}. " +
                        $"Chạy tiếp từ dòng {resume} (bấm nút Chạy tiếp bên phải).");
                    ExtensionInterrupted?.Invoke(_config);
                }
            }
            else if (!silent)
            {
                var resume = _config.SuggestedResumeRow;
                Log(
                    $"Tiến độ: sheet=\"{_config.DataSheet}\", xong dòng {_config.LastCompletedRow}, " +
                    $"chạy tiếp từ {resume} (từ dòng form: {_config.StartRow}).");
            }

            ExtensionProgressSynced?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            if (!silent)
                Log($"Đồng bộ extension lỗi: {ex.Message}");
            return false;
        }
    }

    private static bool HasMeaningfulProgress(ExtensionRunnerState state) =>
        state.LastCompletedRow is > 0 ||
        state.CurrentRow is > 0 ||
        state.StartRow is > 0 ||
        !string.IsNullOrWhiteSpace(state.SheetName);

    public void ApplyConfig(InstanceConfig config) => _config = config;

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

            if (_braveProcess is not null && _braveProcess.HasExited)
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

    /// <summary>Profile trên đĩa — không cần Brave đang chạy từ launcher.</summary>
    private DirectoryInfo? ResolveProfileRoot()
    {
        if (_profileRoot is not null && _profileRoot.Exists)
            return _profileRoot;

        if (_config is null)
            return null;

        var root = BraveProfileManager.GetProfileRootDirectory(_config);
        if (!root.Exists)
            return null;

        var defaultDir = Path.Combine(root.FullName, "Default");
        return Directory.Exists(defaultDir) ? root : null;
    }

    public Task ResumeContinueAsync(
        string braveExe,
        string sourceUserData,
        bool preferSuggestedResume = true,
        bool retryExtensionStart = false,
        CancellationToken cancellationToken = default)
    {
        if (_config is null)
            throw new InvalidOperationException("Chưa chọn cấu hình instance.");

        if (_runnerLoopActive)
        {
            Log("Runner đang chạy trên launcher.");
            return Task.CompletedTask;
        }

        // Huỷ vòng cũ (nếu còn) rồi tạo CTS mới. Mỗi vòng dùng token CỤC BỘ (loopToken) và TỰ
        // Dispose CTS của mình ở finally — tránh: (a) rò CTS/đăng-ký-linked qua mỗi lần resume,
        // (b) task cũ đọc nhầm token mới khi field bị thay (trước đây body đọc thẳng field CTS).
        try { _runnerLoopCts?.Cancel(); } catch (ObjectDisposedException) { }
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runnerLoopCts = cts;
        var loopToken = cts.Token;

        // Đặt ĐỒNG BỘ trước khi Task.Run để không có khe IsRunnerLoopPending=false (scheduler dựa vào đó
        // để giữ trần Max). Khe này đóng luôn cả cờ resume sau khi đã bắt đầu chạy lại.
        _runnerLoopRequested = true;
        _runnerResuming = false;

        _runnerLoopTask = Task.Run(async () =>
        {
            _runnerLoopRequested = true;
            // Cổng warmup: giữ trong lúc chờ SW cold-start, thả khi SW lên (onAfter) / lỗi (catch) / kết
            // thúc (finally). Khai báo NGOÀI try để finally truy cập được → không rò permit (tránh deadlock).
            var warmupHeld = false;
            void ReleaseWarmup() { if (warmupHeld) { warmupHeld = false; WarmupRelease?.Invoke(); } }
            try
            {
                var braveDied = _running && _braveProcess is not null && _braveProcess.HasExited;
                if (braveDied)
                {
                    Log("Brave đã tắt - đang khởi động lại...");
                    StopSwPinner();
                    _running = false;
                    try { _braveProcess!.Dispose(); } catch { }
                    _braveProcess = null;
                }

                if (!_running)
                {
                    Log("Brave chưa chạy — đang khởi động…");
                    await StartAsync(braveExe, sourceUserData).ConfigureAwait(false);
                    await Task.Delay(3000, loopToken).ConfigureAwait(false);
                }

                var profileRoot = ResolveProfileRoot()
                    ?? throw new InvalidOperationException("Profile chưa sẵn sàng — Start instance này trước.");

                if (!await EnsureShopeeLoggedInAsync(loopToken).ConfigureAwait(false))
                    throw new InvalidOperationException(
                        "Không đăng nhập được Shopee (captcha/OTP hoặc sai tài khoản) — bỏ qua instance này.");

                // Phase 4c: TỰ đăng nhập BigSeller đầu phiên (mint token tươi KHỚP IP proxy Brave này) nếu chưa
                // fresh (TTL) + có mật khẩu. Chạy TRƯỚC bước nạp cookie-file: thành công → token mới nằm trong
                // browser + xuất ra file; bước nạp cookie ngay dưới thấy "browser có token sống" nên GIỮ (không
                // đè token cũ). Lane không tự login (fresh/khác) vẫn nạp token mới từ file → cùng IP acc nên hợp lệ.
                await TryAutoLoginBigSellerAsync(loopToken).ConfigureAwait(false);

                // Import BigSeller cookie (nếu account có cấu hình) — qua CDP local, KHÔNG qua proxy instance.
                await ImportBigSellerCookiesIfConfiguredAsync(loopToken).ConfigureAwait(false);

                _runnerLoopActive = true;
                Log("Bắt đầu chạy (launcher điều khiển)…");
                ExtensionProgressSynced?.Invoke();

                var extensionRetryCount = 0;
                var bigSellerReloginTries = 0;          // Phase 4c: số lần tự login lại khi mất phiên giữa chừng
                const int maxBigSellerReloginTries = 2;
                for (var proxyAttempt = 0;
                     proxyAttempt < 4 && !loopToken.IsCancellationRequested;
                     proxyAttempt++)
                {
                    try
                    {
                        await LauncherRunnerLoop.RunAsync(
                            _cdpPort,
                            profileRoot,
                            _config!,   // non-null: ResumeContinueAsync throw nếu _config null trước khi lên Task.Run; ApplyConfig chỉ gán non-null.
                            Log,
                            () => { RefreshRunStatusFromConfig(); ExtensionProgressSynced?.Invoke(); },
                            preferSuggestedResume: proxyAttempt > 0 || preferSuggestedResume,
                            loopToken,
                            onBeforeExtensionReady: async () =>
                            {
                                StopSwPinner();
                                await Task.Delay(400, loopToken).ConfigureAwait(false);
                                // Vào hàng đợi cold-start SW: chỉ vài instance dựng SW cùng lúc (máy yếu đỡ nghẽn).
                                if (WarmupAcquire is not null)
                                {
                                    await WarmupAcquire(loopToken).ConfigureAwait(false);
                                    warmupHeld = true;
                                }
                            },
                            onAfterExtensionReady: () =>
                            {
                                StartSwPinner();
                                ReleaseWarmup();   // SW đã lên → thả cổng NGAY cho instance kế (không giữ suốt scrape)
                                return Task.CompletedTask;
                            },
                            onCaptchaState: c =>
                            {
                                // Engine báo đang ở /verify chờ giải tay (true) hoặc đã qua captcha (false)
                                // → cập nhật cờ + cột Trạng thái ("🚫 Captcha" ↔ về "Đang chạy — proxy").
                                if (_config is not null) { _config.CaptchaError = c; RefreshRunStatusFromConfig(); }
                            },
                            onScrapeSucceeded: () => WriteBackBigSellerTokenAsync(loopToken)).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (
                        retryExtensionStart &&
                        extensionRetryCount < MaxExtensionRelaunchRetries &&
                        IsExtensionConnectionError(ex.Message))
                    {
                        ReleaseWarmup();   // lỗi giữa before↔after → thả cổng trước khi reopen (vòng sau tự acquire lại)
                        extensionRetryCount++;
                        Log($"Extension/CDP không phản hồi — tự đóng/mở lại profile rồi thử chạy lại ({extensionRetryCount}/{MaxExtensionRelaunchRetries})…");
                        await RestartProfileForExtensionErrorAsync().ConfigureAwait(false);
                        await Task.Delay(3500, loopToken).ConfigureAwait(false);
                        profileRoot = ResolveProfileRoot()
                            ?? throw new InvalidOperationException("Profile chưa sẵn sàng sau khi mở lại.");
                        // Mở lại profile THƯỜNG GIỮ NGUYÊN cookie (Cookies SQLite bền) → token BigSeller ĐANG
                        // SỐNG (kể cả token server VỪA XOAY lúc scrape) vẫn còn trong browser. CHỈ nạp lại từ
                        // file khi browser THỰC SỰ mất muc_token. Nạp đè token CŨ (file) lên token đang sống =
                        // GIẾT phiên → "log in BigSeller first". Đây CHÍNH là lý do thỉnh thoảng 1 instance (vd
                        // 1/8 Brave) bị "login first" khi chạy nhiều Brave/1 tk: nó tình cờ restart extension
                        // giữa chừng rồi nạp đè token cũ. App cũ KHÔNG re-import khi restart nên không bị.
                        if (!string.IsNullOrWhiteSpace(_bigSellerCookieFile))
                        {
                            if (await BigSellerCookieEngine.HasAuthCookieInBrowserAsync(_cdpPort).ConfigureAwait(false))
                                Log("BigSeller cookie: profile mở lại vẫn còn muc_token — GIỮ phiên sống, KHÔNG nạp đè.");
                            else
                                await ImportBigSellerCookiesIfConfiguredAsync(loopToken).ConfigureAwait(false);
                        }
                        proxyAttempt--;
                        continue;
                    }

                    // Phase 4c: mất phiên BigSeller GIỮA CHỪNG (phase="needlogin") → TỰ đăng nhập lại (mint
                    // token mới khớp IP proxy này) rồi CHẠY TIẾP từ dòng dừng, thay vì bỏ cả job. Chỉ khi có
                    // mật khẩu + còn lượt; xoá TTL để ép login lại. `continue` tăng proxyAttempt → vòng sau
                    // preferSuggestedResume (proxyAttempt>0) tự chạy tiếp từ dòng dừng gần nhất.
                    if (!loopToken.IsCancellationRequested
                        && string.Equals(_config?.RunnerPhase, "needlogin", StringComparison.OrdinalIgnoreCase)
                        && bigSellerReloginTries < maxBigSellerReloginTries
                        && HasBigSellerPassword())
                    {
                        bigSellerReloginTries++;
                        Log($"BigSeller mất phiên giữa chừng — TỰ đăng nhập lại rồi chạy tiếp ({bigSellerReloginTries}/{maxBigSellerReloginTries})…");
                        if (!string.IsNullOrWhiteSpace(_config?.AccountId))
                            BigSellerSessionRegistry.Invalidate(_config!.AccountId);   // ép login lại (bỏ TTL)
                        await TryAutoLoginBigSellerAsync(loopToken).ConfigureAwait(false);
                        await ImportBigSellerCookiesIfConfiguredAsync(loopToken).ConfigureAwait(false);
                        continue;
                    }

                    // Proxy lỗi khi scrape (hoặc captcha) → RunAsync return với phase="paused".
                    // KHÔNG retry cùng instance nữa: kết thúc loop để RunnerLoopEnded kích hoạt
                    // handoff sang instance khác (xem ShopeeWorkspaceControl.HandleCaptchaHandoffAsync,
                    // nay nhận cả ca proxy). Hoàn tất bình thường (phase="finished") cũng break ở đây.
                    break;
                }

                Log("Runner hoàn tất.");
                // Tự đóng profile sau khi chạy xong (nếu bật)
                if (_config is not null &&
                    _config.AutoCloseProfileOnFinish &&
                    string.Equals(_config.RunnerPhase, "finished", StringComparison.OrdinalIgnoreCase))
                {
                    Log("Tự dừng profile vì đã chạy xong.");
                    await StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                if (_config is not null)
                {
                    _config.RunnerRunning = false;
                    _config.RunnerPhase = "stopped";
                }
                Log("Đã dừng chạy.");
            }
            catch (ApiNotRunningException ex)
            {
                if (_config is not null)
                {
                    _config.RunnerRunning = false;
                    _config.RunnerPhase = "paused";
                    _config.LastRunnerMessage = "API dữ liệu chưa chạy (port 8012).";
                }
                Log($"Lỗi runner: {ex.Message}");
            }
            catch (Exception ex)
            {
                if (_config is not null)
                {
                    _config.RunnerRunning = false;
                    _config.RunnerPhase = "error";
                    _config.LastRunnerMessage = ex.Message;
                }
                Log($"Lỗi runner: {ex.Message}");
            }
            finally
            {
                ReleaseWarmup();   // an toàn: thả cổng warmup nếu còn giữ (đường cancel / lỗi khác)
                _runnerLoopActive = false;
                ExtensionProgressSynced?.Invoke();
                if (_runnerLoopRequested && _config is not null)
                {
                    RunnerLoopEnded?.Invoke(_config.Id);
                    _runnerLoopRequested = false;
                }
                // Vòng này sở hữu CTS của chính nó: gỡ field (nếu vẫn trỏ tới nó) rồi Dispose.
                // Dispose lặp lại (vd Dispose()/Stop của session) là vô hại; gỡ field trước khi Dispose
                // để các lời gọi Cancel bên ngoài không chạm vào CTS đã giải phóng.
                if (ReferenceEquals(_runnerLoopCts, cts)) _runnerLoopCts = null;
                cts.Dispose();
            }
        }, loopToken);

        return Task.CompletedTask;
    }

    public async Task StopRunnerAsync(CancellationToken cancellationToken = default)
    {
        if (!_runnerLoopActive && !_runnerLoopRequested && _runnerLoopTask is null)
        {
            Log("Runner chưa chạy — không có gì để dừng.");
            return;
        }

        Log("Đang dừng runner…");
        try { _runnerLoopCts?.Cancel(); } catch (ObjectDisposedException) { }

        var profileRoot = ResolveProfileRoot();
        if (profileRoot is not null)
        {
            try
            {
                await ExtensionRunnerAutomation.AbortScrapeStepAsync(
                    _cdpPort, profileRoot, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"Hủy bước scrape: {ex.Message}");
            }
        }

        if (_runnerLoopTask is not null)
        {
            try
            {
                await _runnerLoopTask.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Log("Runner chưa dừng hẳn trong 8s — có thể cần bấm lại.");
            }
        }

        if (_config is not null)
        {
            _config.RunnerRunning = false;
            _config.RunnerPhase = "stopped";
            var last = _config.LastCompletedRow;
            var resume = _config.SuggestedResumeRow;
            var sheet = string.IsNullOrWhiteSpace(_config.DataSheet) ? "?" : _config.DataSheet;
            Log(
                last is > 0
                    ? $"Trạng thái cuối: sheet \"{sheet}\", xong dòng {last}, chạy tiếp từ {resume}."
                    : "Đã dừng chạy.");
        }

        ExtensionProgressSynced?.Invoke();
    }

    public async Task StopRunningWorkAsync(CancellationToken cancellationToken = default)
    {
        if (_runnerLoopActive || _runnerLoopRequested || _runnerLoopTask is { IsCompleted: false })
            await StopRunnerAsync(cancellationToken).ConfigureAwait(false);

        if (_running)
            await StopAsync(cancellationToken).ConfigureAwait(false);
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

            var (proxyServer, proxyData) = await ResolveProxyForLaunchAsync().ConfigureAwait(false);
            await BringUpProfileAsync(
                proxyServer,
                proxyData is not null ? BuildFingerprint(proxyData) : proxyServer ?? "",
                ensureProfile: true).ConfigureAwait(false);

            _monitorTimer.Start();
            if (enableRunnerExtension)
            {
                _progressTimer.Start();
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

    private void StartSwPinner()
    {
        if (!_extensionAutomationEnabled)
            return;

        // Resolve extensionId TRƯỚC khi tạo CTS: nhánh "extensionId is null" thoát sớm sẽ không
        // để lại một CTS chưa Dispose.
        var extensionId = _profileRoot is null
            ? RunnerExtensionPaths.TryGetLoadedExtensionId()
            : ExtensionRunnerAutomation.TryGetRunnerExtensionIdFromProfile(_profileRoot)
              ?? RunnerExtensionPaths.TryGetLoadedExtensionId();
        if (extensionId is null) return;

        try { _swPinnerCts?.Cancel(); } catch (ObjectDisposedException) { }
        var cts = new CancellationTokenSource();
        _swPinnerCts = cts;
        var ct = cts.Token;

        _swPinnerTask = Task.Run(async () =>
        {
            try
            {
                await ExtensionRunnerAutomation.PinSwWithFlatSessionAsync(
                    _cdpPort, extensionId, Log, ct).ConfigureAwait(false);
            }
            finally
            {
                // Task sở hữu CTS của chính nó (xem ghi chú ở runner-loop): gỡ field rồi Dispose.
                if (ReferenceEquals(_swPinnerCts, cts)) _swPinnerCts = null;
                cts.Dispose();
            }
        }, ct);
    }

    private void StopSwPinner()
    {
        try { _swPinnerCts?.Cancel(); } catch (ObjectDisposedException) { }
        _swPinnerCts = null;
    }

    private static void PrepareProfileForLaunch(string profileRoot) =>
        BraveProfileManager.PrepareProfileForLaunch(profileRoot);
    /// <summary>Xóa script cache của service worker để Brave load lại extension mới nhất từ disk.</summary>
    private void ScheduleDeferredSyncAfterStart()
    {
        if (!_extensionAutomationEnabled)
            return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(2500).ConfigureAwait(false);
            await SyncExtensionProgressAsync(silent: true).ConfigureAwait(false);
        });
    }

    /// <summary>Đóng nhanh (khi thoát app) - không chờ CDP.</summary>
    public void Stop()
    {
        try { _runnerLoopCts?.Cancel(); } catch (ObjectDisposedException) { }
        StopSwPinner();
        _monitorTimer.Stop();
        _progressTimer.Stop();
        _running = false;
        KillBraveProcess(maxWaitMs: 1500);
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
        _monitorTimer.Stop();
        _progressTimer.Stop();
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
            await Task.Run(() => KillBraveProcess(maxWaitMs: 4000), CancellationToken.None);

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
        if (_runnerLoopActive || _runnerLoopRequested)
            return true;

        return _config is not null &&
               (_config.RunnerRunning == true ||
                string.Equals(_config.RunnerPhase, "starting", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_config.RunnerPhase, "opening", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_config.RunnerPhase, "scraping", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_config.RunnerPhase, "saving", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_config.RunnerPhase, "paused", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        // Trước đây chỉ dừng+huỷ _monitorTimer → mỗi session đóng lại rò _progressTimer và hai
        // CancellationTokenSource (linked CTS còn giữ đăng ký trên token cha) → tích luỹ qua nhiều
        // lần mở/đóng profile → góp phần đơ máy. Dọn hết ở đây.
        _monitorTimer.Stop();
        _progressTimer.Stop();
        _monitorTimer.Dispose();
        _progressTimer.Dispose();

        try { _runnerLoopCts?.Cancel(); } catch { }
        try { _runnerLoopCts?.Dispose(); } catch { }
        _runnerLoopCts = null;

        try { _swPinnerCts?.Cancel(); } catch { }
        try { _swPinnerCts?.Dispose(); } catch { }
        _swPinnerCts = null;

        KillBraveProcess();

        // Gỡ đăng ký SAU khi đã giết Brave của profile → nếu còn sót tiến trình nào, lần sweep kế coi là
        // mồ côi và dọn nốt (không để rò qua các vòng xoay tk).
        if (_profileRoot is not null)
            BraveFleet.UnregisterActiveProfile(_profileRoot.FullName);
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

    private DirectoryInfo EnsureProfile(DirectoryInfo sourceUserData, InstanceConfig config) =>
        BraveProfileManager.EnsureProfile(sourceUserData, config, Log);
    private void LaunchBrave(string exePath, string arguments)
    {
        KillBraveProcess();
        // Đăng ký profile vào "fleet": trình dọn Brave mồ côi (BraveFleet) sẽ CHỪA cửa sổ đang sống này,
        // chỉ giết Brave thuộc app mà KHÔNG còn session nào nhận (sót sau treo/crash). Đăng ký TRƯỚC khi
        // phóng để con-trình Brave xuất hiện là đã được bảo vệ.
        if (_profileRoot is not null)
            BraveFleet.RegisterActiveProfile(_profileRoot.FullName);
        // Phóng Brave GẮN vào Job Object KILL_ON_JOB_CLOSE của app → app chết kiểu gì (kể cả crash /
        // force-kill) thì OS tự dọn sạch Brave con, không còn tiến trình mồ côi ăn RAM.
        _braveProcess = BraveJobObject.Start(exePath, arguments);
        Log($"Brave PID={_braveProcess?.Id}");
    }

    private void KillBraveProcess(int maxWaitMs = 1500)
    {
        // Brave sắp chết → đóng kết nối CDP DÙNG CHUNG của port (WS cũ chết theo); lần dùng sau hub tự
        // nối lại tới Brave mới. Best-effort, không chặn.
        if (_cdpPort > 0) PortCdpHub.For(_cdpPort).ResetSoon();
        // Giết tiến trình stub mà launcher giữ tham chiếu (nếu còn).
        if (_braveProcess is not null)
        {
            try
            {
                if (!_braveProcess.HasExited)
                {
                    TryCloseBraveGracefully(maxWaitMs);
                    if (!_braveProcess.HasExited)
                    {
                        _braveProcess.Kill(entireProcessTree: true);
                        if (maxWaitMs > 0)
                            _braveProcess.WaitForExit(maxWaitMs);
                    }
                }
            }
            catch { }
            finally
            {
                _braveProcess.Dispose();
                _braveProcess = null;
            }
        }

        // Brave hay fork rồi thoát stub → browser thật + GPU/renderer/utility chạy ở PID khác,
        // Kill(tree) ở trên bỏ sót. Quét & giết tận gốc theo --user-data-dir duy nhất của profile
        // để không tích tụ zombie qua mỗi vòng xoay (nguyên nhân đơ máy sau vài vòng).
        try
        {
            BraveProcessReaper.KillByUserDataDir(_profileRoot?.FullName, Log);
        }
        catch { }
    }

    private void TryCloseBraveGracefully(int maxWaitMs)
    {
        if (_braveProcess is null || _braveProcess.HasExited)
            return;

        var waitMs = Math.Max(2500, maxWaitMs);
        try
        {
            _braveProcess.CloseMainWindow();
            if (_braveProcess.WaitForExit(waitMs))
                return;
        }
        catch
        {
            // fall through to CDP Browser.close
        }

        try
        {
            using var browser = new ClientWebSocket();
            browser.ConnectAsync(new Uri(GetBrowserWebSocketUrlAsync().GetAwaiter().GetResult()), CancellationToken.None)
                .GetAwaiter().GetResult();
            SendCdpAsync(browser, 501, "Browser.close", null).GetAwaiter().GetResult();
            _braveProcess.WaitForExit(waitMs);
        }
        catch
        {
            // fallback kill happens in caller
        }
    }

    private string BuildBraveArguments(string userDataDir, string? proxyServer, string? bigSellerProxyServer) =>
        BraveProfileManager.BuildBraveArguments(
            _cdpPort, userDataDir, proxyServer, Log, _sourceUserData,
            loadRunnerExtension: _extensionAutomationEnabled,
            bigSellerProxyServer: bigSellerProxyServer);

    /// <summary>
    /// Kill tiến trình Brave đang theo dõi, RỒI đảm bảo CDP port đã nhả hẳn trước khi cho launch lại.
    /// Nếu sau khi kill mà port vẫn còn (một Brave cũ — vd. instance lỗi proxy — vẫn giữ port/profile),
    /// gửi Browser.close qua CDP để đuổi nốt. Nếu bỏ qua bước này, brave.exe mới chỉ forward URL sang
    /// instance cũ rồi tự thoát → không có browser mới → runner treo ở "Đang chờ extension trên CDP".
    /// </summary>
    private async Task KillBraveAndWaitPortFreeAsync(int maxWaitMs = 8000)
    {
        KillBraveProcess();

        var deadline = DateTime.UtcNow.AddMilliseconds(maxWaitMs);
        var evicted = false;
        while (DateTime.UtcNow < deadline)
        {
            if (!await IsCdpPortReachableAsync(_cdpPort).ConfigureAwait(false))
                return; // port đã nhả — sạch, có thể launch lại

            // Còn một Brave nào đó giữ port → đuổi bằng Browser.close (kill theo PID không bắt được
            // vì brave.exe gốc có thể đã fork+exit, browser thật chạy ở PID khác).
            if (!evicted)
                Log($"CDP port {_cdpPort} vẫn còn Brave cũ giữ — đóng nốt trước khi mở lại…");
            evicted = true;
            try
            {
                using var browser = new ClientWebSocket();
                var wsUrl = await GetBrowserWebSocketUrlAsync().ConfigureAwait(false);
                await browser.ConnectAsync(new Uri(wsUrl), CancellationToken.None).ConfigureAwait(false);
                await SendCdpAsync(browser, 502, "Browser.close", null).ConfigureAwait(false);
            }
            catch { /* port đang đóng dở; vòng lặp sẽ kiểm tra lại */ }
            await Task.Delay(400).ConfigureAwait(false);
        }

        if (await IsCdpPortReachableAsync(_cdpPort).ConfigureAwait(false))
            Log($"Cảnh báo: CDP port {_cdpPort} vẫn bận sau khi chờ — Brave mới có thể không khởi động sạch.");
    }

    private static async Task<bool> IsCdpPortReachableAsync(int port)
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            var connectTask = tcp.ConnectAsync("127.0.0.1", port);
            return await Task.WhenAny(connectTask, Task.Delay(1200)).ConfigureAwait(false) == connectTask
                   && connectTask.IsCompletedSuccessfully;
        }
        catch
        {
            return false;
        }
    }

    private async Task<(string? proxyServer, Dictionary<string, object>? proxyData)> ResolveProxyForLaunchAsync()
    {
        if (_config is null)
            throw new InvalidOperationException("Chưa cấu hình instance.");

        var kiotKey = _config.KiotProxyKey.Trim();
        if (!string.IsNullOrWhiteSpace(kiotKey))
        {
            var proxy = await GetWorkingProxyAsync().ConfigureAwait(false);
            var server = BuildProxyServer(proxy, _config.ProxyType);
            return (server, proxy);
        }

        var manual = _config.ManualProxy.Trim();
        if (!string.IsNullOrWhiteSpace(manual))
        {
            var server = NormalizeManualProxy(manual, _config.ProxyType);
            Log($"Dùng proxy thủ công: {server}");
            return (server, null);
        }

        if (_config.RequireProxy)
            throw new InvalidOperationException(
                "Instance chưa có proxy (KiotProxy key / proxy thủ công) — không mở profile để tránh login Shopee bằng IP máy.");

        Log(_config.RequireProxy
            ? "Không có proxy — mở profile bằng IP máy (đã xác nhận bỏ qua cảnh báo)."
            : "Không có Kiot key / proxy thủ công — mở profile không proxy.");
        return (null, null);
    }

    private static string NormalizeManualProxy(string input, string proxyType)
    {
        var s = input.Trim();
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("socks4://", StringComparison.OrdinalIgnoreCase))
            return s;

        var scheme = proxyType.Equals("socks5", StringComparison.OrdinalIgnoreCase) ? "socks5://" : "http://";
        return scheme + s;
    }

    private static bool IsExtensionConnectionError(string message) =>
        message.Contains("extension", StringComparison.OrdinalIgnoreCase) &&
        (message.Contains("CDP", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("không kết nối", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("không phản hồi", StringComparison.OrdinalIgnoreCase));

    private async Task RestartProfileForProxyErrorAndResumeRunnerAsync()
    {
        var wasRunnerActive = _runnerLoopActive;
        // Giữ profile được tính là "đang làm việc" suốt khe cancel→relaunch→resume để scheduler không mở
        // thêm profile (vượt Max). ResumeContinueAsync gỡ cờ khi runner chạy lại; finally lo ca không resume.
        if (wasRunnerActive) _runnerResuming = true;
        try
        {
            try { _runnerLoopCts?.Cancel(); } catch { }
            await RestartProfileForProxyErrorAsync().ConfigureAwait(false);
            if (wasRunnerActive)
            {
                await Task.Delay(2500).ConfigureAwait(false);
                await ResumeContinueAsync(_braveExe, _sourceUserData, preferSuggestedResume: true)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _runnerResuming = false;
        }
    }

    private async Task RelaunchProfileAndResumeRunnerAsync(string? server, string? proxyFingerprint)
    {
        var wasRunnerActive = _runnerLoopActive;
        if (wasRunnerActive) _runnerResuming = true;
        try
        {
            try { _runnerLoopCts?.Cancel(); } catch { }
            await RelaunchProfileAsync(server, proxyFingerprint).ConfigureAwait(false);
            if (wasRunnerActive)
            {
                await Task.Delay(2500).ConfigureAwait(false);
                await ResumeContinueAsync(_braveExe, _sourceUserData, preferSuggestedResume: true)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _runnerResuming = false;
        }
    }

    /// <summary>
    /// Watchdog: runner báo "đang chạy" nhưng dòng (CurrentRow) không tiến quá lâu = kẹt thật
    /// (SW chết giữa chừng / tab treo / CDP đơ). Tự mở lại profile + chạy tiếp. Ngưỡng 8 phút >
    /// nghỉ tối đa giữa link (5 phút) nên không đụng vào lúc runner nghỉ hợp lệ.
    /// </summary>
    private async Task CheckRunnerStallAndRecoverAsync()
    {
        // Chỉ canh khi runner thực sự đang chạy ở pha "đang làm việc". Mọi pha khác (paused/error/
        // stopped/finished) hoặc đang restart/busy → reset bộ đếm.
        var phase = _config?.RunnerPhase ?? "";
        var working = _runnerLoopActive && !_restarting && !_busy && _config is not null &&
            (phase.Equals("starting", StringComparison.OrdinalIgnoreCase) ||
             phase.Equals("opening", StringComparison.OrdinalIgnoreCase) ||
             phase.Equals("video", StringComparison.OrdinalIgnoreCase) ||
             phase.Equals("running", StringComparison.OrdinalIgnoreCase));

        if (!working)
        {
            _watchdogLastRow = _config?.CurrentRow;
            _watchdogStaleSince = DateTime.UtcNow;
            _watchdogRecoveredThisStall = false;
            return;
        }

        if (_config!.CurrentRow != _watchdogLastRow)
        {
            _watchdogLastRow = _config.CurrentRow;
            _watchdogStaleSince = DateTime.UtcNow;
            _watchdogRecoveredThisStall = false;
            return;
        }

        if (_watchdogRecoveredThisStall ||
            DateTime.UtcNow - _watchdogStaleSince < WatchdogStallTimeout)
            return;

        _watchdogRecoveredThisStall = true;
        _restarting = true;
        Log($"Watchdog: runner kẹt ở dòng {_config.CurrentRow?.ToString() ?? "?"} > {WatchdogStallTimeout.TotalMinutes:0} phút — tự mở lại profile và chạy tiếp…");
        try
        {
            await RestartProfileForProxyErrorAndResumeRunnerAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"Watchdog mở lại lỗi: {ex.Message}");
        }
        finally
        {
            _restarting = false;
        }
    }

    /// <summary>
    /// ĐƯỜNG THỰC THI DUY NHẤT để dựng Brave lên cho profile này — dùng cho CẢ khởi động lần đầu
    /// (cold start, <paramref name="ensureProfile"/>=true) lẫn mọi lần mở lại (warm restart sau lỗi
    /// proxy/extension, đổi proxy, ERR_PROXY tab, user tự đóng cửa sổ). Mọi nhánh auto/manual đều gọi
    /// vào đây: chúng chỉ khác nhau ở "cách thức" (chọn proxy nào, có dựng lại profile từ source không,
    /// có resume runner sau đó không), còn "cách thực thi" — đảm bảo profile, dọn SW pinner, clear
    /// extension cache, prepare profile, kill Brave + chờ CDP port nhả hẳn, launch, pin SW — là MỘT.
    /// <paramref name="proxyFingerprint"/> null = giữ nguyên fingerprint hiện tại (chỉ truyền khi đổi proxy).
    /// LƯU Ý: không động tới timers / AutoImport / resume — đó là việc của caller (chạy đúng thread/ngữ cảnh).
    /// </summary>
    private async Task BringUpProfileAsync(string? proxyServer, string? proxyFingerprint, bool ensureProfile)
    {
        if (_config is null)
            throw new InvalidOperationException("Chưa chọn cấu hình instance.");

        if (ensureProfile)
        {
            var sourceData = new DirectoryInfo(_sourceUserData);
            if (!sourceData.Exists)
                throw new DirectoryNotFoundException("Không tìm thấy thư mục User Data mẫu.");
            _profileRoot = EnsureProfile(sourceData, _config);
            Log($"Profile: {_profileRoot.FullName}");
        }

        if (_profileRoot is null)
            throw new InvalidOperationException("Profile chưa sẵn sàng.");

        StopSwPinner();
        ExtensionRunnerAutomation.ClearResolvedExtension(_cdpPort);
        PrepareProfileForLaunch(_profileRoot.FullName);
        await KillBraveAndWaitPortFreeAsync().ConfigureAwait(false);

        // Phân giải proxy RIÊNG của tk BigSeller (nếu có key) NGAY trước khi build args → bigseller.com
        // split-tunnel qua IP này (PAC), Shopee giữ proxyServer của instance. Không key → null = IP máy.
        var bigSellerProxyServer = await ResolveBigSellerProxyServerAsync().ConfigureAwait(false);
        var args = BuildBraveArguments(_profileRoot.FullName, proxyServer, bigSellerProxyServer);
        LaunchBrave(_braveExe, args);
        _running = true;
        _proxySummary = proxyServer ?? "(không proxy)";
        if (proxyFingerprint is not null)
            _currentProxyFingerprint = proxyFingerprint;
        SetStatus(proxyServer is not null ? $"Đang chạy — {proxyServer}" : "Đang chạy — không proxy");
        // Không pin SW ở đây — runner loop StartSwPinner sau khi extension sẵn sàng.
        if (_extensionAutomationEnabled)
            await WaitForCdpReadyAsync(attempts: 40, delayMs: 500).ConfigureAwait(false);
    }

    /// <summary>Mở lại profile đang sống (warm restart) — wrapper gọn cho <see cref="BringUpProfileAsync"/>.</summary>
    // PHANH RELAUNCH TOÀN APP. Khi NHIỀU cửa sổ mất SW cùng lúc (đoạn sau), nếu để tất cả relaunch
    // (Kill + mở lại Brave + cold-start SW + churn WS/popup) ĐỒNG THỜI → bùng tài nguyên = "brave chạy mất
    // kiểm soát, máy đơ" (đúng triệu chứng: CHỈ đơ lúc không scrape được, không phải lúc chạy thường).
    // Gate này cho tối đa 2 relaunch một lúc trên TOÀN app → hệ thống hồi TỪ TỐN, không thundering herd.
    // (WarmupGate chỉ chặn cold-start SW, KHÔNG chặn phần Kill+relaunch nặng.) CHỈ áp cho relaunch — lần
    // mở đầu đi thẳng BringUpProfileAsync (đã có LaunchStagger) nên không bị gate này serialize.
    private static readonly SemaphoreSlim RelaunchGate = new(2, 2);

    private async Task RelaunchProfileAsync(string? proxyServer, string? proxyFingerprint)
    {
        // Chờ có giới hạn 2': cổng kẹt lâu bất thường thì cứ relaunch để KHÔNG treo teardown/Stop.
        var got = await RelaunchGate.WaitAsync(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        try { await BringUpProfileAsync(proxyServer, proxyFingerprint, ensureProfile: false).ConfigureAwait(false); }
        finally { if (got) RelaunchGate.Release(); }
    }

    private async Task RestartProfileForExtensionErrorAsync()
    {
        if (_profileRoot is null)
            throw new InvalidOperationException("Profile chưa sẵn sàng.");

        var server = string.IsNullOrWhiteSpace(_proxySummary) || _proxySummary.StartsWith('(')
            ? null
            : _proxySummary;

        // Lỗi extension → giữ nguyên proxy, chỉ mở lại profile sạch.
        await RelaunchProfileAsync(server, proxyFingerprint: null).ConfigureAwait(false);
    }

    private async Task RestartProfileForProxyErrorAsync()
    {
        if (_profileRoot is null || _config is null)
            return;

        Dictionary<string, object>? proxyData = null;
        string? server;
        if (!string.IsNullOrWhiteSpace(_config.KiotProxyKey.Trim()))
        {
            proxyData = await GetWorkingProxyAsync(preferFresh: true, avoidFingerprint: _currentProxyFingerprint)
                .ConfigureAwait(false);
            server = BuildProxyServer(proxyData, _config.ProxyType);
        }
        else
        {
            (server, proxyData) = await ResolveProxyForLaunchAsync().ConfigureAwait(false);
        }

        await RelaunchProfileAsync(
            server,
            proxyData is not null ? BuildFingerprint(proxyData) : server ?? "").ConfigureAwait(false);
    }

    private Task<Dictionary<string, object>> GetProxyAsync()
    {
        if (_config is null) throw new InvalidOperationException("Chua cau hinh instance.");
        return KiotProxyClient.GetNewProxyAsync(_config.KiotProxyKey, _config.Region, Log);
    }
    private async Task<Dictionary<string, object>> GetWorkingProxyAsync(
        int maxAttempts = 5,
        bool preferFresh = false,
        string? avoidFingerprint = null)
    {
        // Normal launch can reuse /current after the first /new attempt to avoid Kiot rate limits.
        // Proxy-error recovery must prefer /new and reject the same proxy that just failed in Brave.
        Exception? lastError = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            Dictionary<string, object> proxy;
            try
            {
                // ƯU TIÊN /current: giữ IP hiện hành của key (KiotProxy gán qua /new, sống ~30') → login
                // và scrape DÙNG CHUNG 1 IP trong cửa sổ 30' → tránh captcha do nhảy IP. KHÔNG gọi /new
                // mỗi launch (sẽ ép xoay IP → login-IP ≠ scrape-IP).
                proxy = await GetCurrentProxyAsync();
            }
            catch (Exception curEx) when (IsProxyExpiredError(curEx.Message))
            {
                // /current chưa có proxy (key CHƯA kích hoạt lần nào / IP đã hết hạn 30') → /new gán IP
                // mới MỘT LẦN; các launch sau trong 30' lại dùng /current cùng IP đó.
                try
                {
                    proxy = await GetProxyAsync();
                }
                catch (Exception newEx)
                {
                    lastError = newEx;
                    if (attempt < maxAttempts)
                        await Task.Delay(preferFresh ? 10_000 : 2_000).ConfigureAwait(false);
                    continue;
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt < maxAttempts)
                    await Task.Delay(preferFresh ? 10_000 : 2_000).ConfigureAwait(false);
                continue;
            }

            var proxyServer = BuildProxyServer(proxy, _config!.ProxyType);
            var fingerprint = BuildFingerprint(proxy);
            if (!string.IsNullOrWhiteSpace(avoidFingerprint) &&
                string.Equals(fingerprint, avoidFingerprint, StringComparison.Ordinal))
            {
                lastError = new InvalidOperationException($"KiotProxy vẫn trả về proxy cũ đang lỗi: {proxyServer}");
                if (attempt < maxAttempts)
                    await Task.Delay(2000).ConfigureAwait(false);
                continue;
            }

            // KiotProxy API trả proxy thành công = proxy sống — không test thêm qua bên thứ ba.
            Log($"Proxy từ KiotProxy ({attempt}/{maxAttempts}): {proxyServer}");
            return proxy;
        }
        throw lastError ?? new InvalidOperationException("Không lấy được proxy.");
    }

    private Task<Dictionary<string, object>> GetCurrentProxyAsync()
    {
        if (_config is null) throw new InvalidOperationException("Chua cau hinh instance.");
        return KiotProxyClient.GetCurrentProxyAsync(_config.KiotProxyKey);
    }
    private static string BuildProxyServer(Dictionary<string, object> proxy, string selectedType) =>
        KiotProxyClient.BuildProxyServer(proxy, selectedType);
    private static string BuildFingerprint(Dictionary<string, object> proxy) =>
        KiotProxyClient.BuildFingerprint(proxy);
    private static bool IsProxyExpiredError(string msg) =>
        msg.Contains("KiotProxy current", StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("KiotProxy new", StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("PROXY_NOT_FOUND_BY_KEY", StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("Could not find the proxy being used by key", StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("hết hạn", StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("het han", StringComparison.OrdinalIgnoreCase);

    private async Task CheckProxyAndRestartIfNeededAsync()
    {
        if (!_running || _restarting || _busy || _profileRoot is null || _braveProcess is null || _config is null)
            return;

        var taskRunDispatched = false;
        try
        {
            var hasKiot = !string.IsNullOrWhiteSpace(_config.KiotProxyKey.Trim());
            string? server;

            if (!hasKiot)
            {
                server = string.IsNullOrWhiteSpace(_config.ManualProxy)
                    ? null
                    : NormalizeManualProxy(_config.ManualProxy, _config.ProxyType);

                // Khi runner đang chạy: KHÔNG để monitor mở lại profile vì proxy thủ công cố định —
                // mở lại cũng dính cùng proxy chết → kẹt "đang mở link X/Y → reload" lặp vô hạn.
                // Để runner tự phát hiện proxyError ở bước scrape rồi tạm dừng (handoff) — sạch hơn.
                if (!_runnerLoopActive && await HasChromeProxyErrorPageAsync().ConfigureAwait(false))
                {
                    _restarting = true;
                    Log("Phát hiện ERR_PROXY/No internet - tự khởi động lại profile...");
                    await RestartProfileForProxyErrorAndResumeRunnerAsync().ConfigureAwait(false);
                }
                return;
            }

            Dictionary<string, object> current;
            try
            {
                current = await GetCurrentProxyAsync();
            }
            catch (HttpRequestException ex)
            {
                SetStatus("Không kết nối KiotProxy API");
                Log($"Monitor: {ex.Message}");
                return;
            }
            catch (Exception ex) when (IsProxyExpiredError(ex.Message))
            {
                _restarting = true;
                try { await RestartWithFreshProxyAsync(); }
                catch (Exception rfEx) { Log($"Refresh: {rfEx.Message}"); }
                return;
            }

            var fp = BuildFingerprint(current);
            server = BuildProxyServer(current, _config.ProxyType);

            if (!string.Equals(fp, _currentProxyFingerprint, StringComparison.Ordinal))
            {
                // KiotProxy (random) xoay proxy là BÌNH THƯỜNG — KHÔNG relaunch profile đang scrape ngon
                // chỉ vì fingerprint đổi (gây "được tí lại reload" liên tục mỗi 30s). Chỉ cập nhật fp.
                // Nếu proxy cũ thật sự chết: Brave hiện ERR_PROXY (xử lý ngay dưới) hoặc scrape step báo
                // proxy error → handoff. Như vậy vẫn không "chết" khi đổi proxy mà không reload vô cớ.
                _currentProxyFingerprint = fp;
            }

            // Proxy API báo OK nhung Brave v?n hi?n "No internet"/ERR_PROXY... (tab chrome-error).
            // Trường hợp này user đang phải Đóng profile → Mở lại thủ công. Tự động làm tương tự.
            // Runner đang chạy → để bước scrape tự bắt proxyError và handoff sang profile khác (proxy mới).
            // Monitor mở lại ở đây sẽ huỷ scrape đang chạy + dùng lại proxy cũ → vòng reload cùng một dòng.
            if (!_runnerLoopActive && await HasChromeProxyErrorPageAsync().ConfigureAwait(false))
            {
                _restarting = true;
                taskRunDispatched = true;
                var restartServer = server;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        Log("Phát hiện ERR_PROXY/No internet trên tab - tự khởi động lại profile...");
                        await RelaunchProfileAndResumeRunnerAsync(restartServer, proxyFingerprint: null)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log($"Restart profile (proxy error): {ex.Message}");
                    }
                    finally
                    {
                        _restarting = false;
                        RaiseStatusChanged();
                    }
                });
                return;
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Monitor: {ex.Message}");
            Log(ex.Message);
        }
        finally
        {
            if (!taskRunDispatched)
            {
                _restarting = false;
                RaiseStatusChanged();
            }
        }
    }

    private async Task RestartWithFreshProxyAsync()
    {
        if (_profileRoot is null || _config is null)
            return;

        if (string.IsNullOrWhiteSpace(_config.KiotProxyKey.Trim()))
        {
            await RestartProfileForProxyErrorAsync().ConfigureAwait(false);
            return;
        }

        var proxy = await GetWorkingProxyAsync(preferFresh: true, avoidFingerprint: _currentProxyFingerprint);
        var server = BuildProxyServer(proxy, _config.ProxyType);
        await RelaunchProfileAsync(server, BuildFingerprint(proxy)).ConfigureAwait(false);
    }

    private async Task<bool> HasChromeProxyErrorPageAsync()
    {
        try
        {
            using var response = await AppServices.DirectHttp.GetAsync(
                $"http://127.0.0.1:{_cdpPort}/json/list",
                CancellationToken.None).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var type = item.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                if (!string.Equals(type, "page", StringComparison.OrdinalIgnoreCase))
                    continue;

                var url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                var title = item.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "";
                if (url.StartsWith("chrome-error://", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("No internet", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("ERR_PROXY_CONNECTION_FAILED", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Kiểm tra cookie phiên Shopee (SPC_ST / SPC_EC) — có giá trị thật nghĩa là đã đăng nhập.
    /// </summary>
    private async Task<bool> IsShopeeLoggedInAsync()
    {
        try
        {
            var cookies = await GetAllCookiesFromBraveAsync().ConfigureAwait(false);
            return cookies.Any(c =>
            {
                var domain = c.TryGetValue("domain", out var d) ? d?.ToString() ?? "" : "";
                if (!domain.Contains("shopee", StringComparison.OrdinalIgnoreCase))
                    return false;

                var name = c.TryGetValue("name", out var n) ? n?.ToString() ?? "" : "";
                if (!string.Equals(name, "SPC_ST", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(name, "SPC_EC", StringComparison.OrdinalIgnoreCase))
                    return false;

                var value = c.TryGetValue("value", out var v) ? v?.ToString() ?? "" : "";
                return !string.IsNullOrWhiteSpace(value) && value != "-" && value.Length > 5;
            });
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
    public async Task<bool> EnsureShopeeLoggedInAsync(CancellationToken cancellationToken = default)
    {
        if (_config is null || !_running)
            return false;

        // Không có chuỗi tài khoản → giữ hành vi cũ (profile đã login thủ công từ trước).
        if (string.IsNullOrWhiteSpace(_config.ShopeeAccountLogin))
            return true;

        if (!await WaitForCdpReadyAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            Log("Shopee: CDP chưa sẵn sàng — không kiểm tra được đăng nhập.");
            return false;
        }

        // Import session đã đăng nhập của tk (từ profile Edge của tab "Kiểm tra tài khoản") sang Brave →
        // IsShopeeLoggedInAsync thấy SPC_ST/SPC_EC → KHỎI điền form. Login lại nhiều lần từ IP khác nhau
        // chính là nguyên nhân dính captcha; import cookie tránh được điều đó.
        try
        {
            var injected = await InjectShopeeSessionCookiesAsync().ConfigureAwait(false);
            if (injected > 0)
                Log($"Shopee: import {injected} cookie từ profile đã đăng nhập (khỏi điền form).");
        }
        catch (Exception ex) { Log("Shopee: import cookie session lỗi (sẽ thử login thường): " + ex.Message); }

        // Thử vài nhịp trước khi kết luận "chưa đăng nhập": cookie store có thể chưa nạp xong từ đĩa
        // ngay sau khi CDP ready → tránh login lại THỪA (login liên tục là nguyên nhân dính captcha).
        for (var i = 0; i < 5; i++)
        {
            if (await IsShopeeLoggedInAsync().ConfigureAwait(false))
            {
                Log("Shopee: đã đăng nhập sẵn (giữ cookie từ phiên trước).");
                ClearShopeeLoginPendingFlag();
                return true;
            }
            if (i < 4) await Task.Delay(800, cancellationToken).ConfigureAwait(false);
        }

        Log("Shopee: chưa đăng nhập — tự đăng nhập bằng tài khoản đã lưu…");
        await OpenShopeeAccountLoginAsync().ConfigureAwait(false);

        for (var i = 0; i < 30; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(3000, cancellationToken).ConfigureAwait(false);
            if (await IsShopeeLoggedInAsync().ConfigureAwait(false))
            {
                Log("Shopee: đăng nhập thành công.");
                ClearShopeeLoginPendingFlag();
                return true;
            }
        }

        Log("Shopee: không xác nhận được đăng nhập sau 90s (có thể vướng captcha/OTP) — cần xử lý thủ công.");
        return false;
    }

    private void ClearShopeeLoginPendingFlag()
    {
        if (_config is null)
            return;

        var changed = _config.OpenWithShopeeAccount || _config.CreateNewProfileOnNextStart;
        _config.OpenWithShopeeAccount = false;
        _config.CreateNewProfileOnNextStart = false;
        if (changed)
            ExtensionProgressSynced?.Invoke();
    }

    public async Task<bool> OpenShopeeAccountLoginAsync()
    {
        if (_config is null || !_running)
            return false;

        try
        {
            if (!TryParseShopeeAccountLogin(_config.ShopeeAccountLogin, out var login, out var parseError))
            {
                Log($"Shopee login: {parseError}");
                return false;
            }

            var cdpReady = false;
            for (var i = 0; i < 20 && _running; i++)
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

            if (!cdpReady || !_running)
            {
                Log("Shopee login: CDP không sẵn sàng — profile có thể đã đóng.");
                return false;
            }

            await SetShopeeSpcFCookieAsync(login).ConfigureAwait(false);
            if (!_running)
                return false;

            await OpenShopeeLoginPageAsync().ConfigureAwait(false);
            if (!_running)
                return false;

            await FillShopeeLoginFormAsync(login).ConfigureAwait(false);
            Log($"Shopee login: đã mở trang đăng nhập và điền tài khoản {login.Username}.");
            return true;
        }
        catch (Exception ex)
        {
            Log($"Shopee login lỗi: {ex.Message}");
            return false;
        }
    }

    private static bool TryParseShopeeAccountLogin(
        string raw,
        out ShopeeAccountLogin login,
        out string error) =>
        ShopeeLoginAutomation.TryParseLoginLine(raw, out login, out error);
    private string? _shopeeSessionProfileDir;

    /// <summary>Thư mục profile (Edge) ĐÃ đăng nhập Shopee của tk này — để import nguyên session (SPC_ST/
    /// SPC_EC…) sang Brave, khỏi điền form. Trống/không hợp lệ → bỏ qua, login thường.</summary>
    public void SetShopeeSessionProfileDir(string? dir) => _shopeeSessionProfileDir = dir;

    /// <summary>Đọc + giải mã cookie shopee từ profile đã đăng nhập rồi inject vào Brave qua CDP. Trả số
    /// cookie đã nạp (0 = không có profile / không giải mã được → caller login thường).</summary>
    private async Task<int> InjectShopeeSessionCookiesAsync()
    {
        var dir = _shopeeSessionProfileDir;
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
        await SendCdpAsync(browser, 716, "Storage.setCookies", new { cookies = payloads });
        return payloads.Length;
    }

    private async Task SetShopeeSpcFCookieAsync(ShopeeAccountLogin login)
    {
        var payload = new Dictionary<string, object?>
        {
            ["name"] = "SPC_F",
            ["value"] = login.SpcF,
            ["domain"] = string.IsNullOrWhiteSpace(login.CookieDomain) ? ".shopee.vn" : login.CookieDomain,
            ["path"] = "/",
            ["secure"] = true,
            ["httpOnly"] = false,
            ["sameSite"] = "Lax",
            ["expires"] = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds(),
        };

        using var browser = new ClientWebSocket();
        await browser.ConnectAsync(new Uri(await GetBrowserWebSocketUrlAsync().ConfigureAwait(false)), CancellationToken.None);
        await SendCdpAsync(browser, 710, "Storage.setCookies", new { cookies = new[] { payload } });
    }

    private async Task OpenShopeeLoginPageAsync()
    {
        const string loginUrl = "https://shopee.vn/buyer/login?next=https%3A%2F%2Fshopee.vn";

        var wsUrl = await _cdpClient.EnsurePageTargetAsync(
            url => url.StartsWith("https://shopee.vn/buyer/login", StringComparison.OrdinalIgnoreCase),
            loginUrl).ConfigureAwait(false);

        using var page = new ClientWebSocket();
        await page.ConnectAsync(new Uri(wsUrl), CancellationToken.None).ConfigureAwait(false);
        await SendCdpAsync(page, 721, "Page.navigate", new { url = loginUrl });
    }

    private async Task FillShopeeLoginFormAsync(ShopeeAccountLogin login)
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
                var wsUrl = await FindPageWebSocketUrlAsync(url =>
                    url.StartsWith("https://shopee.vn/buyer/login", StringComparison.OrdinalIgnoreCase) ||
                    url.StartsWith("https://shopee.vn/", StringComparison.OrdinalIgnoreCase)).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(wsUrl))
                {
                    await Task.Delay(700).ConfigureAwait(false);
                    continue;
                }

                using var page = new ClientWebSocket();
                await page.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
                await SendCdpAsync(page, 730, "Runtime.enable", null);
                await Task.Delay(500).ConfigureAwait(false);
                await SendCdpAsync(page, 731, "Runtime.evaluate", new
                {
                    expression,
                    awaitPromise = true,
                    returnByValue = true,
                });
                return;
            }
            catch (Exception ex) when (
                ex.Message.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("Cannot find context", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("Target closed", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("WebSocket", StringComparison.OrdinalIgnoreCase))
            {
                lastError = ex;
                await Task.Delay(900).ConfigureAwait(false);
            }
        }

        if (lastError is not null)
            throw lastError;
    }

    private Task<string> GetBrowserWebSocketUrlAsync() =>
        _cdpClient.GetBrowserWebSocketUrlAsync();
    private Task<string?> FindPageWebSocketUrlAsync(Func<string, bool> urlMatches) =>
        _cdpClient.FindPageWebSocketUrlAsync(urlMatches);
    private static Task<JsonElement> SendCdpAsync(ClientWebSocket socket, int id, string method, object? @params) =>
        CdpClient.SendAsync(socket, id, method, @params);
    private Task<List<Dictionary<string, object?>>> GetAllCookiesFromBraveAsync() =>
        _cookieService.GetShopeeCookiesAsync();

    /// <summary>
    /// Import BigSeller cookie từ file account vào browser qua CDP.
    /// CDP là kết nối WebSocket local — KHÔNG đi qua proxy của instance.
    /// </summary>
    /// <summary>Browser của instance này HIỆN có muc_token BigSeller sống không (qua CDP). Dùng để verify
    /// "BigSeller có out thật không": nếu 1 process báo login-first mà process ANH EM cùng khung vẫn còn
    /// token sống → account CÒN sống (chỉ process kia glitch), KHÔNG dừng cả job.</summary>
    public async Task<bool> HasBigSellerAuthAsync()
    {
        if (!_running) return false;
        try { return await BigSellerCookieEngine.HasAuthCookieInBrowserAsync(_cdpPort).ConfigureAwait(false); }
        catch { return false; }
    }

    /// <summary>
    /// GHI NGƯỢC token BigSeller server-VỪA-XOAY về FILE sau khi cào xong 1 dòng. Server xoay muc_token mỗi
    /// lần dùng → token TĨNH trong file "ôi" dần; process khác rớt token mà nạp lại token ôi = đập server
    /// bằng token chết → chuỗi auth-thất-bại dồn dập → BigSeller đá nguyên account. Lưu token MỚI NHẤT (từ
    /// browser vừa cào THÀNH CÔNG nên chắc chắn còn sống) vào file → process khác nạp lại token HIỆN HÀNH →
    /// tự hồi, không đập server. Best-effort: lỗi ghi không được làm hỏng vòng cào.
    /// </summary>
    private async Task WriteBackBigSellerTokenAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_bigSellerCookieFile) || !_running || cancellationToken.IsCancellationRequested)
            return;
        try
        {
            var cookies = await BigSellerCookieEngine.GetBigSellerCookiesAsync(_cdpPort).ConfigureAwait(false);
            // Chỉ ghi khi token còn sống (có muc_token) — tránh ghi đè file bằng cookie rỗng/chết.
            if (!BigSellerCookieEngine.HasAuthCookie(cookies))
                return;
            BigSellerCookieEngine.TryWriteCookieFile(_bigSellerCookieFile, cookies, Log);
        }
        catch { /* best-effort */ }
    }

    /// <summary>Phase 4c — ĐẦU PHIÊN: tự đăng nhập BigSeller ngay TRONG Brave scrape (khớp IP proxy) để có
    /// token tươi. Resolve email/mật khẩu từ kho BigSeller theo <c>AccountId</c> của instance; TTL + attach
    /// CDP + xuất token do <see cref="BigSellerAutoLogin.EnsureFreshSessionAsync"/> lo. AN TOÀN: chưa nhập mật
    /// khẩu → im lặng bỏ qua; mọi lỗi rơi về nạp cookie-file như cũ (không phá luồng scrape).</summary>
    /// <summary>Tk BigSeller của instance này (theo AccountId) đã nhập mật khẩu chưa — để quyết định có thử
    /// tự đăng nhập lại khi mất phiên giữa chừng không.</summary>
    private bool HasBigSellerPassword()
    {
        if (string.IsNullOrWhiteSpace(_config?.AccountId)) return false;
        var acct = BigSellerStore.Shared.Accounts.FirstOrDefault(a => a.Id == _config.AccountId);
        return acct is not null && !string.IsNullOrWhiteSpace(acct.Password);
    }

    private async Task TryAutoLoginBigSellerAsync(CancellationToken ct)
    {
        if (!_running || string.IsNullOrWhiteSpace(_config?.AccountId)) return;
        var acct = BigSellerStore.Shared.Accounts.FirstOrDefault(a => a.Id == _config.AccountId);
        if (acct is null || string.IsNullOrWhiteSpace(acct.Password)) return;   // chưa nhập pass → dùng cookie file như cũ
        try
        {
            await BigSellerAutoLogin.EnsureFreshSessionAsync(
                _cdpPort, _config.AccountId, acct.Email, acct.Password, _bigSellerCookieFile, Log, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log($"BigSeller auto-login (bỏ qua, dùng cookie file): {ex.Message}"); }
    }

    private async Task ImportBigSellerCookiesIfConfiguredAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_bigSellerCookieFile) || !_running)
            return;

        // CHẨN ĐOÁN "token mất đi đâu": ghi token ĐANG có trong browser TRƯỚC khi import (để biết import
        // có ghi đè token đang sống không, và token có hết hạn không).
        var tokenBefore = await BigSellerCookieEngine.GetAuthCookieDebugAsync(_cdpPort).ConfigureAwait(false);
        // Log RÕ tk BigSeller + file cookie đang nạp (để bắt nếu multi-BigSeller nạp nhầm file của tk khác).
        Log($"BigSeller nạp cookie: tk=\"{_config?.BigSellerAccountName ?? "?"}\" file=\"{Path.GetFileName(_bigSellerCookieFile)}\"");
        Log($"BigSeller token (trước import): {tokenBefore}");

        // ─── GIỮ TOKEN SỐNG — chống "log in first" sau vài vòng ──────────────────────────────────────
        // Server BigSeller XOAY muc_token mỗi khi cào → profile bền của instance giữ token MỚI HƠN token
        // TĨNH trong file. Trước đây hàm này LUÔN nhập đè token-file lên token-đang-sống ⇒ ép browser quay
        // về token cũ ⇒ sau vài vòng server đã xoay qua từ lâu nên TỪ CHỐI ⇒ "log in BigSeller first"
        // (xảy ra ở CẢ đi-IP-máy lẫn đi-proxy-instance vì lỗi nằm ở lệnh đè, không phải IP).
        // Quy tắc mới (cùng tinh thần đường restart-extension ở trên): chỉ nhập từ file khi
        //   • browser CHƯA có muc_token (seed lần đầu / profile mới), HOẶC
        //   • token trong file MỚI HƠN browser theo hạn (tức user vừa ĐĂNG NHẬP LẠI → file cập nhật).
        // Nếu token browser mới hơn/bằng hoặc TRÙNG giá trị file ⇒ GIỮ phiên server vừa cấp, KHÔNG đè.
        var browserTok = await BigSellerCookieEngine.GetBrowserAuthTokenInfoAsync(_cdpPort).ConfigureAwait(false);
        if (browserTok is { } bt)
        {
            var fileTok = BigSellerCookieEngine.GetFileAuthTokenInfo(_bigSellerCookieFile);
            var sameValue = fileTok is { } ft && string.Equals(ft.Value, bt.Value, StringComparison.Ordinal);
            // File mới hơn khi: file CÓ hạn rõ VÀ (browser KHÔNG hạn — session cookie, thường do user vừa
            // login lại → cần nạp token mới; HOẶC cả hai có hạn nhưng file trễ hơn). Cả hai có hạn mà browser
            // ≥ file ⇒ server vừa xoay ⇒ GIỮ (đây là fix bug "login first sau vài vòng", KHÔNG được phá).
            var fileNewer = fileTok is { Expires: { } fe } && (bt.Expires is not { } be || fe > be);
            if (sameValue || !fileNewer)
            {
                Log("BigSeller: browser đã có muc_token sống (server vừa xoay) — GIỮ phiên, KHÔNG nạp đè token cũ từ file.");
                return;
            }
            Log("BigSeller: token trong file MỚI HƠN browser (có thể bạn vừa đăng nhập lại) → nạp đè để cập nhật.");
        }

        // Nạp + XÁC NHẬN có muc_token; thử lại tối đa 3 lần (import lần đầu hay flaky do CDP/page chưa
        // sẵn sàng → trước đây nuốt lỗi 1 lần là instance đó dính "login bigseller first").
        for (var attempt = 1; attempt <= 3 && !cancellationToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await BigSellerCookieEngine.ImportFromFileAsync(
                    _cdpPort, _bigSellerCookieFile, Log,
                    reloadBigSellerTabs: false, navigateUrl: null, cancellationToken).ConfigureAwait(false);
                if (await BigSellerCookieEngine.HasAuthCookieInBrowserAsync(_cdpPort).ConfigureAwait(false))
                {
                    var tokenAfter = await BigSellerCookieEngine.GetAuthCookieDebugAsync(_cdpPort).ConfigureAwait(false);
                    Log($"BigSeller token (sau import, lần {attempt}): {tokenAfter}");
                    return;
                }
                Log($"BigSeller cookie: chưa thấy muc_token sau khi nạp — thử lại ({attempt}/3)…");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log($"BigSeller cookie (lần {attempt}): {ex.Message}"); }
            await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
        }
        Log("⚠ BigSeller cookie: KHÔNG nạp được muc_token sau 3 lần — instance này có thể bị 'login bigseller first'.");
    }

    private void SetStatus(string text)
    {
        _statusText = text;
        RaiseStatusChanged();
    }

    // Status lúc TRƯỚC khi đổi sang Captcha — để khôi phục đúng status cũ khi captcha được giải.
    private string? _statusBeforeCaptcha;

    /// <summary>Đồng bộ cột "Trạng thái" theo cờ captcha của runner: đang dính captcha → hiện "🚫 Captcha"
    /// NGAY (thay vì vẫn "Đang chạy — …"); giải xong → trả lại status cũ. Gọi mỗi nhịp onProgress của
    /// LauncherRunnerLoop (loop set config.CaptchaError tại điểm phát hiện/giải captcha).</summary>
    private void RefreshRunStatusFromConfig()
    {
        if (_config is null) return;
        if (_config.CaptchaError)
        {
            _statusBeforeCaptcha ??= _statusText;
            if (_statusText != "🚫 Captcha") SetStatus("🚫 Captcha");
        }
        else if (_statusBeforeCaptcha is not null)
        {
            SetStatus(_statusBeforeCaptcha);
            _statusBeforeCaptcha = null;
        }
    }

    private void RaiseStatusChanged() => StatusChanged?.Invoke();

    private void Log(string message)
    {
        LogLine?.Invoke(message);
        _log(message);
        // Ghi thêm ra file cố định để chẩn đoán (xem ScrapeFileLog) — không cần copy từ UI.
        ScrapeFileLog.Write(_config?.DisplayName, message);
    }
}

