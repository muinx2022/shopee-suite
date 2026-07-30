namespace Shopee.Core.Coordination;

/// <summary>
/// Một đơn ĐỌC VỀ từ hub (GET /api/orders). MIRROR <c>OrderRecord</c> của hub
/// (server/Shopee.Hub.Web/Data/HubDatabase.Orders.cs) — hub map TƯỜNG MINH sang kiểu này, KHÔNG serialize
/// thẳng <c>OrderRecord</c>, để đổi tên field bên hub là gãy build chứ không âm thầm làm rỗng cột bên client.
/// Class (không record) + property settable để JSON bind khoan dung (field thiếu → giá trị mặc định).
/// <para><b>CHỈ ĐỂ XEM.</b> Đơn ở đây thuộc shop của MÁY KHÁC nên KHÔNG có <c>account_id</c> local — tuyệt đối
/// không ghi vào bảng <c>orders</c> của máy này (sẽ bị đẩy ngược lên hub, ghi trùng dòng Google Sheet, bị vòng
/// dọn "đơn kết thúc" xoá).</para>
/// </summary>
public sealed class HubOrderItem
{
    /// <summary>Id dòng trên hub (khoá chính bảng <c>orders</c> của hub) — chỉ để phân biệt dòng khi hiển thị.</summary>
    public long Id { get; set; }

    /// <summary>Id SỐ của shop trên hub — tra tên qua GET /api/shops (<see cref="HubShopItem"/>).</summary>
    public long ShopId { get; set; }

    public string OrderSn { get; set; } = string.Empty;
    public string? ShopeeOrderId { get; set; }
    public string? BuyerUsername { get; set; }
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

    /// <summary>Thời điểm client đẩy đơn này lên hub (UTC).</summary>
    public DateTimeOffset SyncedAt { get; set; }

    /// <summary>Thời điểm hub NHẬN file phiếu PDF của đơn. NULL = chưa có phiếu trên hub.</summary>
    public DateTimeOffset? SlipAt { get; set; }
}

/// <summary>
/// MỘT TRANG kết quả GET /api/orders — lọc + phân trang chạy PHÍA HUB (tham số <c>shopId/status/q/page/pageSize</c>),
/// client KHÔNG tải hết về rồi lọc. <see cref="Total"/> = tổng đơn khớp bộ lọc trên MỌI trang (mẫu số phân trang).
/// </summary>
public sealed class HubOrdersPage
{
    public List<HubOrderItem> Items { get; set; } = [];
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

/// <summary>
/// Một dòng kết quả GET /prepare-stats: số đơn ĐÃ "chuẩn bị hàng" của MỘT shop trong ngày hỏi. Hub đếm THẲNG từ
/// bảng <c>orders</c> (khoá <c>shop_id+order_sn</c>) nên mỗi đơn chỉ tính một lần dù bao nhiêu máy cùng chạy — đây
/// là con số CHUNG toàn hệ thống, khác bộ đếm cục bộ <c>prepare_daily</c> của từng máy. Class (không record) +
/// property settable để JSON bind khoan dung.
/// </summary>
public sealed class PrepareStatItem
{
    /// <summary>Tài khoản đăng nhập shop (<c>shops.username</c> trên hub) — CHÍNH là <c>shop_login</c> client dùng
    /// làm khoá khi đẩy đơn, nên client tra thẳng vào lưới tab "Kết quả" không cần dịch tên.</summary>
    public string ShopUsername { get; set; } = string.Empty;

    /// <summary>Số đơn của shop này đã chuẩn bị hàng trong ngày hỏi.</summary>
    public int Count { get; set; }
}

/// <summary>
/// Dữ liệu thống kê đơn dùng chung lấy từ Hub. Số liệu được gom trên hub để mọi client nhìn cùng một ảnh chụp,
/// thay vì mỗi máy tự tính trên SQLite local.
/// <para><b>CHỈ SỐ THÔ.</b> Hub KHÔNG dựng chuỗi hiển thị (ngày/tiền/tỉ lệ): máy chủ chạy giờ UTC và không biết
/// múi giờ/định dạng của từng client — client tự định dạng từ các số ở đây.</para>
/// </summary>
public sealed class SharedOrderStatistics
{
    public int TotalOrders { get; set; }
    public int TotalItems { get; set; }
    public int NeedsAction { get; set; }
    public int Delivered { get; set; }
    public int Cancelled { get; set; }
    public long Revenue { get; set; }
    public long AverageOrder { get; set; }

    /// <summary>Số đơn HIỆU LỰC (không hủy) — mẫu số của "TB / đơn hiệu lực" và của <see cref="WithFinalAmount"/>.</summary>
    public int ActiveOrders { get; set; }

    /// <summary>Số đơn có mã vận đơn (trên tổng <see cref="TotalOrders"/>).</summary>
    public int WithTracking { get; set; }

    /// <summary>Số đơn hiệu lực đã có "số tiền cuối cùng" (trên tổng <see cref="ActiveOrders"/>).</summary>
    public int WithFinalAmount { get; set; }

    /// <summary>Lần đẩy đơn lên hub GẦN NHẤT trong khoảng hỏi (<c>MAX(synced_at)</c>, UTC); null = khoảng đó không
    /// có đơn nào. Client tự đổi sang giờ máy mình để hiển thị.</summary>
    public DateTimeOffset? LastSyncedUtc { get; set; }

    public List<SharedOrderStatisticBreakdown> StatusRows { get; set; } = [];
    public List<SharedOrderStatisticBreakdown> CarrierRows { get; set; } = [];
    public List<SharedOrderStatisticBreakdown> PaymentRows { get; set; } = [];
    public List<SharedShopStatisticRow> ShopRows { get; set; } = [];
}

public sealed class SharedOrderStatisticBreakdown
{
    public string Label { get; set; } = string.Empty;
    public int OrderCount { get; set; }
    public long Value { get; set; }
    public double Percentage { get; set; }
}

public sealed class SharedShopStatisticRow
{
    public string Shop { get; set; } = string.Empty;
    public int OrderCount { get; set; }
    public int ItemCount { get; set; }
    public long Revenue { get; set; }
    public long Average { get; set; }
    public double TrackingRate { get; set; }
}

/// <summary>
/// Một shop hub theo dõi (GET /api/shops) — RÚT GỌN từ <c>Shop</c> của hub: chỉ 3 field cần để đổi
/// <see cref="HubOrderItem.ShopId"/> (số) sang tên hiển thị. CỐ Ý bỏ password/cookie/proxy của bản hub —
/// client xem đơn không cần và không nên nhận.
/// </summary>
public sealed class HubShopItem
{
    public long Id { get; set; }
    /// <summary>Tên hiển thị shop (hub đặt = username khi tự đăng ký lúc client push đơn).</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Tài khoản đăng nhập shop — KHÓA tự đăng ký shop trên hub.</summary>
    public string? Username { get; set; }
}
