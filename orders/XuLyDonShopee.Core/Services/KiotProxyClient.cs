using System.Text.Json;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Gọi API KiotProxy (kiotproxy.com) lấy proxy theo API key. Hỗ trợ NHIỀU key:
/// xoay vòng qua các key; key tới lượt bị lỗi thì thử key kế tiếp. Mọi lỗi
/// (mạng/timeout/JSON/key hỏng) đều nuốt và trả null để tầng gọi dùng IP máy.
/// Tài liệu: GET /api/v1/proxies/new?key=&amp;region= → data.http = "ip:port".
/// </summary>
public class KiotProxyClient : IKiotProxyClient
{
    /// <summary>Base URL mặc định của KiotProxy.</summary>
    public const string DefaultBaseUrl = "https://api.kiotproxy.com";

    /// <summary>Vùng mặc định (random = toàn hệ thống).</summary>
    public const string DefaultRegion = "random";

    private readonly object _lock = new();
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _region;
    private readonly List<string> _keys;
    private int _index;

    /// <param name="apiKeys">Danh sách API key của người dùng (mỗi key một dòng khi nhập).</param>
    /// <param name="region">Vùng proxy (bac/trung/nam/random). Mặc định random.</param>
    /// <param name="baseUrl">Base URL của dịch vụ (cấu hình được để test/đổi endpoint).</param>
    /// <param name="httpClient">HttpClient tùy chọn (phục vụ test).</param>
    public KiotProxyClient(IEnumerable<string> apiKeys, string region = DefaultRegion,
        string baseUrl = DefaultBaseUrl, HttpClient? httpClient = null)
    {
        _keys = KiotProxyKeyParser.Parse(apiKeys is null ? null : string.Join("\n", apiKeys));
        _region = string.IsNullOrWhiteSpace(region) ? DefaultRegion : region.Trim();
        _baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        _http = httpClient ?? new HttpClient();
    }

    /// <summary>Số key hợp lệ hiện có.</summary>
    public int KeyCount
    {
        get { lock (_lock) { return _keys.Count; } }
    }

    /// <summary>Lấy/đổi proxy mới (gọi <c>/proxies/new</c>, có gửi <c>region</c>).</summary>
    public Task<ProxyEntry?> GetNewProxyAsync(CancellationToken cancellationToken = default)
        => SelectAcrossKeysAsync("new", withRegion: true, cancellationToken);

    /// <summary>Lấy proxy đang gán với key (gọi <c>/proxies/current</c>, KHÔNG gửi <c>region</c>);
    /// chỉ trả proxy còn hạn (đã kiểm <c>expirationAt</c>).</summary>
    public Task<ProxyEntry?> GetCurrentProxyAsync(CancellationToken cancellationToken = default)
        => SelectAcrossKeysAsync("current", withRegion: false, cancellationToken);

    /// <summary>
    /// Xoay vòng qua các key (bắt đầu từ điểm hiện tại), gọi endpoint <paramref name="path"/> cho từng
    /// key; key nào cho proxy hợp lệ đầu tiên thì trả về. Không có key → null (không gọi HTTP).
    /// </summary>
    private async Task<ProxyEntry?> SelectAcrossKeysAsync(string path, bool withRegion, CancellationToken ct)
    {
        List<string> keys;
        int start;
        lock (_lock)
        {
            if (_keys.Count == 0)
            {
                return null; // không có key → dùng IP máy, không gọi HTTP
            }
            keys = _keys;
            start = _index;
            _index = (_index + 1) % _keys.Count;
        }

        // Thử tối đa toàn bộ key, bắt đầu từ điểm xoay vòng hiện tại.
        for (var i = 0; i < keys.Count; i++)
        {
            var key = keys[(start + i) % keys.Count];
            var proxy = await FetchAsync(key, path, withRegion, ct).ConfigureAwait(false);
            if (proxy != null)
            {
                return proxy;
            }
        }
        return null;
    }

    private async Task<ProxyEntry?> FetchAsync(string key, string path, bool withRegion, CancellationToken ct)
    {
        try
        {
            var url = $"{_baseUrl}/api/v1/proxies/{path}?key={Uri.EscapeDataString(key)}";
            if (withRegion)
            {
                url += $"&region={Uri.EscapeDataString(_region)}";
            }
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // /current: chỉ nhận proxy còn hạn. /new: proxy vừa cấp coi như còn hạn.
            return path == "current"
                ? ParseProxyIfAlive(body, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                : ParseResponse(body);
        }
        catch
        {
            return null; // nuốt lỗi → key kế tiếp / IP máy
        }
    }

    /// <summary>
    /// Trả proxy nếu JSON success và CHƯA hết hạn (<c>expirationAt &gt; nowUnixMs</c>). Không có
    /// <c>expirationAt</c> → coi như còn hạn. success=false/FAIL hoặc đã hết hạn → null.
    /// Tách khỏi đồng hồ hệ thống (nhận <paramref name="nowUnixMs"/>) để test được.
    /// </summary>
    public static ProxyEntry? ParseProxyIfAlive(string? json, long nowUnixMs)
    {
        var proxy = ParseResponse(json);
        if (proxy is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json!);
            var root = doc.RootElement;

            var dataEl = root;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data))
            {
                dataEl = data;
            }

            if (dataEl.ValueKind == JsonValueKind.Object &&
                dataEl.TryGetProperty("expirationAt", out var exp) &&
                exp.ValueKind == JsonValueKind.Number &&
                exp.TryGetInt64(out var expMs) &&
                expMs <= nowUnixMs)
            {
                return null; // đã hết hạn
            }
        }
        catch
        {
            // Không đọc được expirationAt → coi như còn hạn (đã có proxy hợp lệ từ ParseResponse).
        }

        return proxy;
    }

    /// <summary>
    /// Trích proxy "host:port" từ JSON KiotProxy. Ưu tiên data.http. Nếu
    /// success=false hoặc status="FAIL" → null. Lỗi parse → null.
    /// </summary>
    public static ProxyEntry? ParseResponse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        // Các tên trường có thể chứa chuỗi "host:port" (ưu tiên http theo tài liệu).
        string[] candidateKeys =
        {
            "http", "proxyHttp", "proxy", "proxyAddress", "address",
            "socks5", "proxySocks5", "https"
        };

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                // Thất bại rõ ràng theo tài liệu → null (HTTP status có thể vẫn 200).
                if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
                {
                    return null;
                }
                if (root.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String &&
                    string.Equals(st.GetString(), "FAIL", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            var value = FindProxyString(root, candidateKeys);
            if (value is null && root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("data", out var data))
            {
                value = FindProxyString(data, candidateKeys);
            }
            if (value is null)
            {
                return null;
            }

            var parsed = ProxyParser.Parse(value);
            return parsed.Valid.Count > 0 ? parsed.Valid[0] : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindProxyString(JsonElement element, string[] keys)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        foreach (var key in keys)
        {
            if (element.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                var s = prop.GetString();
                if (!string.IsNullOrWhiteSpace(s) && s!.Contains(':'))
                {
                    return s;
                }
            }
        }
        return null;
    }
}
