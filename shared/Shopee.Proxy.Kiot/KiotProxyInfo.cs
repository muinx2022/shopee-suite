namespace Shopee.Proxy.Kiot;

/// <summary>
/// Thông tin proxy đã bóc từ phần <c>data</c> của phản hồi API KiotProxy.
/// Các trường điển hình được map sẵn (thiếu field nào → null); NGOÀI RA toàn bộ
/// <c>data</c> thô được giữ nguyên trong <see cref="Raw"/> — phase 2b (MultiBrave)
/// cần dict thô này để dựng "vân tay" proxy nên KHÔNG được bỏ.
/// </summary>
public sealed record KiotProxyInfo(
    string? Host,
    int? HttpPort,
    int? Socks5Port,
    string? Http,
    string? Socks5,
    string? RealIpAddress,
    long? NextRequestAtMs,
    long? ExpirationAtMs,
    IReadOnlyDictionary<string, object?> Raw);
