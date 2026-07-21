using Microsoft.Playwright;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Chuyển một <see cref="ProxyEntry"/> của app sang cấu hình proxy của Playwright.
/// Hàm thuần (không phụ thuộc trạng thái/IO) nên test được dễ dàng.
/// </summary>
public static class PlaywrightProxyMapper
{
    /// <summary>
    /// Trả về <see cref="Proxy"/> cho Playwright, hoặc <c>null</c> nếu <paramref name="entry"/>
    /// là <c>null</c> (nghĩa là không dùng proxy — đi IP máy).
    /// </summary>
    /// <remarks>
    /// LƯU Ý: Chromium <b>không hỗ trợ xác thực user/pass cho SOCKS5</b>. Với proxy SOCKS5
    /// có auth, phần Username/Password vẫn được gán nhưng trình duyệt sẽ bỏ qua khi kết nối.
    /// Đa số proxy dùng trong dự án là HTTP nên chấp nhận hạn chế này ở bước hiện tại.
    /// </remarks>
    public static Proxy? ToPlaywrightProxy(ProxyEntry? entry)
    {
        if (entry is null)
        {
            return null;
        }

        var scheme = entry.Type == ProxyType.Socks5 ? "socks5" : "http";
        var proxy = new Proxy
        {
            Server = $"{scheme}://{entry.Host}:{entry.Port}"
        };

        if (!string.IsNullOrWhiteSpace(entry.Username))
        {
            proxy.Username = entry.Username;
            proxy.Password = entry.Password;
        }

        return proxy;
    }
}
