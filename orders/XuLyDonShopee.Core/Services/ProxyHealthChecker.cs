using System.Net;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Kiểm tra một proxy có còn <b>kết nối được</b> hay không bằng cách thử tải một URL nhỏ qua proxy.
/// </summary>
public interface IProxyHealthChecker
{
    /// <summary>Trả true nếu tải được URL kiểm tra qua <paramref name="proxy"/> trong thời gian cho phép.</summary>
    Task<bool> IsAliveAsync(ProxyEntry proxy, CancellationToken cancellationToken = default);
}

/// <summary>
/// Thử kết nối thật qua proxy tới một endpoint trả về IP (api.ipify.org). Mọi lỗi (timeout, từ chối
/// kết nối, proxy chết) đều nuốt và trả <c>false</c> để tầng gọi rơi về IP máy.
/// </summary>
/// <remarks>
/// LƯU Ý: <see cref="WebProxy"/> có thể KHÔNG hỗ trợ xác thực user/pass cho SOCKS5 (giống hạn chế đã
/// biết của Chromium). Đa số proxy dùng trong dự án là HTTP nên chấp nhận hạn chế này ở bước hiện tại.
/// </remarks>
public class ProxyHealthChecker : IProxyHealthChecker
{
    /// <summary>URL nhỏ để thử kết nối qua proxy (trả về IP dạng text).</summary>
    private const string TestUrl = "https://api.ipify.org";

    /// <summary>Thời gian chờ tối đa cho một lần kiểm tra (ms).</summary>
    private const int TimeoutMs = 8000;

    /// <summary>Địa chỉ proxy dạng scheme://host:port (hàm thuần, test được).</summary>
    public static string ToProxyAddress(ProxyEntry p)
        => p.Type == ProxyType.Socks5 ? $"socks5://{p.Host}:{p.Port}" : $"http://{p.Host}:{p.Port}";

    public async Task<bool> IsAliveAsync(ProxyEntry proxy, CancellationToken cancellationToken = default)
    {
        try
        {
            var wp = new WebProxy(ToProxyAddress(proxy));
            if (!string.IsNullOrWhiteSpace(proxy.Username))
            {
                wp.Credentials = new NetworkCredential(proxy.Username, proxy.Password);
            }

            using var handler = new HttpClientHandler { Proxy = wp, UseProxy = true };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(TimeoutMs) };
            using var res = await http.GetAsync(TestUrl, cancellationToken).ConfigureAwait(false);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false; // proxy chết / không kết nối được → IP máy
        }
    }
}
