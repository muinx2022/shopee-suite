namespace Shopee.Core.Coordination;

/// <summary>
/// Một đơn hàng client đẩy lên hub (POST /api/orders/push). MIRROR <c>SyncedOrder</c> của module đơn Shopee
/// (orders/XuLyDonShopee.Core/Models/SyncedOrder.cs) để client map 1-1, khỏi lệch field. Class (không record)
/// + property settable để JSON bind khoan dung (field thiếu → giá trị mặc định).
/// </summary>
public sealed class OrderPushItem
{
    /// <summary>Mã đơn hàng (vd "260716T6NPV58S"). KHÓA upsert cùng shop.</summary>
    public string OrderSn { get; set; } = string.Empty;
    public string? ShopeeOrderId { get; set; }
    public string? BuyerUsername { get; set; }
    /// <summary>Mảng JSON các sản phẩm {name, variation, amount, image}.</summary>
    public string ItemsJson { get; set; } = "[]";
    public int ItemCount { get; set; }
    public string? ItemSummary { get; set; }
    public string? Sku { get; set; }
    public long? TotalPrice { get; set; }
    public string? TotalPriceText { get; set; }
    public long? FinalAmount { get; set; }
    public string? FinalAmountText { get; set; }
    public string? PaymentMethod { get; set; }
    public string? Status { get; set; }
    public string? StatusDescription { get; set; }
    public string? CancelReason { get; set; }
    public string? Channel { get; set; }
    public string? Carrier { get; set; }
    public string? TrackingNumber { get; set; }
}

/// <summary>
/// Body POST /api/orders/push: client đẩy đơn đã sync của MỘT shop lên hub. Hub tự đăng ký shop theo
/// <see cref="ShopUsername"/> (client không cần biết id trên hub) — <see cref="ShopName"/> chỉ để hiển thị/
/// đặt tên shop khi tạo mới.
/// </summary>
public sealed class OrdersPushRequest
{
    /// <summary>Tài khoản đăng nhập shop (username/email/SĐT) — KHÓA đăng ký shop trên hub.</summary>
    public string ShopUsername { get; set; } = string.Empty;
    /// <summary>Tên hiển thị shop (tùy chọn). Trống → hub lấy tạm <see cref="ShopUsername"/> làm tên.</summary>
    public string? ShopName { get; set; }
    public List<OrderPushItem> Orders { get; set; } = [];
}

/// <summary>Kết quả upsert đơn: số đơn vừa thêm mới + số đơn cập nhật.</summary>
public sealed record OrdersPushResult(int Added, int Updated);
