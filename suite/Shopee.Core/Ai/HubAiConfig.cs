using Shopee.Core.Coordination;

namespace Shopee.Core.Ai;

/// <summary>
/// Lấy cấu hình AI TƯƠI từ Hub mỗi lần gọi AI. NGUỒN SỰ THẬT là Hub (trang "Cấu hình AI" trên Hub,
/// file <c>config/ai.json</c>); <see cref="AiConfigStore"/> local giờ CHỈ còn là CACHE/FALLBACK khi
/// offline. Client KHÔNG tự sửa/đẩy ai.json lên Hub nữa — tránh bản cache cũ đè bản Hub mới.
/// </summary>
public static class HubAiConfig
{
    /// <summary>TTL cache: trong khoảng này KHÔNG gọi Hub lại. Lý do: GenerateDescription gọi mỗi sản phẩm
    /// (và nhiều lane update chạy song song) → không đập Hub mỗi SP.</summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    /// <summary>Sau 1 lần fetch THẤT BẠI, chờ khoảng này mới thử Hub lại (trong lúc đó trả cache) — Hub sập
    /// giữa run thì các lần gọi AI kế tiếp không lặp lại chờ-lỗi mạng cho từng sản phẩm.</summary>
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(30);
    /// <summary>Trần chờ RIÊNG cho lần tải ai.json (file ~1KB): DownloadAsync dùng HttpClient timeout 5' (cỡ
    /// workbook) — tunnel treo mà chờ 5' cho mỗi lần gọi AI thì cả run đứng hình. Quá trần → dùng cache.</summary>
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly object _lock = new();
    private static DateTime _lastFetchUtc = DateTime.MinValue;     // thời điểm fetch Hub THÀNH CÔNG gần nhất
    private static DateTime _lastAttemptUtc = DateTime.MinValue;   // thời điểm THỬ fetch gần nhất (kể cả thất bại)

    /// <summary>
    /// Trả cấu hình AI: còn trong TTL kể từ lần fetch Hub thành công gần nhất → trả thẳng cache local (không
    /// gọi mạng); hết TTL + đã kết nối Hub → tải <c>config/ai.json</c>, lưu cache rồi trả bản Hub. Mọi lỗi
    /// (offline, 404, JSON hỏng, chưa kết nối Hub) → trả cache cũ (<see cref="AiConfigStore"/>.Current). Chỉ
    /// <see cref="OperationCanceledException"/> (người dùng huỷ) mới được propagate.
    /// </summary>
    public static async Task<AiConfig> GetAsync(CancellationToken ct = default)
    {
        // Còn trong TTL kể từ lần fetch thành công / còn trong backoff sau lần fetch thất bại → trả cache,
        // KHÔNG gọi mạng (backoff để Hub sập giữa run không bắt từng lần gọi AI chờ-lỗi mạng lại từ đầu).
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (now - _lastFetchUtc < Ttl) return AiConfigStore.Shared.Current;
            if (now - _lastAttemptUtc < FailureBackoff) return AiConfigStore.Shared.Current;
            _lastAttemptUtc = now;   // đánh dấu TRƯỚC khi gọi mạng — các lane song song khỏi cùng ùa vào fetch
        }

        if (CoordinationRuntime.Client is not { } client)
            return AiConfigStore.Shared.Current;   // chưa kết nối Hub → dùng cache/fallback

        try
        {
            // Trần 10s riêng cho file bé này (HttpClient bên dưới timeout 5' cỡ workbook — không hợp ở đây).
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(FetchTimeout);
            var bytes = await client.DownloadAsync("config/ai.json", timeoutCts.Token).ConfigureAwait(false);
            if (bytes is not null && JsonSerializer.Deserialize<AiConfig>(NoBom(bytes), JsonOpts) is { } cfg)
            {
                AiConfigStore.Shared.Save(cfg);   // lưu cache cho lần offline sau
                lock (_lock) _lastFetchUtc = DateTime.UtcNow;
                return cfg;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }   // người dùng huỷ → KHÔNG nuốt
        catch { /* offline / treo quá 10s / 404 / JSON hỏng → rơi về cache cũ bên dưới */ }

        return AiConfigStore.Shared.Current;
    }

    /// <summary>Bỏ BOM UTF-8 (EF BB BF) ở đầu nếu có: store ghi ai.json bằng Encoding.UTF8 (CÓ BOM) → deserialize
    /// thẳng byte[] sẽ ném "'0xEF' is an invalid start of a value". Đối xứng với <c>HubConfigSync.NoBom</c>.</summary>
    private static ReadOnlySpan<byte> NoBom(byte[] b) =>
        b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? b.AsSpan(3) : b.AsSpan();
}
