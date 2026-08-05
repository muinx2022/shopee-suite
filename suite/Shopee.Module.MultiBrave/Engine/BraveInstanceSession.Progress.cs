namespace OpenMultiBraveLauncherV3;

/// <summary>
/// ĐỒNG BỘ TIẾN ĐỘ extension về <see cref="InstanceConfig"/> của phiên (đọc qua CDP khi Brave còn sống,
/// đọc file profile khi đã đóng), phát hiện "bị dừng giữa chừng" để gợi ý chạy tiếp, và phản chiếu cờ
/// captcha của runner lên cột Trạng thái.
/// </summary>
internal sealed partial class BraveInstanceSession
{
    private string? _lastInterruptLogSignature;
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
}
