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

/// <summary>Một file phiếu PDF của một đơn client đẩy lên hub (POST /api/orders/slip). Class + property settable
/// để JSON bind khoan dung.</summary>
public sealed class SlipPushItem
{
    /// <summary>Mã đơn hàng — phải KHỚP đơn đã tồn tại trên hub (shop_id+order_sn).</summary>
    public string OrderSn { get; set; } = string.Empty;
    /// <summary>Nội dung file phiếu PDF mã hoá base64 (hub kiểm magic %PDF- + trần 5MB trước khi lưu).</summary>
    public string FileBase64 { get; set; } = string.Empty;
}

/// <summary>
/// Body POST /api/orders/slip: client đẩy MỘT LÔ (≤5) file phiếu của MỘT shop lên hub. Hub tự đăng ký/khớp
/// shop theo <see cref="ShopUsername"/> (như /api/orders/push). Mỗi phiếu: đơn phải đã có trên hub, nếu chưa →
/// hub báo per-item <c>missing</c>; base64/PDF/kích thước sai → per-item <c>errors</c>.
/// </summary>
public sealed class OrdersSlipPushRequest
{
    /// <summary>Tài khoản đăng nhập shop (username/email/SĐT) — KHÓA khớp shop trên hub (như /api/orders/push).</summary>
    public string ShopUsername { get; set; } = string.Empty;
    /// <summary>Tên hiển thị shop (tùy chọn).</summary>
    public string? ShopName { get; set; }
    public List<SlipPushItem> Slips { get; set; } = [];
}

/// <summary>Một phiếu bị lỗi khi lưu (base64 hỏng / không phải PDF / quá lớn / tên đơn không hợp lệ).</summary>
public sealed record SlipPushError(string OrderSn, string Error);

/// <summary>Kết quả lưu lô phiếu trên hub: <see cref="Saved"/> = số phiếu đã lưu; <see cref="Missing"/> = mã đơn
/// CHƯA có trên hub (client thử lại lượt sau); <see cref="Errors"/> = phiếu lỗi. Client suy ra tập ĐÃ LƯU =
/// (lô gửi) − Missing − Errors để đánh dấu đúng đơn.</summary>
public sealed record OrdersSlipPushResult(int Saved, List<string> Missing, List<SlipPushError> Errors);
