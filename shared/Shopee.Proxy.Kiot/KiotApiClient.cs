namespace Shopee.Proxy.Kiot;

/// <summary>
/// Client HTTP dùng chung cho API KiotProxy (kiotproxy.com). Inject được cả
/// <see cref="HttpClient"/> lẫn <c>baseUrl</c> để test và đổi endpoint. KHÔNG BAO GIỜ
/// ném vì lỗi HTTP/parse/timeout — trả <see cref="KiotApiResult"/> với <c>Success=false</c>.
/// Tài liệu: GET <c>/api/v1/proxies/new?key=&amp;region=</c> và
/// <c>/api/v1/proxies/current?key=</c>.
/// </summary>
public sealed class KiotApiClient
{
    /// <summary>Base URL mặc định của KiotProxy.</summary>
    public const string DefaultBaseUrl = "https://api.kiotproxy.com";

    /// <summary>Vùng mặc định (random = toàn hệ thống).</summary>
    public const string DefaultRegion = "random";

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    /// <param name="http">HttpClient dùng để gọi (inject được để test / tái dùng socket).</param>
    /// <param name="baseUrl">Base URL dịch vụ; rỗng → <see cref="DefaultBaseUrl"/>.</param>
    public KiotApiClient(HttpClient http, string? baseUrl = null)
    {
        _http = http ?? new HttpClient();
        _baseUrl = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl).TrimEnd('/');
    }

    /// <summary>Cấp/đổi IP mới (gọi <c>/proxies/new</c>, CÓ gửi <c>region</c>; rỗng → random).</summary>
    public Task<KiotApiResult> GetNewAsync(string key, string? region, CancellationToken ct = default)
    {
        var r = string.IsNullOrWhiteSpace(region) ? DefaultRegion : region.Trim();
        var url = $"{_baseUrl}/api/v1/proxies/new?key={Uri.EscapeDataString(key ?? string.Empty)}" +
                  $"&region={Uri.EscapeDataString(r)}";
        return SendAsync(url, ct);
    }

    /// <summary>Lấy IP đang gán với key (gọi <c>/proxies/current</c>, KHÔNG gửi <c>region</c>).</summary>
    public Task<KiotApiResult> GetCurrentAsync(string key, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/v1/proxies/current?key={Uri.EscapeDataString(key ?? string.Empty)}";
        return SendAsync(url, ct);
    }

    private async Task<KiotApiResult> SendAsync(string url, CancellationToken ct)
    {
        int status;
        string body;
        try
        {
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            status = (int)response.StatusCode;
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Mạng/timeout/hủy → không ném, coi như thất bại (chưa có phản hồi HTTP).
            return new KiotApiResult(false, ex.Message, null, null);
        }

        if (status < 200 || status >= 300)
        {
            return new KiotApiResult(false, KiotApiParser.ExtractError(body), null, status);
        }

        return KiotApiParser.ParseBody(body, status);
    }
}
