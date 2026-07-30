using Shopee.Core.BigSeller;

namespace OpenMultiBraveLauncherV3;

/// <summary>
/// VÒNG CHẠY RUNNER của phiên: giành quyền chạy (nguyên tử), dựng Brave + đăng nhập Shopee + token
/// BigSeller, gọi <see cref="LauncherRunnerLoop"/>, xử lý các đường hồi phục (lỗi extension, mất phiên
/// BigSeller giữa chừng) rồi dừng sạch. Kèm SW pinner giữ service worker sống suốt vòng chạy.
/// </summary>
internal sealed partial class BraveInstanceSession
{
    private CancellationTokenSource? _runnerLoopCts;
    private Task? _runnerLoopTask;
    private volatile bool _runnerLoopActive;

    private CancellationTokenSource? _swPinnerCts;
    private Task? _swPinnerTask;

    // SW runner hay không lên ở vài vòng đầu khi mở profile mới — relaunch lại tới 4 lần (mỗi vòng đã
    // nhanh hơn nhờ MaxPopupReopenBeforeRelaunch=2) để profile "lì" vẫn lên thay vì bị bỏ cuộc/đánh lỗi.
    private const int MaxExtensionRelaunchRetries = 4;

    // 0/1: có 1 vòng runner đã được GIÀNH quyền chạy (claim ở ResumeContinueAsync) chưa. Là int để guard bằng
    // Interlocked.CompareExchange (nguyên tử) — trước đây guard đọc _runnerLoopActive vốn chỉ bật SÂU trong
    // Task.Run sau nhiều await, nên 2 lời gọi sát nhau (user bấm + watchdog) đều lọt = 2 vòng cùng profile.
    private int _runnerLoopRequested;
    // Đang trong khe cancel→relaunch→resume runner (watchdog/proxy mở lại profile rồi chạy tiếp). Lúc này
    // _runnerLoopActive/_runnerLoopRequested tạm = false nên scheduler tưởng profile rảnh → mở thêm profile
    // = VƯỢT MAX. Cờ này giữ profile vẫn "đang làm việc" (IsRunnerLoopPending) suốt khe đó.
    private volatile bool _runnerResuming;

    public Task ResumeContinueAsync(
        string braveExe,
        string sourceUserData,
        bool preferSuggestedResume = true,
        bool retryExtensionStart = false,
        CancellationToken cancellationToken = default)
    {
        if (_config is null)
            throw new InvalidOperationException("Chưa chọn cấu hình instance.");

        // GUARD NGUYÊN TỬ: giành quyền chạy vòng runner. CompareExchange đóng khe mà guard cũ (đọc
        // _runnerLoopActive — cờ chỉ bật sâu trong Task.Run sau nhiều await) để hở → 2 lời gọi sát nhau
        // (user bấm + watchdog) đều lọt = 2 vòng cùng profile. Chỉ đúng 1 lời gọi giành được (0→1).
        if (Interlocked.CompareExchange(ref _runnerLoopRequested, 1, 0) != 0)
        {
            Log("Runner đang chạy trên launcher.");
            return Task.CompletedTask;
        }

        // Huỷ vòng cũ (nếu còn) rồi tạo CTS mới. Mỗi vòng dùng token CỤC BỘ (loopToken) và TỰ
        // Dispose CTS của mình ở finally — tránh: (a) rò CTS/đăng-ký-linked qua mỗi lần resume,
        // (b) task cũ đọc nhầm token mới khi field bị thay (trước đây body đọc thẳng field CTS).
        // Bọc try/catch để NHẢ cờ đã giành nếu dựng CTS ném (kẻo kẹt cờ = không bao giờ chạy lại được).
        CancellationTokenSource cts;
        try
        {
            try { _runnerLoopCts?.Cancel(); } catch (ObjectDisposedException) { }
            cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }
        catch { Interlocked.Exchange(ref _runnerLoopRequested, 0); throw; }
        _runnerLoopCts = cts;
        var loopToken = cts.Token;

        // Cờ resume đóng ở đây (đã giành _runnerLoopRequested nguyên tử ở guard trên, ĐỒNG BỘ trước Task.Run
        // nên không có khe IsRunnerLoopPending=false để scheduler mở thêm profile vượt Max).
        _runnerResuming = false;

        _runnerLoopTask = Task.Run(async () =>
        {
            // Cổng warmup: giữ trong lúc chờ SW cold-start, thả khi SW lên (onAfter) / lỗi (catch) / kết
            // thúc (finally). Khai báo NGOÀI try để finally truy cập được → không rò permit (tránh deadlock).
            var warmupHeld = false;
            void ReleaseWarmup() { if (warmupHeld) { warmupHeld = false; WarmupRelease?.Invoke(); } }
            try
            {
                var braveDied = _running && _brave.HasExited;
                if (braveDied)
                {
                    Log("Brave đã tắt - đang khởi động lại...");
                    StopSwPinner();
                    _running = false;
                    _brave.DiscardExitedProcess();
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
                await _bigSeller.TryAutoLoginAsync(loopToken).ConfigureAwait(false);

                // Import BigSeller cookie (nếu account có cấu hình) — qua CDP local, KHÔNG qua proxy instance.
                await _bigSeller.ImportCookiesIfConfiguredAsync(loopToken).ConfigureAwait(false);

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
                            onScrapeSucceeded: () => _bigSeller.WriteBackTokenAsync(loopToken)).ConfigureAwait(false);
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
                        if (_bigSeller.HasCookieFile)
                        {
                            if (await _bigSeller.BrowserHasAuthCookieAsync().ConfigureAwait(false))
                                Log("BigSeller cookie: profile mở lại vẫn còn muc_token — GIỮ phiên sống, KHÔNG nạp đè.");
                            else
                                await _bigSeller.ImportCookiesIfConfiguredAsync(loopToken).ConfigureAwait(false);
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
                        && _bigSeller.HasPassword())
                    {
                        bigSellerReloginTries++;
                        Log($"BigSeller mất phiên giữa chừng — TỰ đăng nhập lại rồi chạy tiếp ({bigSellerReloginTries}/{maxBigSellerReloginTries})…");
                        if (!string.IsNullOrWhiteSpace(_config?.AccountId))
                            BigSellerSessionRegistry.Invalidate(_config!.AccountId);   // ép login lại (bỏ TTL)
                        await _bigSeller.TryAutoLoginAsync(loopToken).ConfigureAwait(false);
                        await _bigSeller.ImportCookiesIfConfiguredAsync(loopToken).ConfigureAwait(false);
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
                if (_runnerLoopRequested != 0 && _config is not null)
                {
                    RunnerLoopEnded?.Invoke(_config.Id);
                    Interlocked.Exchange(ref _runnerLoopRequested, 0);
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
        if (!_runnerLoopActive && _runnerLoopRequested == 0 && _runnerLoopTask is null)
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
        if (_runnerLoopActive || _runnerLoopRequested != 0 || _runnerLoopTask is { IsCompleted: false })
            await StopRunnerAsync(cancellationToken).ConfigureAwait(false);

        if (_running)
            await StopAsync(cancellationToken).ConfigureAwait(false);
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

    private static bool IsExtensionConnectionError(string message) =>
        message.Contains("extension", StringComparison.OrdinalIgnoreCase) &&
        (message.Contains("CDP", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("không kết nối", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("không phản hồi", StringComparison.OrdinalIgnoreCase));
}
