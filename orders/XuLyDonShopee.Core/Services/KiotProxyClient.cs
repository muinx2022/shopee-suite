using Shopee.Proxy.Kiot;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Gọi API KiotProxy (kiotproxy.com) lấy proxy theo API key. Hỗ trợ NHIỀU key:
/// xoay vòng qua các key; key tới lượt bị lỗi thì thử key kế tiếp. Mọi lỗi
/// (mạng/timeout/JSON/key hỏng) đều nuốt và trả null để tầng gọi dùng IP máy.
/// Phần gọi HTTP + bóc JSON đã ủy quyền cho <see cref="KiotApiClient"/> (project chung
/// <c>Shopee.Proxy.Kiot</c>); lớp này là adapter: xoay key, map ra <see cref="ProxyEntry"/>
/// và kiểm hết hạn của <c>/current</c>.
/// </summary>
public class KiotProxyClient : IKiotProxyClient
{
    /// <summary>Base URL mặc định của KiotProxy.</summary>
    public const string DefaultBaseUrl = KiotApiClient.DefaultBaseUrl;

    /// <summary>Vùng mặc định (random = toàn hệ thống).</summary>
    public const string DefaultRegion = KiotApiClient.DefaultRegion;

    // Tên các trường trong "data" có thể chứa chuỗi "host:port" (ưu tiên http theo tài liệu).
    private static readonly string[] CandidateKeys =
    {
        "http", "proxyHttp", "proxy", "proxyAddress", "address",
        "socks5", "proxySocks5", "https"
    };

    private readonly object _lock = new();
    private readonly KiotApiClient _api;
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
        _api = new KiotApiClient(httpClient ?? new HttpClient(), baseUrl ?? DefaultBaseUrl);
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
        // Gọi client chung (client này KHÔNG ném; lỗi mạng/HTTP/JSON → Success=false).
        var result = withRegion
            ? await _api.GetNewAsync(key, _region, ct).ConfigureAwait(false)
            : await _api.GetCurrentAsync(key, ct).ConfigureAwait(false);

        var proxy = MapToProxyEntry(result);
        if (proxy is null)
        {
            return null; // lỗi / success=false / FAIL / không có proxy → key kế tiếp / IP máy
        }

        // /current: chỉ nhận proxy còn hạn (expirationAt > now). /new: proxy vừa cấp coi như còn hạn.
        if (path == "current" &&
            result.Data is { ExpirationAtMs: { } expMs } &&
            expMs <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            return null; // đã hết hạn
        }

        return proxy;
    }

    /// <summary>
    /// Trả proxy nếu JSON success và CHƯA hết hạn (<c>expirationAt &gt; nowUnixMs</c>). Không có
    /// <c>expirationAt</c> → coi như còn hạn. success=false/FAIL hoặc đã hết hạn → null.
    /// Tách khỏi đồng hồ hệ thống (nhận <paramref name="nowUnixMs"/>) để test được.
    /// </summary>
    public static ProxyEntry? ParseProxyIfAlive(string? json, long nowUnixMs)
    {
        var result = KiotApiParser.ParseBody(json);
        var proxy = MapToProxyEntry(result);
        if (proxy is null)
        {
            return null;
        }

        // Không đọc được expirationAt → coi như còn hạn (đã có proxy hợp lệ).
        if (result.Data is { ExpirationAtMs: { } expMs } && expMs <= nowUnixMs)
        {
            return null; // đã hết hạn
        }

        return proxy;
    }

    /// <summary>
    /// Trích proxy "host:port" từ JSON KiotProxy. Ưu tiên data.http. Nếu
    /// success=false hoặc status="FAIL" → null. Lỗi parse → null.
    /// </summary>
    public static ProxyEntry? ParseResponse(string? json)
        => MapToProxyEntry(KiotApiParser.ParseBody(json));

    /// <summary>
    /// Map kết quả API chung sang <see cref="ProxyEntry"/>: thất bại/không có data → null;
    /// dò các trường ứng viên trong <c>data</c> thô để lấy chuỗi "host:port", rồi
    /// <see cref="ProxyParser.Parse"/>.
    /// </summary>
    private static ProxyEntry? MapToProxyEntry(KiotApiResult result)
    {
        if (!result.Success || result.Data is null)
        {
            return null;
        }

        var value = FindProxyString(result.Data.Raw, CandidateKeys);
        if (value is null)
        {
            return null;
        }

        var parsed = ProxyParser.Parse(value);
        return parsed.Valid.Count > 0 ? parsed.Valid[0] : null;
    }

    private static string? FindProxyString(IReadOnlyDictionary<string, object?> raw, string[] keys)
    {
        foreach (var key in keys)
        {
            if (raw.TryGetValue(key, out var v) && v is string s &&
                !string.IsNullOrWhiteSpace(s) && s.Contains(':'))
            {
                return s;
            }
        }
        return null;
    }
}
