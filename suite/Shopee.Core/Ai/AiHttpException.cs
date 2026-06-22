namespace Shopee.Core.Ai;

/// <summary>
/// Lỗi HTTP từ nhà cung cấp AI (kèm mã trạng thái). Cho phép caller phân biệt lỗi cấu hình/quyền
/// (key sai, hết quota, model sai — KHÔNG nên retry) với lỗi tạm (rate-limit/5xx — nên retry),
/// tránh vòng lặp retry vô hạn khi key hỏng.
/// </summary>
public sealed class AiHttpException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    /// <summary>True nếu là lỗi cấu hình/quyền (400/401/403/404) — retry vô ích, nên dừng và báo người dùng.</summary>
    public bool IsPermanent => StatusCode is 400 or 401 or 403 or 404;
}
