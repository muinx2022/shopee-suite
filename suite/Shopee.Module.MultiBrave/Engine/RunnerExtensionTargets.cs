using System.Net.WebSockets;
using System.Text.Json;
using Shopee.Core.Cdp;
using Shopee.Core.Infrastructure;

namespace OpenMultiBraveLauncherV3;

/// <summary>
/// TRA CỨU extension runner và các target của nó trên CDP: ID extension (từ thư mục nạp, từ
/// <c>Preferences</c> của profile, từ target đang mở), WS/targetId của service worker và của popup, cộng
/// vài tiện ích CDP cấp thấp. Thuần ĐỌC — không đánh thức, không đóng gì (xem <see cref="RunnerSwLifecycle"/>
/// và <see cref="RunnerExtensionTabs"/>).
/// </summary>
internal static class RunnerExtensionTargets
{
    public static async Task<List<string>> DiscoverRunnerExtensionIdsAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        var loaded = RunnerExtensionPaths.TryGetLoadedExtensionId();
        if (!string.IsNullOrWhiteSpace(loaded) && seen.Add(loaded))
            result.Add(loaded);

        foreach (var id in await DiscoverExtensionIdsFromBrowserAsync(cdpPort, ct).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(loaded) || string.Equals(id, loaded, StringComparison.OrdinalIgnoreCase))
            {
                if (seen.Add(id))
                    result.Add(id);
            }
        }

        if (result.Count > 0)
            return result;

        foreach (var id in DiscoverExtensionIdsFromProfile(profileRoot))
        {
            if (seen.Add(id))
                result.Add(id);
        }

        return result;
    }

    public static string? TryGetRunnerExtensionIdFromProfile(DirectoryInfo profileRoot) =>
        DiscoverExtensionIdsFromProfile(profileRoot).FirstOrDefault();

    public static List<string> DiscoverExtensionIdsFromProfile(DirectoryInfo profileRoot)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var defaultDir = Path.Combine(profileRoot.FullName, "Default");
        DiscoverExtensionIdsFromPreferences(Path.Combine(defaultDir, "Preferences"), ids);
        DiscoverExtensionIdsFromPreferences(Path.Combine(defaultDir, "Secure Preferences"), ids);
        return ids.ToList();
    }

    private static void DiscoverExtensionIdsFromPreferences(string preferencesPath, ISet<string> ids)
    {
        if (!File.Exists(preferencesPath))
            return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(preferencesPath));
            if (!doc.RootElement.TryGetProperty("extensions", out var extensions) ||
                !extensions.TryGetProperty("settings", out var settings) ||
                settings.ValueKind != JsonValueKind.Object)
                return;

            var loadDir = RunnerExtensionPaths.ResolveLoadDirectory();
            foreach (var setting in settings.EnumerateObject())
            {
                var id = setting.Name;
                if (id.Length != 32 || !id.All(c => c is >= 'a' and <= 'p'))
                    continue;

                var root = setting.Value;
                var manifestName = "";
                var defaultPopup = "";
                if (root.TryGetProperty("manifest", out var manifest) &&
                    manifest.ValueKind == JsonValueKind.Object)
                {
                    manifestName = manifest.TryGetProperty("name", out var nameEl)
                        ? nameEl.GetString() ?? ""
                        : "";
                    if (manifest.TryGetProperty("action", out var action) &&
                        action.ValueKind == JsonValueKind.Object &&
                        action.TryGetProperty("default_popup", out var popupEl))
                        defaultPopup = popupEl.GetString() ?? "";
                }

                var path = root.TryGetProperty("path", out var pathEl)
                    ? pathEl.GetString() ?? ""
                    : "";

                var nameMatches = string.Equals(
                    manifestName, RunnerExtensionPaths.ExtensionDisplayName,
                    StringComparison.OrdinalIgnoreCase);
                var popupMatches = string.Equals(defaultPopup, "popup.html", StringComparison.OrdinalIgnoreCase);
                var pathMatches = !string.IsNullOrWhiteSpace(loadDir) &&
                    PathsEqual(path, loadDir);

                if (pathMatches || (nameMatches && popupMatches))
                    ids.Add(id);
            }
        }
        catch
        {
            // Preferences có thể đang bị Brave ghi; bỏ qua, vòng sau sẽ thử lại.
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<List<string>> DiscoverExtensionIdsFromBrowserAsync(int cdpPort, CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in await CdpClient.TryListTargetsAsync(cdpPort, ct))
            TryAddExtensionIdFromUrl(target.Url, ids);

        ClientWebSocket? browser = null;
        try
        {
            browser = await ConnectBrowserWebSocketAsync(cdpPort, ct);
            var targets = await CdpClient.SendAsync(browser, 40, "Target.getTargets", new { }, ct,
                receiveTimeoutMs: RunnerExtensionRpc.CdpReceiveTimeoutMs);
            if (targets.TryGetProperty("targetInfos", out var targetInfos))
            {
                foreach (var target in targetInfos.EnumerateArray())
                {
                    var url = target.TryGetProperty("url", out var urlEl) ? urlEl.GetString() ?? "" : "";
                    TryAddExtensionIdFromUrl(url, ids);
                }
            }
        }
        catch
        {
            // ignore
        }
        finally
        {
            if (browser is not null)
            {
                try
                {
                    if (browser.State == WebSocketState.Open)
                        await browser.CloseAsync(WebSocketCloseStatus.NormalClosure, "", ct);
                }
                catch
                {
                    // ignore
                }

                browser.Dispose();
            }
        }

        return ids.ToList();
    }

    private static void TryAddExtensionIdFromUrl(string url, ISet<string> ids)
    {
        if (!url.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
            return;

        var rest = url["chrome-extension://".Length..];
        var slash = rest.IndexOf('/');
        var id = slash >= 0 ? rest[..slash] : rest;
        if (id.Length == 32 && id.All(c => c is >= 'a' and <= 'p'))
            ids.Add(id);
    }

    /// <summary>
    /// Lấy webSocketDebuggerUrl của SW extension từ /json/list (Brave không trả targetId trong Target.getTargets).
    /// </summary>
    public static async Task<string?> GetSwDebuggerUrlFromListAsync(
        int cdpPort, string extensionId, CancellationToken ct)
    {
        foreach (var target in await CdpClient.TryListTargetsAsync(cdpPort, ct))
        {
            if (target.IsServiceWorker &&
                target.Url.Contains(extensionId, StringComparison.OrdinalIgnoreCase) &&
                target.HasWsUrl)
                return target.WsUrl;
        }
        return null;
    }

    /// <summary>
    /// Lấy id (targetId CDP) của SW extension từ /json/list.
    /// Brave không trả service_worker qua Target.getTargets, nhưng /json/list có đầy đủ thông tin.
    /// </summary>
    public static async Task<string?> GetSwTargetIdFromListAsync(
        int cdpPort, string extensionId, CancellationToken ct)
    {
        foreach (var target in await CdpClient.TryListTargetsAsync(cdpPort, ct))
        {
            if (target.IsServiceWorker &&
                target.Url.Contains(extensionId, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(target.Id))
                return target.Id;
        }
        return null;
    }

    public static async Task<string?> FindExtensionPopupDebuggerUrlAsync(
        int cdpPort,
        string extensionId,
        CancellationToken ct)
    {
        var popupUrl = $"chrome-extension://{extensionId}/popup.html";
        foreach (var target in await CdpClient.TryListTargetsAsync(cdpPort, ct))
        {
            if (target.Url.Equals(popupUrl, StringComparison.OrdinalIgnoreCase) && target.HasWsUrl)
                return target.WsUrl;
        }

        return null;
    }

    public static async Task<string?> FindExtensionPopupTargetIdAsync(
        int cdpPort,
        string extensionId,
        CancellationToken ct)
    {
        var popupUrl = $"chrome-extension://{extensionId}/popup.html";
        foreach (var target in await CdpClient.TryListTargetsAsync(cdpPort, ct))
        {
            if (target.Url.Equals(popupUrl, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(target.Id))
                return target.Id;
        }

        return null;
    }

    /// <summary>Dump tất cả entries trong /json/list để debug (type + url ngắn).</summary>
    public static async Task<string> GetAllSwTargetsSummaryAsync(int cdpPort, CancellationToken ct)
    {
        try
        {
            var entries = new List<string>();
            foreach (var target in await CdpClient.ListTargetsAsync(cdpPort, ct))
            {
                var type = string.IsNullOrEmpty(target.Type) ? "?" : target.Type;
                var wsOk = target.WsUrl is null ? "ws-" : "ws+";
                var shortUrl = target.Url.Length > 55 ? target.Url[..55] : target.Url;
                entries.Add($"{type}({wsOk}):{shortUrl}");
            }
            return entries.Count == 0 ? "(list rỗng)" : string.Join(" | ", entries);
        }
        catch (HttpRequestException) { return "(HTTP fail)"; }
        catch (Exception ex) { return $"(ex: {ex.Message})"; }
    }

    public static async Task<ClientWebSocket> ConnectBrowserWebSocketAsync(int cdpPort, CancellationToken ct)
    {
        using var response = await AppServices.DirectHttp.GetAsync(CdpEndpoints.Version(cdpPort), ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var wsUrl = doc.RootElement.GetProperty("webSocketDebuggerUrl").GetString()
            ?? throw new InvalidOperationException("CDP browser endpoint không khả dụng.");

        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(wsUrl), ct);
        return socket;
    }

    public static async Task<bool> IsCdpPortReachableAsync(int port, CancellationToken ct)
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            var connectTask = tcp.ConnectAsync(CdpEndpoints.Host, port, ct).AsTask();
            return await Task.WhenAny(connectTask, Task.Delay(1500, ct)) == connectTask
                   && connectTask.IsCompletedSuccessfully;
        }
        catch
        {
            return false;
        }
    }
}
