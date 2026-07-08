namespace Shopee.Core.Proxy;

/// <summary>Kết quả lấy proxy từ kiotproxy.</summary>
public readonly record struct KiotResult(string? Proxy, string? Ip, long NextChangeAtMs, string? Error);

/// <summary>
/// Lấy proxy từ kiotproxy. <see cref="FetchNewAsync"/> gọi /proxies/new để xoay IP — chỉ thực
/// sự đổi IP khi đã qua mốc <c>nextRequestAt</c> của key (ProxyPool tự gate theo mốc này).
/// </summary>
public static class KiotProxyClient
{
    // Dùng chung 1 HttpClient: tạo mới mỗi lần gọi (mỗi account/mỗi lane) gây cạn socket/TIME_WAIT.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    public static async Task<KiotResult> FetchNewAsync(
        string key, CancellationToken ct = default, string proxyType = "http")
    {
        if (string.IsNullOrWhiteSpace(key))
            return new KiotResult(null, null, 0, "key rỗng");

        var url = $"https://api.kiotproxy.com/api/v1/proxies/new?key={Uri.EscapeDataString(key)}&region=random";
        return await TryFetchAsync(url, proxyType, ct);
    }

    /// <summary>Lấy proxy đang gán với key (không xoay) — dùng khi key chưa tới giờ đổi.</summary>
    public static async Task<KiotResult> FetchCurrentAsync(
        string key, CancellationToken ct = default, string proxyType = "http")
    {
        if (string.IsNullOrWhiteSpace(key))
            return new KiotResult(null, null, 0, "key rỗng");

        var url = $"https://api.kiotproxy.com/api/v1/proxies/current?key={Uri.EscapeDataString(key)}";
        return await TryFetchAsync(url, proxyType, ct);
    }

    private static async Task<KiotResult> TryFetchAsync(
        string url, string proxyType, CancellationToken ct)
    {
        string response;
        try
        {
            response = await Http.GetStringAsync(url, ct);
        }
        catch (Exception ex)
        {
            return new KiotResult(null, null, 0, ex.Message);
        }

        try
        {
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            if (root.TryGetProperty("success", out var successProp) && !successProp.GetBoolean())
            {
                var message = root.TryGetProperty("message", out var msg) ? msg.GetString() : null;
                return new KiotResult(null, null, 0, message ?? "kiotproxy success=false.");
            }

            var data = root.TryGetProperty("data", out var dataProp) ? dataProp : root;
            var nextChangeAt = data.TryGetProperty("nextRequestAt", out var nr) && nr.TryGetInt64(out var n) ? n : 0;
            var ip = data.TryGetProperty("realIpAddress", out var ipEl) ? ipEl.GetString() : null;

            var fieldName = string.Equals(proxyType, "socks5", StringComparison.OrdinalIgnoreCase) ? "socks5" : "http";
            if (data.TryGetProperty(fieldName, out var proxyProp))
            {
                var proxy = proxyProp.GetString();
                if (!string.IsNullOrWhiteSpace(proxy))
                {
                    ip ??= proxy.Split(':')[0];
                    return new KiotResult($"{fieldName}://{proxy}", ip, nextChangeAt, null);
                }
            }

            if (!data.TryGetProperty("host", out var hostProp))
                return new KiotResult(null, null, nextChangeAt, "kiotproxy không trả về host/proxy.");
            var host = hostProp.GetString();
            if (string.IsNullOrWhiteSpace(host))
                return new KiotResult(null, null, nextChangeAt, "kiotproxy trả về host rỗng.");

            var portField = string.Equals(fieldName, "socks5", StringComparison.OrdinalIgnoreCase) ? "socks5Port" : "httpPort";
            if (!data.TryGetProperty(portField, out var portProp))
                return new KiotResult(null, null, nextChangeAt, $"kiotproxy không trả về {portField}.");

            ip ??= host;
            return new KiotResult($"{fieldName}://{host}:{portProp.GetInt32()}", ip, nextChangeAt, null);
        }
        catch (JsonException)
        {
            return new KiotResult(null, null, 0, response);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  API Dictionary (đầy đủ field cho fingerprint) — gộp từ KiotProxyService của MultiBrave
    // ──────────────────────────────────────────────────────────────────────────────
    //  Khác API KiotResult ở trên: trả NGUYÊN VẸN data (host/http/socks5/realIpAddress…) để BuildFingerprint
    //  dựng "vân tay" proxy, và NÉM InvalidOperationException khi lỗi (caller MultiBrave dựa vào chuỗi
    //  "KiotProxy current"/"KiotProxy new" trong message để phân loại IsProxyExpiredError → fallback /new).
    //  GIỮ NGUYÊN thông điệp lỗi để không phá hợp đồng đó.

    /// <summary>Cấp IP MỚI cho <paramref name="key"/> (gọi /new). Ném nếu key rỗng / API lỗi / success=false.</summary>
    public static async Task<Dictionary<string, object>> GetNewProxyAsync(
        string? key, string? region, Action<string>? log = null)
    {
        key = key?.Trim();
        region = string.IsNullOrWhiteSpace(region) ? "random" : region.Trim();
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Can nhap KiotProxy key.");

        var url = $"https://api.kiotproxy.com/api/v1/proxies/new?key={Uri.EscapeDataString(key)}&region={Uri.EscapeDataString(region)}";
        log?.Invoke($"Lay proxy: {region}");

        using var response = await Http.GetAsync(url);
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"KiotProxy new {(int)response.StatusCode}: {ExtractError(json)}");

        return ParseProxyResponse(json);
    }

    /// <summary>Lấy IP HIỆN HÀNH (sticky ~30') của <paramref name="key"/> (gọi /current). Ném nếu key chưa
    /// kích hoạt / IP hết hạn → caller fallback sang <see cref="GetNewProxyAsync"/>.</summary>
    public static async Task<Dictionary<string, object>> GetCurrentProxyAsync(string? key)
    {
        key = key?.Trim();
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Can nhap KiotProxy key.");

        var url = $"https://api.kiotproxy.com/api/v1/proxies/current?key={Uri.EscapeDataString(key)}";
        using var response = await Http.GetAsync(url);
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
