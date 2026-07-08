namespace Shopee.Core.Infrastructure;

/// <summary>
/// HttpClient dùng chung (tái sử dụng handler → không cạn socket/TIME_WAIT). Gộp về Core từ 2 bản
/// nhân đôi byte-identical (MultiBrave/UpdateProduct).
/// <list type="bullet">
///   <item><see cref="Http"/>: client mặc định (qua proxy hệ thống nếu có).</item>
///   <item><see cref="DirectHttp"/>: KHÔNG proxy + tự giải nén, timeout 15s — dùng cho CDP loopback
///   (127.0.0.1) để proxy hệ thống không chặn/đổi hướng gọi nội bộ.</item>
/// </list>
/// </summary>
public static class AppServices
{
    public static readonly HttpClient Http = new();

    public static readonly HttpClient DirectHttp = new(new HttpClientHandler
    {
        UseProxy = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };
}
