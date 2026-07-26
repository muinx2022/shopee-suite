namespace Shopee.Core.Coordination;

/// <summary>
/// Lấy cấu hình DÙNG CHUNG của module Đơn hàng (khối GSheet) từ Hub — NGUỒN SỰ THẬT là Hub (trang
/// "Đơn hàng (GSheet)" trên Hub, file <c>config/orders.json</c>). Khuôn sao y <see cref="Ai.HubAiConfig"/>:
/// TTL 60s, backoff 30s sau lỗi, trần chờ riêng 10s cho một lượt tải.
/// <para>
/// KHÁC HubAiConfig ở chỗ KHÔNG có store cache trên đĩa: nguồn "cache" của cấu hình này chính là CSDL local
/// của module Đơn hàng (bảng <c>settings</c>). Vì vậy khi chưa kết nối / lỗi / hub chưa có route thì trả
/// <c>null</c> = "KHÔNG BIẾT" — caller phải GIỮ NGUYÊN cấu hình local, tuyệt đối không coi là "hub rỗng".
/// </para>
/// </summary>
public static class HubOrdersConfig
{
    /// <summary>TTL cache: trong khoảng này KHÔNG gọi Hub lại (nhịp gọi tới từ poller fleet 12s + timer 60s
    /// của OrdersModuleHost → TTL là cái chặn thật sự số request).</summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    /// <summary>Sau 1 lần fetch THẤT BẠI, chờ khoảng này mới thử Hub lại (Hub sập thì các nhịp kế tiếp không
    /// lặp lại chờ-lỗi-mạng).</summary>
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(30);
    /// <summary>Trần chờ RIÊNG cho một lượt gọi (payload 2 chuỗi): _http của HubClient đã là 8s, nhưng bọc
    /// thêm ở đây để đồng nhất khuôn với HubAiConfig và không phụ thuộc timeout của tầng dưới.</summary>
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);

    private static readonly object _lock = new();
    private static OrdersSharedConfig? _cached;                    // bản Hub gần nhất lấy được (null = chưa từng)
    private static DateTime _lastFetchUtc = DateTime.MinValue;     // thời điểm fetch Hub THÀNH CÔNG gần nhất
    private static DateTime _lastAttemptUtc = DateTime.MinValue;   // thời điểm THỬ fetch gần nhất (kể cả thất bại)

    /// <summary>
    /// Trả cấu hình dùng chung của Hub: còn trong TTL (hoặc còn trong backoff sau lỗi) → trả bản đã lấy được
    /// gần nhất, KHÔNG gọi mạng; ngược lại gọi Hub, lưu lại rồi trả bản mới. Chưa kết nối Hub / offline /
    /// hub cũ chưa có route / JSON hỏng → trả <c>null</c> ("không biết" → caller giữ nguyên bản local). Chỉ
    /// <see cref="OperationCanceledException"/> do người gọi huỷ mới được propagate.
    /// </summary>
    public static async Task<OrdersSharedConfig?> GetAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (now - _lastFetchUtc < Ttl) return _cached;
            if (now - _lastAttemptUtc < FailureBackoff) return _cached;
            _lastAttemptUtc = now;   // đánh dấu TRƯỚC khi gọi mạng — nhiều nhịp song song khỏi cùng ùa vào fetch
        }

        if (CoordinationRuntime.Client is not { } client)
            return null;   // chưa kết nối Hub → "không biết", KHÔNG đụng cấu hình local

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(FetchTimeout);
            var cfg = await client.GetOrdersConfigAsync(timeoutCts.Token).ConfigureAwait(false);
            if (cfg is not null)
            {
                lock (_lock) { _cached = cfg; _lastFetchUtc = DateTime.UtcNow; }
                return cfg;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }   // người dùng huỷ → KHÔNG nuốt
        catch { /* offline / treo quá 10s / 404 (hub cũ) / JSON hỏng → coi như "không biết" */ }

        return null;
    }

    /// <summary>Quên bản đã lấy + mốc TTL để lượt gọi kế tiếp hỏi Hub NGAY — dùng sau khi máy này vừa ĐẨY cấu
    /// hình mới lên Hub (khỏi phải chờ hết TTL mới thấy bản vừa đẩy).</summary>
    public static void Invalidate()
    {
        lock (_lock)
        {
            _cached = null;
            _lastFetchUtc = DateTime.MinValue;
            _lastAttemptUtc = DateTime.MinValue;
        }
    }
}
