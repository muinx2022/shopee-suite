using Shopee.Core.Ai;

namespace Shopee.Core.Tests;

/// <summary>
/// Control-flow của <see cref="AiChat.ExecuteWithRetryAsync{T}"/> — đường retry DÙNG CHUNG cho viết tên,
/// mô tả và phân loại danh mục. Canh 3 luật thêm ở đợt hợp nhất 06/08: trần riêng cho lỗi tạm
/// (<c>maxAttemptsTransient</c>), tôn trọng <c>Retry-After</c> của server, và mặc định (0) không đổi
/// hành vi caller cũ. Mọi test inject <c>delay</c> no-op nên chạy tức thời, không cần mạng.
/// </summary>
public class AiChatRetryTests
{
    private static Func<int, CancellationToken, Task> NoDelay(List<int>? waits = null)
        => (ms, _) => { waits?.Add(ms); return Task.CompletedTask; };

    [Fact]
    public async Task LoiTam_ChiThuToiTranTransient_DuMaxAttemptsConLon()
    {
        var calls = 0;
        await Assert.ThrowsAsync<HttpRequestExceptionFake>(() => AiChat.ExecuteWithRetryAsync<int>(
            _ => { calls++; throw new HttpRequestExceptionFake(); },
            maxAttempts: 9, maxAttemptsTransient: 3, delay: NoDelay()));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task RateLimit429_KhongTinhVaoTranTransient_ChayDuMaxAttempts()
    {
        var calls = 0;
        await Assert.ThrowsAsync<AiHttpException>(() => AiChat.ExecuteWithRetryAsync<int>(
            _ => { calls++; throw new AiHttpException(429, "rate limit"); },
            maxAttempts: 4, maxAttemptsTransient: 2, delay: NoDelay()));
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task MacDinhTransientBangKhong_HanhViCu_ChayDuMaxAttempts()
    {
        var calls = 0;
        await Assert.ThrowsAsync<HttpRequestExceptionFake>(() => AiChat.ExecuteWithRetryAsync<int>(
            _ => { calls++; throw new HttpRequestExceptionFake(); },
            maxAttempts: 3, delay: NoDelay()));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task RetryAfterCuaServer_DuocDungThayBackoffTuTinh()
    {
        var waits = new List<int>();
        await Assert.ThrowsAsync<AiHttpException>(() => AiChat.ExecuteWithRetryAsync<int>(
            _ => throw new AiHttpException(429, "rate limit", retryAfterMs: 2500),
            maxAttempts: 3, rateLimitDelayMs: 15000, delay: NoDelay(waits)));
        // Cả 2 lần chờ đều theo Retry-After (2500ms), không phải 15000×lần.
        Assert.Equal([2500, 2500], waits);
    }

    [Fact]
    public async Task LoiVinhVien400_NemNgay_KhongRetry()
    {
        var calls = 0;
        await Assert.ThrowsAsync<AiHttpException>(() => AiChat.ExecuteWithRetryAsync<int>(
            _ => { calls++; throw new AiHttpException(400, "model sai"); },
            maxAttempts: 5, delay: NoDelay()));
        Assert.Equal(1, calls);
    }

    /// <summary>Exception "mạng" thuần cho test — không kéo System.Net.Http vào assert.</summary>
    private sealed class HttpRequestExceptionFake : Exception;
}
