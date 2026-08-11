namespace Shopee.Toolkit.Browser;

/// <summary>
/// Luật <b>"app chỉ chạy được bằng Brave"</b> (chốt 11/08/2026) — một chỗ duy nhất giữ câu thông báo và phép
/// kiểm, cho cả cổng chặn lúc khởi động (suite <c>App.OnStartup</c>) lẫn lúc phóng trình duyệt (orders
/// <c>BrowserLocator.RequireBraveExecutable</c>).
/// <para>Vì sao chỉ Brave: cầu nối Đơn hàng bắt buộc nạp extension qua <c>--load-extension</c>, mà Chromium
/// 137+ đã bỏ cờ cho phép cờ đó — trên Chrome/Edge đời mới extension <b>không nạp mà cũng không báo lỗi</b>,
/// cầu nối chỉ treo hết hạn rồi chết. Trước đây app có ComboBox chọn trình duyệt và một nhánh lùi âm thầm tải
/// Chromium đóng gói của Playwright; cả hai đã bỏ.</para>
/// <para>Đặt ở <c>Shopee.Toolkit</c> vì đây là NƠI DUY NHẤT cả hai phía cùng tham chiếu được: orders
/// <b>không</b> ref <c>suite/Shopee.Core</c> (xem <c>orders/CLAUDE.md</c>).</para>
/// </summary>
public static class BraveRequirement
{
    /// <summary>Câu báo thiếu Brave dùng chung. Có chỗ tải kèm theo — báo "không tìm thấy trình duyệt" trống
    /// không thì người dùng không biết phải làm gì.</summary>
    public const string ThieuBraveMessage =
        "Không tìm thấy trình duyệt Brave trên máy. App chỉ chạy được bằng Brave "
        + "(cầu nối cần nạp extension, Chrome/Edge đời mới đã chặn việc này). "
        + "Hãy cài Brave từ https://brave.com/download/ rồi mở lại app.";

    /// <summary>
    /// (THUẦN) Lý do phải CHẶN khởi động, hoặc <c>null</c> nếu chạy được. Nhận đường dẫn Brave đã dò
    /// (<c>BrowserLocator.FindBraveExecutable()</c>) thay vì tự dò, để test được cả hai nhánh trên máy bất kỳ.
    /// Đường dẫn rỗng/toàn khoảng trắng tính là KHÔNG có (dò hụt trả chuỗi rỗng cũng phải chặn).
    /// </summary>
    public static string? LyDoChanKhoiDong(string? bravePath)
        => string.IsNullOrWhiteSpace(bravePath) ? ThieuBraveMessage : null;
}
