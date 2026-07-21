using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Điều phối việc chọn proxy KiotProxy còn sống (kết hợp cả hai cách kiểm). Hàm thuần logic (nhận
/// client + checker qua tham số) nên test được bằng stub.
/// </summary>
public static class ProxySelector
{
    /// <summary>Lấy proxy KiotProxy còn sống (kết hợp cả hai cách kiểm):
    /// (1) API — ưu tiên proxy hiện tại còn hạn (current, đã kiểm expirationAt); nếu key chưa có proxy/hết hạn
    /// (FAIL PROXY_NOT_FOUND_BY_KEY) thì xin proxy mới (new). (2) Thử kết nối thật qua proxy.
    /// Cả hai đạt → trả proxy; không lấy được proxy nào hoặc kết nối chết → null (tầng gọi dùng IP máy).</summary>
    public static async Task<ProxyEntry?> SelectKiotProxyAsync(
        IKiotProxyClient? kiot, IProxyHealthChecker checker, CancellationToken cancellationToken = default)
    {
        if (kiot is null) return null;                            // không có key KiotProxy → IP máy
        // GIỮ IP LÂU NHẤT: luôn ưu tiên proxy hiện tại (sticky, key đang gán) — KHÔNG ép xin proxy mới.
        // Chỉ gọi /new khi current == null (key chưa có proxy/hết hạn) để tránh xoay IP liên tục.
        var proxy = await kiot.GetCurrentProxyAsync(cancellationToken).ConfigureAwait(false)
                    ?? await kiot.GetNewProxyAsync(cancellationToken).ConfigureAwait(false);
        if (proxy is null) return null;                           // API không cấp được proxy nào → IP máy
        return await checker.IsAliveAsync(proxy, cancellationToken).ConfigureAwait(false)
            ? proxy
            : null;                                               // kết nối chết → IP máy
    }
}
