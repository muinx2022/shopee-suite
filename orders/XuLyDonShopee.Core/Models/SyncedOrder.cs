namespace XuLyDonShopee.Core.Models;

/// <summary>
/// Một đơn hàng thu thập được từ danh sách "Tất cả" của trang bán hàng Shopee trong một lượt sync.
/// DTO thuần (KHÔNG dính DB/Playwright): Core quét DOM điền vào đây rồi trả về; tầng App lưu qua
/// <c>OrdersRepository.UpsertMany</c>. Khóa nghiệp vụ là <see cref="OrderSn"/> (mã đơn) theo từng tài khoản.
/// </summary>
public sealed class SyncedOrder
{
    /// <summary>Mã đơn hàng (vd "260716T6NPV58S") — token cuối của ô <c>.order-sn</c>. KHÓA upsert cùng account.</summary>
    public string OrderSn { get; set; } = string.Empty;

    /// <summary>Số đơn Shopee trong href thẻ card (<c>/portal/sale/order/&lt;số&gt;</c>). Có thể null.</summary>
    public string? ShopeeOrderId { get; set; }

    /// <summary>Tên đăng nhập người mua (<c>.buyer-username</c>). Có thể null.</summary>
    public string? BuyerUsername { get; set; }

    /// <summary>Mảng JSON các sản phẩm <c>{name, variation, amount, image}</c> (mặc định <c>"[]"</c>).</summary>
    public string ItemsJson { get; set; } = "[]";

    /// <summary>Số sản phẩm trong đơn (độ dài mảng items).</summary>
    public int ItemCount { get; set; }

    /// <summary>Tên sản phẩm ĐẦU (hiển thị nhanh ở màn xem — plan 2). Có thể null nếu không có item.</summary>
    public string? ItemSummary { get; set; }

    /// <summary>SKU sản phẩm (chuỗi alphanumeric liên tục cuối cùng của tên sản phẩm, vd "B02435"). Có thể null.</summary>
    public string? Sku { get; set; }

    /// <summary>Nguyên văn tổng tiền (<c>.total-price</c>, vd "₫166.500"). Có thể null.</summary>
    public string? TotalPriceText { get; set; }

    /// <summary>Tổng tiền đã parse về số nguyên VND (bỏ mọi ký tự không phải số); parse lỗi → null.</summary>
    public long? TotalPrice { get; set; }

    /// <summary>
    /// "Số tiền cuối cùng" đã parse về số nguyên VND, lấy từ TRANG CHI TIẾT đơn (card <c>[type='FinalAmount']</c>
    /// → <c>.amount</c>). CHỈ lấy cho đơn KHÁC "Đã hủy". Null nếu chưa lấy / không đọc được.
    /// </summary>
    public long? FinalAmount { get; set; }

    /// <summary>Nguyên văn "Số tiền cuối cùng" (vd "₫292.010") từ trang chi tiết. Có thể null.</summary>
    public string? FinalAmountText { get; set; }

    /// <summary>Hình thức thanh toán (<c>.payment-method</c>). Có thể null.</summary>
    public string? PaymentMethod { get; set; }

    /// <summary>Trạng thái đơn (<c>.status-info-col .status</c>, vd "Đã hủy" / "Chờ lấy hàng"). Có thể null.</summary>
    public string? Status { get; set; }

    /// <summary>Mô tả trạng thái (<c>.status-description</c>). Có thể null.</summary>
    public string? StatusDescription { get; set; }

    /// <summary>Lý do hủy (từ popover ẩn chứa "Lý do hủy:"). Có thể null.</summary>
    public string? CancelReason { get; set; }

    /// <summary>Kênh vận chuyển (<c>.maksed-channel-name</c>, vd "Nhanh"). Có thể null.</summary>
    public string? Channel { get; set; }

    /// <summary>Đơn vị vận chuyển (<c>.fulfilment-channel-name</c>, vd "SPX Express"). Có thể null.</summary>
    public string? Carrier { get; set; }

    /// <summary>Mã vận đơn (<c>.tracking-number</c>) — nhiều đơn chưa có. Có thể null.</summary>
    public string? TrackingNumber { get; set; }
}
