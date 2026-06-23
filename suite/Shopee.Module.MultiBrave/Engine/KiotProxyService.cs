using System.Text.Json;

namespace OpenMultiBraveLauncherV3;

internal static class KiotProxyService
{
    private const string NewUrl = "https://api.kiotproxy.com/api/v1/proxies/new";
    private const string CurrentUrl = "https://api.kiotproxy.com/api/v1/proxies/current";

    public static Task<Dictionary<string, object>> GetNewProxyAsync(InstanceConfig config, Action<string>? log = null) =>
        GetNewProxyAsync(config.KiotProxyKey, config.Region, log);

    public static Task<Dictionary<string, object>> GetCurrentProxyAsync(InstanceConfig config) =>
        GetCurrentProxyAsync(config.KiotProxyKey);

    /// <summary>Cấp IP MỚI cho <paramref name="key"/> (gọi /new). Dùng được cho cả proxy Shopee (qua
    /// InstanceConfig) lẫn proxy RIÊNG của tk BigSeller (truyền key trực tiếp).</summary>
    public static async Task<Dictionary<string, object>> GetNewProxyAsync(string? key, string? region, Action<string>? log = null)
    {
        key = key?.Trim();
        region = string.IsNullOrWhiteSpace(region) ? "random" : region.Trim();
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Can nhap KiotProxy key.");

        var url = $"{NewUrl}?key={Uri.EscapeDataString(key)}&region={Uri.EscapeDataString(region)}";
        log?.Invoke($"Lay proxy: {region}");

        using var response = await AppServices.Http.GetAsync(url);
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"KiotProxy new {(int)response.StatusCode}: {ExtractError(json)}");

        return ParseProxyResponse(json);
    }

    /// <summary>Lấy IP HIỆN HÀNH (sticky ~30') của <paramref name="key"/> (gọi /current). Ném nếu key chưa
    /// kích hoạt / IP hết hạn → caller fallback sang /new.</summary>
    public static async Task<Dictionary<string, object>> GetCurrentProxyAsync(string? key)
    {
        key = key?.Trim();
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Can nhap KiotProxy key.");

        var url = $"{CurrentUrl}?key={Uri.EscapeDataString(key)}";
        using var response = await AppServices.Http.GetAsync(url);
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"KiotProxy current {(int)response.StatusCode}: {ExtractError(json)}");

        return ParseProxyResponse(json);
    }

    public static string BuildProxyServer(Dictionary<string, object> proxy, string selectedType)
    {
        var type = (selectedType ?? "http").Trim().ToLowerInvariant();
        string? value = type == "socks5"
            ? proxy.TryGetValue("socks5", out var s) ? s?.ToString() : null
            : proxy.TryGetValue("http", out var h) ? h?.ToString() : null;

        value ??= proxy.TryGetValue("http", out var hf) ? hf?.ToString() : null;
        value ??= proxy.TryGetValue("socks5", out var sf) ? sf?.ToString() : null;

        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("KiotProxy khong tra ve endpoint.");

        return value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase)
            ? value
            : $"{(type == "socks5" ? "socks5" : "http")}://{value}";
    }

    public static string BuildFingerprint(Dictionary<string, object> proxy)
    {
        static string Get(Dictionary<string, object> p, string k) =>
            p.TryGetValue(k, out var v) ? v?.ToString() ?? "" : "";
        return string.Join("|", Get(proxy, "realIpAddress"), Get(proxy, "host"),
            Get(proxy, "http"), Get(proxy, "socks5"));
    }

    private static Dictionary<string, object> ParseProxyResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("success", out var s) || !s.GetBoolean())
        {
            var msg = root.TryGetProperty("message", out var m) ? m.GetString() : null;
            var err = root.TryGetProperty("error", out var e) ? e.GetString() : null;
            throw new InvalidOperationException(msg ?? err ?? "KiotProxy tra ve loi.");
        }

        return ParseProxyData(root.GetProperty("data"));
    }

    private static Dictionary<string, object> ParseProxyData(JsonElement data)
    {
        var proxy = new Dictionary<string, object>();
        foreach (var prop in data.EnumerateObject())
        {
            proxy[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                JsonValueKind.Number => prop.Value.TryGetInt64(out var i) ? i : prop.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => prop.Value.ToString() ?? string.Empty,
            };
        }
        return proxy;
    }

    private static string ExtractError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var parts = new List<string>();
            if (root.TryGetProperty("message", out var m) && !string.IsNullOrWhiteSpace(m.GetString()))
                parts.Add(m.GetString()!);
            if (root.TryGetProperty("error", out var e) && !string.IsNullOrWhiteSpace(e.GetString()))
                parts.Add(e.GetString()!);
            if (parts.Count > 0)
                return string.Join(" | ", parts);
        }
        catch { }

        return string.IsNullOrWhiteSpace(json) ? "Loi khong xac dinh." : json.Trim();
    }
}
