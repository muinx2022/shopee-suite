using Shopee.Core.Proxy;

namespace OpenMultiBraveLauncherV3;

/// <summary>
/// CHỌN/XOAY proxy cho một instance: ưu tiên IP đang giữ của key KiotProxy (<c>/current</c>), chỉ gọi
/// <c>/new</c> khi IP hết hạn, và tránh lấy lại đúng proxy vừa chết khi đang khôi phục. Cũng lo nhánh proxy
/// thủ công / không proxy. Tách khỏi <see cref="BraveInstanceSession"/> — phiên chỉ nhận về chuỗi
/// <c>proxyServer</c> + fingerprint để dựng args và so sánh.
/// </summary>
internal sealed class KiotProxyRotator(Func<InstanceConfig?> config, Action<string> log)
{
    private InstanceConfig Config =>
        config() ?? throw new InvalidOperationException("Chưa cấu hình instance.");

    /// <summary>Proxy dùng cho lần mở profile này: Kiot key → thủ công → không proxy (theo đúng thứ tự ưu tiên cũ).</summary>
    public async Task<(string? proxyServer, Dictionary<string, object>? proxyData)> ResolveForLaunchAsync()
    {
        var cfg = Config;

        var kiotKey = cfg.KiotProxyKey.Trim();
        if (!string.IsNullOrWhiteSpace(kiotKey))
        {
            var proxy = await GetWorkingProxyAsync().ConfigureAwait(false);
            var server = BuildProxyServer(proxy, cfg.ProxyType);
            return (server, proxy);
        }

        var manual = cfg.ManualProxy.Trim();
        if (!string.IsNullOrWhiteSpace(manual))
        {
            var server = NormalizeManualProxy(manual, cfg.ProxyType);
            log($"Dùng proxy thủ công: {server}");
            return (server, null);
        }

        if (cfg.RequireProxy)
            throw new InvalidOperationException(
                "Instance chưa có proxy (KiotProxy key / proxy thủ công) — không mở profile để tránh login Shopee bằng IP máy.");

        log(cfg.RequireProxy
            ? "Không có proxy — mở profile bằng IP máy (đã xác nhận bỏ qua cảnh báo)."
            : "Không có Kiot key / proxy thủ công — mở profile không proxy.");
        return (null, null);
    }

    public static string NormalizeManualProxy(string input, string proxyType)
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

    private Task<Dictionary<string, object>> GetProxyAsync()
    {
        if (config() is null) throw new InvalidOperationException("Chua cau hinh instance.");
        return KiotProxyClient.GetNewProxyAsync(Config.KiotProxyKey, Config.Region, log);
    }

    public async Task<Dictionary<string, object>> GetWorkingProxyAsync(
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

            var proxyServer = BuildProxyServer(proxy, Config.ProxyType);
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
            log($"Proxy từ KiotProxy ({attempt}/{maxAttempts}): {proxyServer}");
            return proxy;
        }
        throw lastError ?? new InvalidOperationException("Không lấy được proxy.");
    }

    public Task<Dictionary<string, object>> GetCurrentProxyAsync()
    {
        if (config() is null) throw new InvalidOperationException("Chua cau hinh instance.");
        return KiotProxyClient.GetCurrentProxyAsync(Config.KiotProxyKey);
    }

    public static string BuildProxyServer(Dictionary<string, object> proxy, string selectedType) =>
        KiotProxyClient.BuildProxyServer(proxy, selectedType);

    public static string BuildFingerprint(Dictionary<string, object> proxy) =>
        KiotProxyClient.BuildFingerprint(proxy);

    public static bool IsProxyExpiredError(string msg) =>
        msg.Contains("KiotProxy current", StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("KiotProxy new", StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("PROXY_NOT_FOUND_BY_KEY", StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("Could not find the proxy being used by key", StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("hết hạn", StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("het han", StringComparison.OrdinalIgnoreCase);
}
