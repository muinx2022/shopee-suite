namespace XuLyDonShopee.Core.Models;

/// <summary>
/// Một dòng đơn hàng ĐỌC ra từ bảng <c>orders</c> để hiển thị ở màn "Đơn hàng" (plan 2). Khác
/// <see cref="SyncedOrder"/> (DTO lúc thu thập): bản này mang thêm khóa DB <see cref="Id"/>,
/// <see cref="AccountId"/> và mốc <see cref="SyncedAt"/> đã parse — đủ để render bảng và xuất CSV.
/// CỐ Ý KHÔNG cầm <c>items_json</c>: màn xem dùng <see cref="ItemSummary"/>, không parse json từng dòng.
/// </summary>
public sealed class OrderRow
{
    /// <summary>Khóa chính DB của dòng đơn.</summary>
    public long Id { get; init; }

    /// <summary>Id tài khoản sở hữu đơn (map ra nhãn tài khoản ở tầng App).</summary>
    public long AccountId { get; init; }

    /// <summary>Tên đăng nhập shop của đơn (vd "alina99.store") — nhãn cột "Shop" + lọc màn Đơn hàng. Null cho đơn cũ.</summary>
    public string? ShopLogin { get; init; }

    /// <summary>Mã đơn hàng.</summary>
    public string OrderSn { get; init; } = string.Empty;

    /// <summary>Tên đăng nhập người mua. Có thể null.</summary>
    public string? BuyerUsername { get; init; }

    /// <summary>Số sản phẩm trong đơn.</summary>
    public int ItemCount { get; init; }

    /// <summary>Tên sản phẩm ĐẦU (hiển thị nhanh). Có thể null.</summary>
    public string? ItemSummary { get; init; }

    /// <summary>SKU sản phẩm (chuỗi alphanumeric liên tục cuối cùng của tên sản phẩm, vd "B02435"). Có thể null.</summary>
    public string? Sku { get; init; }

    /// <summary>Tổng tiền đã parse về số nguyên VND. Có thể null.</summary>
    public long? TotalPrice { get; init; }

    /// <summary>Nguyên văn tổng tiền (vd "₫166.500"). Có thể null.</summary>
    public string? TotalPriceText { get; init; }

    /// <summary>"Số tiền cuối cùng" đã parse (VND) từ trang chi tiết đơn — cột "Ước tính". Có thể null.</summary>
    public long? FinalAmount { get; init; }

    /// <summary>Nguyên văn "Số tiền cuối cùng" (vd "₫292.010"). Có thể null.</summary>
    public string? FinalAmountText { get; init; }

    /// <summary>Hình thức thanh toán. Có thể null.</summary>
    public string? PaymentMethod { get; init; }

    /// <summary>Trạng thái đơn (vd "Đã hủy" / "Chờ lấy hàng"). Có thể null.</summary>
    public string? Status { get; init; }

    /// <summary>Mô tả trạng thái. Có thể null.</summary>
    public string? StatusDescription { get; init; }

    /// <summary>Lý do hủy. Có thể null.</summary>
    public string? CancelReason { get; init; }

    /// <summary>Kênh vận chuyển (vd "Nhanh"). Có thể null.</summary>
    public string? Channel { get; init; }

    /// <summary>Đơn vị vận chuyển (vd "SPX Express"). Có thể null.</summary>
    public string? Carrier { get; init; }

    /// <summary>Mã vận đơn. Có thể null.</summary>
    public string? TrackingNumber { get; init; }

    /// <summary>Thời điểm sync gần nhất (UTC, đã parse từ DB).</summary>
    public DateTime SyncedAt { get; init; }
}
