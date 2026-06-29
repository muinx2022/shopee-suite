namespace Shopee.Core.Coordination;

/// <summary>
/// Giá trị mặc định cho hạ tầng Hub của fleet này (điền sẵn để khỏi gõ tay trên từng máy).
/// Client+server dùng CHUNG <see cref="ApiToken"/>. Có thể đổi trong Settings nếu cần.
/// </summary>
public static class HubDefaults
{
    /// <summary>URL hub công khai (qua Cloudflare Tunnel).</summary>
    public const string BaseUrl = "https://api.schedra.net";

    /// <summary>Domain công khai của Hub (public hostname trên Cloudflare).</summary>
    public const string Domain = "api.schedra.net";

    /// <summary>Cổng local mà Hub (Kestrel) lắng nghe / cloudflared trỏ tới.</summary>
    public const int Port = 8088;

    /// <summary>API token dùng chung cả fleet. CỐ TÌNH ĐỂ TRỐNG trong source (KHÔNG commit secret).
    /// Token thật nhập 1 lần ở Settings → Bật Hub (máy Hub) và Settings → kết nối Hub (máy client);
    /// nó nằm trong file config cục bộ (hub-server.json / hub-client.json), KHÔNG lên git.</summary>
    public const string ApiToken = "";
}
