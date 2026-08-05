using System.Net.Http;
using Shopee.Core.Cdp;

namespace OpenMultiBraveLauncherV3;

/// <summary>
/// Cửa hẹp mà <see cref="SessionMonitor"/> nhìn vào phiên: đọc trạng thái để biết có được phép can thiệp
/// không, và gọi các bước mở lại profile (đã có sẵn ở phiên vì chúng đụng vào profile/runner loop).
/// </summary>
internal interface ISessionMonitorHost
{
    InstanceConfig? Config { get; }
    bool IsRunning { get; }
    bool IsBusy { get; }
    bool IsRunnerLoopActive { get; }
    bool IsExtensionAutomationEnabled { get; }
    /// <summary>Đã có profile VÀ tiến trình Brave đang được theo dõi (điều kiện tối thiểu để giám sát).</summary>
    bool HasProfileAndProcess { get; }
    string? CurrentProxyFingerprint { get; set; }

    void SetStatus(string text);
    void RaiseStatusChanged();
    void Log(string message);
    /// <summary>Logger THÔ của phiên (không bắn event, không ghi ScrapeFileLog) — chỉ dùng cho catch cuối
    /// của timer async-void, nơi bản thân đường log giàu có thể lại ném (IO file / CDP đứt giữa recover).</summary>
    void LogRaw(string message);

    Task RestartProfileForProxyErrorAndResumeRunnerAsync();
    Task RelaunchProfileAndResumeRunnerAsync(string? proxyServer, string? proxyFingerprint);
    Task RestartWithFreshProxyAsync();
    Task<bool> SyncExtensionProgressAsync(bool silent, CancellationToken cancellationToken);
}

/// <summary>
/// HAI ĐỒNG HỒ giám sát của một instance: (1) mỗi 30s soi runner có kẹt / proxy có chết không rồi tự mở lại
/// profile, (2) mỗi 20s đồng bộ tiến độ extension. Tách khỏi <see cref="BraveInstanceSession"/> — cờ
/// <c>_restarting</c> vốn chỉ dùng trong hai bước này nên chuyển hẳn vào đây.
/// </summary>
internal sealed class SessionMonitor : IDisposable
{
    private readonly ISessionMonitorHost _host;
    private readonly int _cdpPort;
    private readonly KiotProxyRotator _proxy;

    private readonly System.Timers.Timer _monitorTimer;
    private readonly System.Timers.Timer _progressTimer;

    private bool _restarting;

    // Watchdog: phát hiện runner "đang chạy nhưng đứng im" (SW chết giữa chừng / tab treo).
    // Ngưỡng 8 phút > nghỉ tối đa giữa link (5 phút) nên không báo nhầm lúc nghỉ.
    private int? _watchdogLastRow;
    private DateTime _watchdogStaleSince = DateTime.UtcNow;
    private bool _watchdogRecoveredThisStall;
    private static readonly TimeSpan WatchdogStallTimeout = TimeSpan.FromMinutes(8);

    public SessionMonitor(ISessionMonitorHost host, int cdpPort, KiotProxyRotator proxy)
    {
        _host = host;
        _cdpPort = cdpPort;
        _proxy = proxy;

        _monitorTimer = new System.Timers.Timer { Interval = 30_000, AutoReset = true };
        _monitorTimer.Elapsed += async (_, _) =>
        {
            // async-void (Elapsed) → exception lọt khỏi đây = unhandled = SẬP process. Bọc toàn thân, nuốt +
            // log qua _log (vd lỗi IO khi ghi log/CDP đứt trong recover). Lượt timer sau tự chạy lại.
            try
            {
                await CheckRunnerStallAndRecoverAsync();
                await CheckProxyAndRestartIfNeededAsync();
            }
            catch (Exception ex)
            {
                try { _host.LogRaw($"Lỗi giám sát instance (bỏ qua, chờ lượt sau): {ex.Message}"); } catch { }
            }
        };
        _progressTimer = new System.Timers.Timer { Interval = 20_000, AutoReset = true };
        _progressTimer.Elapsed += (_, _) =>
        {
            if (!_host.IsRunning || !_host.IsExtensionAutomationEnabled)
                return;
            _ = _host.SyncExtensionProgressAsync(silent: true, CancellationToken.None);
        };
    }

    /// <summary>Đang trong khe mở lại profile — phiên không được coi là "rảnh" và watchdog không chồng lượt.</summary>
    public bool IsRestarting => _restarting;

    public void StartWatching() => _monitorTimer.Start();
    public void StartProgressSync() => _progressTimer.Start();

    public void StopTimers()
    {
        _monitorTimer.Stop();
        _progressTimer.Stop();
    }

    public void Dispose()
    {
        // Trước đây chỉ dừng+huỷ _monitorTimer → mỗi session đóng lại rò _progressTimer → tích luỹ qua
        // nhiều lần mở/đóng profile → góp phần đơ máy. Dọn hết ở đây.
        _monitorTimer.Stop();
        _progressTimer.Stop();
        _monitorTimer.Dispose();
        _progressTimer.Dispose();
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
        var config = _host.Config;
        var phase = config?.RunnerPhase ?? "";
        var working = _host.IsRunnerLoopActive && !_restarting && !_host.IsBusy && config is not null &&
            (phase.Equals("starting", StringComparison.OrdinalIgnoreCase) ||
             phase.Equals("opening", StringComparison.OrdinalIgnoreCase) ||
             phase.Equals("video", StringComparison.OrdinalIgnoreCase) ||
             phase.Equals("running", StringComparison.OrdinalIgnoreCase));

        if (!working)
        {
            _watchdogLastRow = config?.CurrentRow;
            _watchdogStaleSince = DateTime.UtcNow;
            _watchdogRecoveredThisStall = false;
            return;
        }

        if (config!.CurrentRow != _watchdogLastRow)
        {
            _watchdogLastRow = config.CurrentRow;
            _watchdogStaleSince = DateTime.UtcNow;
            _watchdogRecoveredThisStall = false;
            return;
        }

        if (_watchdogRecoveredThisStall ||
            DateTime.UtcNow - _watchdogStaleSince < WatchdogStallTimeout)
            return;

        _watchdogRecoveredThisStall = true;
        _restarting = true;
        _host.Log($"Watchdog: runner kẹt ở dòng {config.CurrentRow?.ToString() ?? "?"} > {WatchdogStallTimeout.TotalMinutes:0} phút — tự mở lại profile và chạy tiếp…");
        try
        {
            await _host.RestartProfileForProxyErrorAndResumeRunnerAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _host.Log($"Watchdog mở lại lỗi: {ex.Message}");
        }
        finally
        {
            _restarting = false;
        }
    }

    private async Task CheckProxyAndRestartIfNeededAsync()
    {
        var config = _host.Config;
        if (!_host.IsRunning || _restarting || _host.IsBusy || !_host.HasProfileAndProcess || config is null)
            return;

        var taskRunDispatched = false;
        try
        {
            var hasKiot = !string.IsNullOrWhiteSpace(config.KiotProxyKey.Trim());
            string? server;

            if (!hasKiot)
            {
                server = string.IsNullOrWhiteSpace(config.ManualProxy)
                    ? null
                    : KiotProxyRotator.NormalizeManualProxy(config.ManualProxy, config.ProxyType);

                // Khi runner đang chạy: KHÔNG để monitor mở lại profile vì proxy thủ công cố định —
                // mở lại cũng dính cùng proxy chết → kẹt "đang mở link X/Y → reload" lặp vô hạn.
                // Để runner tự phát hiện proxyError ở bước scrape rồi tạm dừng (handoff) — sạch hơn.
                if (!_host.IsRunnerLoopActive && await HasChromeProxyErrorPageAsync().ConfigureAwait(false))
                {
                    _restarting = true;
                    _host.Log("Phát hiện ERR_PROXY/No internet - tự khởi động lại profile...");
                    await _host.RestartProfileForProxyErrorAndResumeRunnerAsync().ConfigureAwait(false);
                }
                return;
            }

            Dictionary<string, object> current;
            try
            {
                current = await _proxy.GetCurrentProxyAsync();
            }
            catch (HttpRequestException ex)
            {
                _host.SetStatus("Không kết nối KiotProxy API");
                _host.Log($"Monitor: {ex.Message}");
                return;
            }
            catch (Exception ex) when (KiotProxyRotator.IsProxyExpiredError(ex.Message))
            {
                _restarting = true;
                try { await _host.RestartWithFreshProxyAsync(); }
                catch (Exception rfEx) { _host.Log($"Refresh: {rfEx.Message}"); }
                return;
            }

            var fp = KiotProxyRotator.BuildFingerprint(current);
            server = KiotProxyRotator.BuildProxyServer(current, config.ProxyType);

            if (!string.Equals(fp, _host.CurrentProxyFingerprint, StringComparison.Ordinal))
            {
                // KiotProxy (random) xoay proxy là BÌNH THƯỜNG — KHÔNG relaunch profile đang scrape ngon
                // chỉ vì fingerprint đổi (gây "được tí lại reload" liên tục mỗi 30s). Chỉ cập nhật fp.
                // Nếu proxy cũ thật sự chết: Brave hiện ERR_PROXY (xử lý ngay dưới) hoặc scrape step báo
                // proxy error → handoff. Như vậy vẫn không "chết" khi đổi proxy mà không reload vô cớ.
                _host.CurrentProxyFingerprint = fp;
            }

            // Proxy API báo OK nhưng Brave vẫn hiện "No internet"/ERR_PROXY… (tab chrome-error).
            // Trường hợp này user đang phải Đóng profile → Mở lại thủ công. Tự động làm tương tự.
            // Runner đang chạy → để bước scrape tự bắt proxyError và handoff sang profile khác (proxy mới).
            // Monitor mở lại ở đây sẽ huỷ scrape đang chạy + dùng lại proxy cũ → vòng reload cùng một dòng.
            if (!_host.IsRunnerLoopActive && await HasChromeProxyErrorPageAsync().ConfigureAwait(false))
            {
                _restarting = true;
                taskRunDispatched = true;
                var restartServer = server;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        _host.Log("Phát hiện ERR_PROXY/No internet trên tab - tự khởi động lại profile...");
                        await _host.RelaunchProfileAndResumeRunnerAsync(restartServer, proxyFingerprint: null)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _host.Log($"Restart profile (proxy error): {ex.Message}");
                    }
                    finally
                    {
                        _restarting = false;
                        _host.RaiseStatusChanged();
                    }
                });
                return;
            }
        }
        catch (Exception ex)
        {
            _host.SetStatus($"Monitor: {ex.Message}");
            _host.Log(ex.Message);
        }
        finally
        {
            if (!taskRunDispatched)
            {
                _restarting = false;
                _host.RaiseStatusChanged();
            }
        }
    }

    private async Task<bool> HasChromeProxyErrorPageAsync()
    {
        try
        {
            foreach (var target in await CdpClient.ListTargetsAsync(_cdpPort).ConfigureAwait(false))
            {
                if (!target.IsPage)
                    continue;

                if (target.Url.StartsWith("chrome-error://", StringComparison.OrdinalIgnoreCase) ||
                    target.Title.Contains("No internet", StringComparison.OrdinalIgnoreCase) ||
                    target.Title.Contains("ERR_PROXY_CONNECTION_FAILED", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }
        return false;
    }
}
