using Shopee.Core.Proxy;

namespace OpenMultiBraveLauncherV3;

/// <summary>
/// Phân giải proxy RIÊNG của một tài khoản BigSeller (KiotProxy key) → chuỗi proxy-server
/// (vd "http://host:port" / "socks5://host:port"). Ưu tiên /current (IP sticky ~30' → login và scrape
/// dùng chung 1 IP trong cửa sổ đó); /current lỗi (key chưa kích hoạt / hết hạn) → /new cấp IP mới.
/// Dùng cho CẢ luồng đăng nhập BigSeller (Suite) lẫn luồng scrape (engine) để 2 nơi cùng 1 IP/tk.
/// Trả null khi không có key (caller tự hiểu = đi IP máy như cũ).
/// </summary>
public static class BigSellerProxyResolver
{
    public static async Task<string?> ResolveServerAsync(
        string? key, string? region, string? proxyType, Action<string>? log = null)
    {
        key = key?.Trim();
        if (string.IsNullOrWhiteSpace(key))
            return null;

        var type = string.IsNullOrWhiteSpace(proxyType) ? "http" : proxyType.Trim();

        // ƯU TIÊN /current (IP sticky của key) + RETRY 2 lần: mọi lane của CÙNG 1 tk BigSeller phải ra
        // CÙNG 1 IP. Tránh fallback /new vì lỗi tạm thời (rate-limit) — /new sẽ XOAY IP của key → các lane
        // khác đang dùng IP cũ thành "cùng token / nhiều IP" → lại bị đá phiên. Chỉ /new khi /current hỏng
        // hẳn (key chưa kích hoạt lần nào). Login BigSeller (chạy trước scrape) thường đã kích hoạt key.
        Dictionary<string, object>? proxy = null;
        Exception? lastCurErr = null;
        for (var attempt = 1; attempt <= 3 && proxy is null; attempt++)
        {
            try { proxy = await KiotProxyClient.GetCurrentProxyAsync(key).ConfigureAwait(false); }
            catch (Exception ex)
            {
                lastCurErr = ex;
                if (attempt < 3) await Task.Delay(1500).ConfigureAwait(false);
            }
        }
        if (proxy is null)
        {
            try
            {
                log?.Invoke($"Proxy BigSeller: /current chưa có IP ({lastCurErr?.Message}) → cấp IP mới (/new).");
                proxy = await KiotProxyClient.GetNewProxyAsync(key, region, log).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log?.Invoke($"Proxy BigSeller: không lấy được IP cho key ({ex.Message}) — tạm đi IP máy.");
                return null;
            }
        }

        try
        {
            return KiotProxyClient.BuildProxyServer(proxy, type);
        }
        catch (Exception ex)
        {
            log?.Invoke($"Proxy BigSeller: endpoint không hợp lệ ({ex.Message}) — tạm đi IP máy.");
            return null;
        }
    }
}
