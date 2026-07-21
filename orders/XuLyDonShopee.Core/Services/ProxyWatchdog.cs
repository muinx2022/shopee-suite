using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Canh proxy đang gán cho phiên (nguồn KiotProxy): định kỳ kiểm proxy hiện tại còn sống không, nếu CHẾT
/// thì lấy proxy thay thế để tầng gọi RELAUNCH trình duyệt. Hàm thuần logic (nhận client + checker qua tham
/// số) nên test được bằng stub.
/// </summary>
public static class ProxyWatchdog
{
    /// <summary>
    /// Kiểm proxy hiện tại còn sống; nếu CHẾT (xác nhận 2 lần, cách nhau <paramref name="recheckDelayMs"/>)
    /// thì lấy proxy thay thế qua <see cref="ProxySelector.SelectKiotProxyAsync"/> (ưu tiên <c>/current</c>
    /// sticky). Trả proxy MỚI để relaunch, hoặc <c>null</c> khi: <paramref name="current"/> == null (IP máy) /
    /// còn sống / vừa hồi phục ở lần kiểm 2 (false-negative) / không lấy được proxy nào / proxy thay thế TRÙNG
    /// endpoint cũ (chưa xoay xong — chờ chu kỳ sau).
    /// </summary>
    public static async Task<ProxyEntry?> TryGetReplacementAsync(
        IKiotProxyClient kiot, IProxyHealthChecker checker, ProxyEntry? current,
        int recheckDelayMs, CancellationToken ct = default)
    {
        if (current is null) return null;                                                   // IP máy — không có gì để canh
        if (await checker.IsAliveAsync(current, ct).ConfigureAwait(false)) return null;      // còn sống
        if (recheckDelayMs > 0) await Task.Delay(recheckDelayMs, ct).ConfigureAwait(false);
        if (await checker.IsAliveAsync(current, ct).ConfigureAwait(false)) return null;      // hồi phục (false-negative)
        var repl = await ProxySelector.SelectKiotProxyAsync(kiot, checker, ct).ConfigureAwait(false);
        if (repl is null) return null;                                                       // API không cấp được proxy sống
        return ProxyEndpointsEqual(repl, current) ? null : repl;                             // trùng endpoint cũ → chờ chu kỳ sau
    }

    /// <summary>So khớp endpoint proxy theo Host (không phân biệt hoa/thường) + Port.</summary>
    public static bool ProxyEndpointsEqual(ProxyEntry a, ProxyEntry b)
        => string.Equals(a.Host, b.Host, System.StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;
}
