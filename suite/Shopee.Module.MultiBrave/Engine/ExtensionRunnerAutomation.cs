namespace OpenMultiBraveLauncherV3;

/// <summary>
/// CỬA VÀO DUY NHẤT của module tới extension "Shopee Data Runner" — giữ nguyên API mà
/// <see cref="LauncherRunnerLoop"/>, <see cref="BraveInstanceSession"/> và
/// <see cref="ExtensionProgressCoordinator"/> vẫn gọi. Phần thân đã tách làm hai nửa theo trách nhiệm:
/// <list type="bullet">
/// <item><see cref="RunnerSwLifecycle"/> — vòng đời service worker (chờ sẵn sàng, đánh thức, reload, pin).</item>
/// <item><see cref="RunnerExtensionRpc"/> — gửi lệnh vào SW rồi chờ kết quả (JS expression, retry, map kết quả).</item>
/// <item><see cref="RunnerExtensionTargets"/> — tra cứu ID extension + target/WS trên CDP.</item>
/// <item><see cref="RunnerExtensionTabs"/> — dọn popup extension và tab phụ.</item>
/// </list>
/// Hai method đọc dữ liệu link (ResolveEndRow / FetchSheetLinks) không dính CDP nên đã chuyển hẳn sang
/// <c>Shopee.Core.Scrape.ScrapeLinkSource</c> — gọi thẳng ở đó, KHÔNG đi qua lớp này nữa.
/// </summary>
internal static class ExtensionRunnerAutomation
{
    // ── Vòng đời service worker ────────────────────────────────────────────────────────────────────
    /// <summary>Xóa ID cache khi Brave khởi động lại / dừng instance.</summary>
    public static void ClearResolvedExtension(int cdpPort) =>
        RunnerSwLifecycle.ClearResolvedExtension(cdpPort);

    public static Task<string> EnsureRunnerExtensionReadyAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        Action<string>? log,
        CancellationToken cancellationToken = default,
        int timeoutSeconds = 90,
        Action<bool>? onCaptchaState = null) =>
        RunnerSwLifecycle.EnsureRunnerExtensionReadyAsync(
            cdpPort, profileRoot, log, cancellationToken, timeoutSeconds, onCaptchaState);

    public static string? TryGetRunnerExtensionIdFromProfile(DirectoryInfo profileRoot) =>
        RunnerExtensionTargets.TryGetRunnerExtensionIdFromProfile(profileRoot);

    /// <summary>Đóng tab phụ (extension popup, New Tab, SP cũ, login) và giữ ít nhất 1 tab.</summary>
    public static Task TrimAuxiliaryTabsAsync(
        int cdpPort,
        CancellationToken ct,
        bool closeShopeeLoginTabs = true) =>
        RunnerExtensionTabs.TrimAuxiliaryTabsAsync(cdpPort, ct, closeShopeeLoginTabs);

    public static Task PinSwWithFlatSessionAsync(
        int cdpPort, string extensionId, Action<string> log, CancellationToken ct) =>
        RunnerSwLifecycle.PinSwWithFlatSessionAsync(cdpPort, extensionId, log, ct);

    // ── Lệnh gửi vào extension ─────────────────────────────────────────────────────────────────────
    public static Task<RunnerExtensionRpc.ScrapeStepResult> ExecuteScrapeStepAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        string link,
        int rowNumber,
        string statusText,
        string instanceName,
        string sku,
        int? tabId,
        CancellationToken cancellationToken = default) =>
        RunnerExtensionRpc.ExecuteScrapeStepAsync(
            cdpPort, profileRoot, link, rowNumber, statusText, instanceName, sku, tabId, cancellationToken);

    public static Task<RunnerExtensionRpc.BeforeNextLinkCheckResult> CheckBeforeNextLinkAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        int tabId,
        int rowNumber,
        string instanceName,
        string sku,
        CancellationToken cancellationToken = default) =>
        RunnerExtensionRpc.CheckBeforeNextLinkAsync(
            cdpPort, profileRoot, tabId, rowNumber, instanceName, sku, cancellationToken);

    public static Task SetDisplayStateAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        object state,
        CancellationToken cancellationToken = default) =>
        RunnerExtensionRpc.SetDisplayStateAsync(cdpPort, profileRoot, state, cancellationToken);

    public static Task ShowOverlayAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        int tabId,
        string text,
        CancellationToken cancellationToken = default) =>
        RunnerExtensionRpc.ShowOverlayAsync(cdpPort, profileRoot, tabId, text, cancellationToken);

    public static Task HideOverlayAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        int tabId,
        CancellationToken cancellationToken = default) =>
        RunnerExtensionRpc.HideOverlayAsync(cdpPort, profileRoot, tabId, cancellationToken);

    public static Task AbortScrapeStepAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        CancellationToken cancellationToken = default) =>
        RunnerExtensionRpc.AbortScrapeStepAsync(cdpPort, profileRoot, cancellationToken);

    public static Task StopRunAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        Action<string> log,
        CancellationToken cancellationToken = default) =>
        RunnerExtensionRpc.StopRunAsync(cdpPort, profileRoot, log, cancellationToken);

    public static Task<ExtensionRunnerState?> TryReadStateViaCdpAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        CancellationToken cancellationToken = default) =>
        RunnerExtensionRpc.TryReadStateViaCdpAsync(cdpPort, profileRoot, cancellationToken);

    public static Task<bool> TryApplyFormConfigAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        string sheetName,
        int? startRow,
        int? endRow,
        CancellationToken cancellationToken = default) =>
        RunnerExtensionRpc.TryApplyFormConfigAsync(
            cdpPort, profileRoot, sheetName, startRow, endRow, cancellationToken);

    public static Task TryBroadcastRunnerStateAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        CancellationToken cancellationToken = default) =>
        RunnerExtensionRpc.TryBroadcastRunnerStateAsync(cdpPort, profileRoot, cancellationToken);
}
