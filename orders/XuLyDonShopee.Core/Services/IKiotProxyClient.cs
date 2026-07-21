using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Client gọi dịch vụ KiotProxy để lấy một proxy mới (dùng khi danh sách proxy trống).
/// </summary>
public interface IKiotProxyClient
{
    /// <summary>
    /// Lấy một proxy mới (gọi <c>/proxies/new</c>). Trả <c>null</c> nếu lỗi hoặc không có proxy
    /// (khi đó tầng gọi sẽ dùng IP máy).
    /// </summary>
    Task<ProxyEntry?> GetNewProxyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy proxy hiện đang gán với key (gọi <c>/proxies/current</c>), chỉ trả về nếu proxy
    /// <b>còn hạn</b> (theo <c>expirationAt</c>). Trả <c>null</c> nếu key chưa có proxy gán
    /// (FAIL <c>PROXY_NOT_FOUND_BY_KEY</c>), proxy đã hết hạn, hoặc lỗi.
    /// </summary>
    Task<ProxyEntry?> GetCurrentProxyAsync(CancellationToken cancellationToken = default);
}
