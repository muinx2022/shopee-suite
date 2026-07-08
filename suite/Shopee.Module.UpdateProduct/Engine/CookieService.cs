using System.Net.WebSockets;
using System.Text.Json;

namespace UpdateProduct;

internal sealed class CookieService(CdpClient cdpClient)
{
    public async Task<List<Dictionary<string, object?>>> GetShopeeAndBigSellerCookiesAsync()
    {
        // Đọc ở cấp BROWSER (Storage.getCookies) — trả TOÀN BỘ cookie store, KHÔNG phụ thuộc tab
        // đang mở. Trước đây đọc page-level (Network.getAllCookies) lúc tab còn about:blank/newtab
        // → không thấy cookie .shopee.vn/.bigseller lúc nào cũng "rỗng" → tưởng chưa đăng nhập →
        // import token chết từ file đè token sống → login lại + captcha mỗi lần chạy.
        var wsUrl = await cdpClient.GetBrowserWebSocketUrlAsync().ConfigureAwait(false);
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(wsUrl), CancellationToken.None);

        var result = await CdpClient.SendAsync(socket, 1, "Storage.getCookies", null).ConfigureAwait(false);
        if (!result.TryGetProperty("cookies", out var cookiesEl) || cookiesEl.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<Dictionary<string, object?>>();
        foreach (var cookie in cookiesEl.EnumerateArray())
        {
            var domain = cookie.TryGetProperty("domain", out var dp) ? (dp.GetString() ?? "") : "";
            var lower = domain.ToLowerInvariant();
            if (!lower.Contains("shopee") && !lower.Contains("bigseller"))
                continue;

            var map = new Dictionary<string, object?>();
            foreach (var p in cookie.EnumerateObject())
            {
                map[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString(),
                    JsonValueKind.Number => p.Value.TryGetInt64(out var i) ? i : p.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => p.Value.ToString(),
                };
            }
            list.Add(map);
        }
        return list;
    }
}
