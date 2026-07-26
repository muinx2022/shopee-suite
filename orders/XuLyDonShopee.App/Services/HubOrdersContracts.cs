using System;
using System.Collections.Generic;

namespace XuLyDonShopee.App.Services;

/// <summary>
/// Bộ lọc + phân trang cho một lượt ĐỌC đơn từ Hub (màn "Đơn toàn hệ thống"). LỌC VÀ PHÂN TRANG CHẠY PHÍA HUB —
/// mọi field ở đây được shell suite chuyển thẳng thành tham số <c>GET /api/orders?shopId=&amp;status=&amp;q=&amp;page=&amp;pageSize=</c>.
/// <paramref name="ShopId"/> null = mọi shop; <paramref name="Status"/>/<paramref name="Search"/> trống = không lọc.
/// </summary>
public sealed record HubOrdersQuery(long? ShopId, string? Status, string? Search, int Page, int PageSize);

/// <summary>
/// Một đơn ĐỌC VỀ từ Hub — đơn của MÁY KHÁC, CHỈ ĐỂ XEM. Kiểu RIÊNG của module Đơn hàng (module KHÔNG tham
/// chiếu <c>Shopee.Core</c> nên không thấy DTO <c>HubOrderItem</c>; <c>OrdersModuleHost</c> chuyển đổi qua lại).
/// <para><b>KHÔNG</b> phải <see cref="XuLyDonShopee.Core.Models.OrderRow"/>: đơn này không có <c>account_id</c>
/// local và TUYỆT ĐỐI không được ghi vào bảng <c>orders</c> của máy này (sẽ bị đẩy ngược lên Hub, ghi trùng
/// dòng Google Sheet, bị vòng dọn "đơn kết thúc" xoá, bị vòng chờ đẩy nhặt nhầm).</para>
/// </summary>
public sealed class HubOrderView
{
    /// <summary>Id SỐ của shop trên Hub — tra tên qua danh sách shop (hook <c>ListHubShops</c>).</summary>
    public long ShopId { get; init; }

    public string OrderSn { get; init; } = string.Empty;
    public string? BuyerUsername { get; init; }
    public int ItemCount { get; init; }
    public string? ItemSummary { get; init; }
    public string? Sku { get; init; }
    public long? TotalPrice { get; init; }
    public string? TotalPriceText { get; init; }
    public long? FinalAmount { get; init; }
    public string? FinalAmountText { get; init; }
    public string? PaymentMethod { get; init; }
    public string? Status { get; init; }
    public string? StatusDescription { get; init; }
    public string? CancelReason { get; init; }
    public string? Carrier { get; init; }
    public string? TrackingNumber { get; init; }

    /// <summary>Thời điểm client (máy chủ sở hữu đơn) đẩy đơn lên Hub — UTC.</summary>
    public DateTimeOffset SyncedAt { get; init; }
}

/// <summary>
/// Kết quả MỘT TRANG đọc từ Hub. <see cref="Total"/> = tổng đơn khớp bộ lọc trên MỌI trang (mẫu số phân trang),
/// <see cref="Items"/> chỉ là trang hiện tại.
/// </summary>
public sealed record HubOrdersResult(IReadOnlyList<HubOrderView> Items, int Total, int Page, int PageSize);
