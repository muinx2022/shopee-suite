namespace Shopee.Core.Ai;

/// <summary>
/// Lỗi HTTP từ một nhà cung cấp AI (OpenAI/Anthropic/Gemini) — giữ status code để tầng retry phân loại
/// tạm/vĩnh viễn, và <see cref="RetryAfterMs"/> nếu server nói rõ phải chờ bao lâu.
/// </summary>
public sealed class AiHttpException(int statusCode, string message, int? retryAfterMs = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    /// <summary>True nếu là lỗi cấu hình/quyền (400/401/403/404) — retry vô ích, nên dừng và báo người dùng.</summary>
    public bool IsPermanent => StatusCode is 400 or 401 or 403 or 404;

    /// <summary>Thời gian chờ server yêu cầu (header <c>Retry-After</c>, đã quy về ms) — null nếu server
    /// không gửi. Tầng retry ưu tiên giá trị này thay backoff tự tính (chờ ngắn hơn là bị đập lại 429,
    /// chờ dài hơn là phí thời gian).</summary>
    public int? RetryAfterMs { get; } = retryAfterMs;
}
