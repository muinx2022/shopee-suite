using Shopee.Core.BigSeller;

namespace UpdateProduct;

/// <summary>
/// Ghi NGƯỢC muc_token (server vừa xoay) từ browser ra file ĐỊNH KỲ trong lúc chạy — throttle 90s, GIỮ
/// mốc thời gian lần ghi gần nhất. Đây là điều Scrape làm sau MỖI link mà Update/Import trước đây THIẾU
/// (chỉ export lúc đầu + lúc đóng) → file thiu giữa chừng → lane khác / lần chạy sau import token CŨ →
/// BigSeller đá phiên ("log in first"). Chỉ lane export cookie mới ghi (tránh rotation-war). Dùng chung cho
/// <c>BigSellerProductUpdateRunner</c> + <c>BigSellerImportToStoreRunner</c> (2 bản byte-identical trước đây).
/// </summary>
internal sealed class BigSellerTokenWriteBack
{
    private long _lastTick;   // throttle ghi-ngược muc_token định kỳ trong lúc chạy

    public async Task MaybeWriteBackAsync(
        bool exportCookie, int debugPort, string? cookieFile, Action<string> log, CancellationToken ct)
    {
        if (!exportCookie || string.IsNullOrWhiteSpace(cookieFile))
            return;
        var now = Environment.TickCount64;
        if (now - _lastTick < 90_000)
            return;
        _lastTick = now;
        try
        {
            await BigSellerCookieEngine.WriteBackLiveTokenAsync(debugPort, cookieFile!, log, ct).ConfigureAwait(false);
        }
        catch { /* best-effort */ }
    }
}
