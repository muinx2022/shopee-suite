namespace OpenMultiBraveLauncherV3;

/// <summary>
/// DỰNG / MỞ LẠI PROFILE của phiên: một đường thực thi duy nhất (<see cref="BringUpProfileAsync"/>) cho cả
/// cold start lẫn mọi kiểu warm restart, cộng các wrapper "mở lại rồi chạy tiếp" mà watchdog/monitor gọi.
/// </summary>
internal sealed partial class BraveInstanceSession
{
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

    private DirectoryInfo EnsureProfile(DirectoryInfo sourceUserData, InstanceConfig config) =>
        BraveProfileManager.EnsureProfile(sourceUserData, config, Log);

    /// <summary>Xóa script cache của service worker để Brave load lại extension mới nhất từ disk.</summary>
    private static void PrepareProfileForLaunch(string profileRoot) =>
        BraveProfileManager.PrepareProfileForLaunch(profileRoot);

    private string BuildBraveArguments(string userDataDir, string? proxyServer, string? bigSellerProxyServer) =>
        BraveProfileManager.BuildBraveArguments(
            _cdpPort, userDataDir, proxyServer, Log, _sourceUserData,
            loadRunnerExtension: _extensionAutomationEnabled,
            bigSellerProxyServer: bigSellerProxyServer);

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
        await _brave.KillAndWaitPortFreeAsync().ConfigureAwait(false);

        // Phân giải proxy RIÊNG của tk BigSeller (nếu có key) NGAY trước khi build args → bigseller.com
        // split-tunnel qua IP này (PAC), Shopee giữ proxyServer của instance. Không key → null = IP máy.
        var bigSellerProxyServer = await _bigSeller.ResolveProxyServerAsync().ConfigureAwait(false);
        var args = BuildBraveArguments(_profileRoot.FullName, proxyServer, bigSellerProxyServer);
        _brave.Launch(_braveExe, args);
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
            proxyData = await _proxy.GetWorkingProxyAsync(preferFresh: true, avoidFingerprint: _currentProxyFingerprint)
                .ConfigureAwait(false);
            server = KiotProxyRotator.BuildProxyServer(proxyData, _config.ProxyType);
        }
        else
        {
            (server, proxyData) = await _proxy.ResolveForLaunchAsync().ConfigureAwait(false);
        }

        await RelaunchProfileAsync(
            server,
            proxyData is not null ? KiotProxyRotator.BuildFingerprint(proxyData) : server ?? "").ConfigureAwait(false);
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

        var proxy = await _proxy.GetWorkingProxyAsync(preferFresh: true, avoidFingerprint: _currentProxyFingerprint);
        var server = KiotProxyRotator.BuildProxyServer(proxy, _config.ProxyType);
        await RelaunchProfileAsync(server, KiotProxyRotator.BuildFingerprint(proxy)).ConfigureAwait(false);
    }

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
}
