using System.Collections.Generic;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Kết quả một lượt sync đơn hàng ở tab "Tất cả":
/// <list type="bullet">
/// <item><see cref="Orders"/> — danh sách đơn đã gom (ĐÃ khử trùng lặp theo mã đơn trong phiên sync).</item>
/// <item><see cref="Pages"/> — số trang danh sách đã quét.</item>
/// <item><see cref="ReachedPageCap"/> — có CHẠM chốt chặn số trang tối đa không (còn đơn chưa quét hết).</item>
/// </list>
/// Core CHỈ thu thập &amp; trả DTO — việc lưu DB do tầng App làm (Core không tham chiếu DB App).
/// </summary>
public record SyncOrdersResult(List<SyncedOrder> Orders, int Pages, bool ReachedPageCap);
