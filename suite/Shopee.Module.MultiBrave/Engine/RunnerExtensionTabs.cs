using System.Text.RegularExpressions;
using Shopee.Core.Cdp;

namespace OpenMultiBraveLauncherV3;

/// <summary>
/// DỌN TAB của profile scrape: đóng popup extension (cũ/orphan) trước mỗi lần đánh thức SW, và trước khi
/// chạy thì tỉa các tab phụ (New Tab, trang SP của lượt trước, trang login) — luôn GIỮ lại ít nhất 1 tab
/// để Brave không tự thoát.
/// </summary>
internal static class RunnerExtensionTabs
{
    public static async Task CloseAllExtensionPopupTabsAsync(int cdpPort, CancellationToken ct)
    {
        foreach (var target in await CdpClient.TryListTargetsAsync(cdpPort, ct))
        {
            if (!target.Url.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
                continue;

            await CdpClient.CloseTargetAsync(cdpPort, target.Id, ct);
        }
    }

    public static async Task CloseRunnerExtensionPopupTabsAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        CancellationToken ct)
    {
        var runnerIds = new HashSet<string>(
            RunnerExtensionTargets.DiscoverExtensionIdsFromProfile(profileRoot),
            StringComparer.OrdinalIgnoreCase);
        var loadedId = RunnerExtensionPaths.TryGetLoadedExtensionId();
        if (!string.IsNullOrWhiteSpace(loadedId))
            runnerIds.Add(loadedId);
        if (runnerIds.Count == 0)
            return;

        foreach (var target in await CdpClient.TryListTargetsAsync(cdpPort, ct))
        {
            if (!runnerIds.Any(id => target.Url.Equals(
                    $"chrome-extension://{id}/popup.html",
                    StringComparison.OrdinalIgnoreCase)))
                continue;

            await CdpClient.CloseTargetAsync(cdpPort, target.Id, ct);
        }
    }

    /// <summary>Đóng tab phụ (extension popup, New Tab, SP cũ, login) và giữ ít nhất 1 tab.</summary>
    public static async Task TrimAuxiliaryTabsAsync(
        int cdpPort,
        CancellationToken ct,
        bool closeShopeeLoginTabs = true)
    {
        await CloseAllExtensionPopupTabsAsync(cdpPort, ct);

        var pages = (await CdpClient.TryListTargetsAsync(cdpPort, ct))
            .Where(t => t.IsPage && !string.IsNullOrWhiteSpace(t.Id))
            .Select(t => (t.Id, t.Url))
            .ToList();

        if (pages.Count == 0)
            return;

        var toClose = pages
            .Where(p => ShouldCloseAuxiliaryTab(p.Url, closeShopeeLoginTabs))
            .Select(p => p.Id)
            .ToList();

        while (toClose.Count > 0 && pages.Count - toClose.Count < 1)
            toClose.RemoveAt(toClose.Count - 1);

        foreach (var id in toClose)
            await CdpClient.CloseTargetAsync(cdpPort, id, ct);
    }

    private static bool ShouldCloseAuxiliaryTab(string url, bool closeShopeeLoginTabs)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (url.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
            return true;

        if (closeShopeeLoginTabs &&
            url.StartsWith("https://shopee.vn/buyer/login", StringComparison.OrdinalIgnoreCase))
            return true;

        if (IsNewTabUrl(url))
            return true;

        return IsShopeeProductUrl(url);
    }

    private static bool IsNewTabUrl(string url) =>
        url.Equals("about:blank", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("chrome://newtab", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("brave://newtab", StringComparison.OrdinalIgnoreCase);

    private static bool IsShopeeProductUrl(string url)
    {
        if (!url.Contains("shopee", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var uri = new Uri(url);
            if (Regex.IsMatch(uri.AbsolutePath, @"-i\.\d+\.\d+", RegexOptions.IgnoreCase))
                return true;
            return uri.Query.Contains("itemid=", StringComparison.OrdinalIgnoreCase) ||
                   uri.Query.Contains("shopid=", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return url.Contains("-i.", StringComparison.OrdinalIgnoreCase);
        }
    }
}
