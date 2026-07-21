namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Kết quả xử lý MỘT đơn: về "Tất cả" → Chuẩn bị hàng → tự mang ra bưu cục → Xác nhận → In phiếu giao
/// (kèm chụp/lưu phiếu best-effort). Phân biệt bước hỏng để app báo đúng.
/// <para>
/// Ghi chú: chụp/lưu phiếu THẤT BẠI <b>KHÔNG</b> coi là fail cả đơn — đơn đã được arrange; chỉ log cảnh báo.
/// <see cref="Ok"/> nghĩa là đã qua bước "In phiếu giao" (đã bắt được tab phiếu). Việc chụp/lưu phiếu là
/// best-effort có log.
/// </para>
/// </summary>
public enum ArrangeShipmentResult
{
    /// <summary>Đã xử lý xong 1 đơn (đã bấm In phiếu giao + bắt tab phiếu; chụp/lưu là best-effort).</summary>
    Ok,
    /// <summary>Không còn đơn nào trong danh sách chờ xử lý → dừng vòng (plan sau dùng để lặp).</summary>
    NoOrder,
    /// <summary>Lỗi bất ngờ / không có trang/phiên.</summary>
    Failed,
    /// <summary>Không mở được trang "Tất cả" (danh sách đơn).</summary>
    OrdersPageNotOpened,
    /// <summary>Không thấy / không bấm được nút "Chuẩn bị hàng".</summary>
    PrepareNotFound,
    /// <summary>Modal "Giao Đơn Hàng" không mở.</summary>
    ShipModalNotOpened,
    /// <summary>Không bấm được nút "Xác nhận" trong modal "Giao Đơn Hàng".</summary>
    ConfirmFailed,
    /// <summary>Modal "Thông Tin Chi Tiết" không mở.</summary>
    DetailModalNotOpened,
    /// <summary>Không bấm được "In phiếu giao" / không bắt được tab phiếu.</summary>
    PrintFailed,
}
