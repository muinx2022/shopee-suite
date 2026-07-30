namespace Shopee.Core.Cdp;

/// <summary>
/// Nhận diện lỗi CDP/Playwright thuộc loại TẠM THỜI do trang vừa điều hướng (reload/redirect) hoặc target
/// vừa bị đóng — nên thử lại thay vì coi là hỏng thật. Gộp 3 danh sách marker gần trùng (Import-to-store của
/// UpdateProduct, vòng điền form login của MultiBrave, bộ lọc lỗi service worker của runner extension).
/// </summary>
public static class CdpErrors
{
    // Tập marker = HỢP của 3 bản cũ (bản nào cũng chỉ có một phần):
    //   "Execution context was destroyed" / "most likely because of a navigation" — Playwright khi trang nạp lại
    //   "Cannot find context"            — CDP evaluate vào context đã chết ("… with specified id")
    //   "Target closed" / "Inspected target navigated or closed" — tab/SW biến mất giữa lệnh
    //   "WebSocket"                      — kết nối CDP đứt (gồm "remote party closed the WebSocket")
    private static readonly string[] NavigationMarkers =
    [
        "Execution context was destroyed",
        "most likely because of a navigation",
        "Cannot find context",
        "Target closed",
        "Inspected target navigated or closed",
        "WebSocket",
    ];

    /// <summary>true nếu <paramref name="message"/> là lỗi điều hướng/target-đóng tạm thời.</summary>
    public static bool IsTransientNavigationError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        foreach (var marker in NavigationMarkers)
        {
            if (message.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <inheritdoc cref="IsTransientNavigationError(string?)"/>
    public static bool IsTransientNavigationError(Exception? ex) =>
        IsTransientNavigationError(ex?.Message);
}
