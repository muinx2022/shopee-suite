namespace Shopee.Core.Coordination;

/// <summary>
/// Cấu hình DÙNG CHUNG của module Đơn hàng (file <c>config/orders.json</c> trên Hub) — hiện chỉ khối
/// "Đồng bộ Google Sheet". Hub là NGUỒN SỰ THẬT: sửa được ở trang <c>/config/orders</c> của Hub HOẶC ở màn
/// Cài đặt của client (client lưu local rồi đẩy lên Hub), mọi máy còn lại tự nhận về.
/// <para>
/// <b>Luật áp bản Hub (bất biến):</b> URL trống = CÔNG TẮC TẮT đồng bộ GSheet, nên Hub trống thì TUYỆT ĐỐI
/// không đè client (kẻo cả fleet âm thầm tắt ghi sheet). Hub có URL ⇒ coi khối GSheet là MỘT đơn vị đã cấu
/// hình ⇒ áp CẢ hai field (tab trống lúc đó mang đúng nghĩa "tự động theo tháng"). Quyết định nằm ở hàm
/// thuần <c>XuLyDonShopee.Core.Services.GsheetConfigSync.QuyetDinhApBanHub</c> (có unit test).
/// </para>
/// Cấu hình RIÊNG-MÁY (thư mục phiếu/video/ảnh, trình duyệt, chu kỳ theo dõi) KHÔNG bao giờ nằm ở đây.
/// </summary>
public sealed class OrdersSharedConfig
{
    /// <summary>URL Web App Google Apps Script (kết thúc <c>/exec</c>). Trống/null = chưa cấu hình (⇒ không đè client).</summary>
    public string? GsheetWebAppUrl { get; set; }

    /// <summary>Tên tab (sheet) đích — OVERRIDE. Trống = TỰ ĐỘNG theo tháng ("Tháng MM-yyyy").</summary>
    public string? GsheetTabName { get; set; }
}
