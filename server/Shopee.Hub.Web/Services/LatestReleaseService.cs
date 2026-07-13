using System.Text.Json;

namespace Shopee.Hub.Web.Services;

/// <summary>
/// Đọc PHIÊN BẢN release MỚI NHẤT của app desktop từ GitHub Releases (repo PUBLIC → không cần token) để trang
/// /machines biết "bản mới nhất là gì" mà đánh dấu máy nào còn cũ + gợi số máy cần update. Cache kết quả để
/// KHÔNG đập GitHub mỗi lần render (trang tự vẽ lại mỗi 2s theo nhịp fleet): OK giữ 5 phút, THẤT BẠI giữ 1 phút.
/// Mọi lỗi (mạng/JSON/timeout) → null → UI hiện "?". Đăng ký singleton trong Program.cs.
/// </summary>
public sealed class LatestReleaseService
{
    private const string LatestUrl = "https://api.github.com/repos/muinx2022/shopee-suite/releases/latest";

    // GitHub API TỪ CHỐI request không có User-Agent → bắt buộc đặt. Timeout ngắn để không treo render.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly object _lock = new();
    private static readonly TimeSpan OkTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailTtl = TimeSpan.FromMinutes(1);
    private string? _cached;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;
    private TimeSpan _cachedTtl = FailTtl;

    static LatestReleaseService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("shopee-hub-web");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>Phiên bản mới nhất (đã bỏ tiền tố 'v'/'V' của tag), hoặc null nếu chưa đọc được. Trả từ cache khi
    /// còn hạn; hết hạn thì gọi GitHub 1 lần rồi cache lại theo kết quả (OK 5' / lỗi 1').</summary>
    public async Task<string?> GetLatestVersionAsync(CancellationToken ct = default)
    {
        lock (_lock)
            if (DateTimeOffset.UtcNow - _cachedAt < _cachedTtl) return _cached;

        string? version = null;
        try
        {
            await using var s = await Http.GetStreamAsync(LatestUrl, ct);
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
            if (doc.RootElement.TryGetProperty("tag_name", out var tag) && tag.ValueKind == JsonValueKind.String)
            {
                var t = tag.GetString()?.Trim();
                if (!string.IsNullOrEmpty(t))
                    version = t is ['v' or 'V', ..] ? t[1..] : t;
            }
        }
        catch { /* mạng/JSON/timeout → null, cache ngắn để thử lại sớm */ }

        lock (_lock)
        {
            _cached = version;
            _cachedAt = DateTimeOffset.UtcNow;
            _cachedTtl = version is null ? FailTtl : OkTtl;
        }
        return version;
    }
}
