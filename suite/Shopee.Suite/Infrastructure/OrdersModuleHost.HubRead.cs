using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Shopee.Core.Coordination;
using HubSharedOrderStatistics = Shopee.Core.Coordination.SharedOrderStatistics;
using XuLyDonShopee.App.Services;

namespace Shopee.Suite.Infrastructure;

// Partial của OrdersModuleHost: các hook ĐỌC dữ liệu dùng chung từ hub (thống kê đơn, số "chuẩn bị hàng",
// danh bạ sub-acc) + map DTO hub → kiểu của module. Pure move từ OrdersModuleHost.cs.
public static partial class OrdersModuleHost
{
    /// <summary>
    /// RÓT hook đọc thống kê đơn DÙNG CHUNG từ hub — ưu tiên nguồn này để tab "Thống kê" của mọi client cùng nhìn
    /// một ảnh chụp. Hub cũ / offline / lỗi → trả null để màn fallback local. Dùng cổng Client như các hook đẩy
    /// đơn, timeout ngắn vì payload nhỏ.
    /// </summary>
    private static void WireOrderStatisticsRead(AppServices services)
    {
        // Cờ "máy này đã cấu hình Hub chưa" — hook QueryOrderStatistics bên dưới được rót VÔ ĐIỀU KIỆN nên tự nó
        // không phân biệt được "chưa nối Hub" với "Hub chết"; màn Thống kê đọc cờ này để đừng tố oan Hub trên máy
        // vốn chạy độc lập. Đọc TƯƠI mỗi lần gọi (người dùng có thể cấu hình Hub rồi Reconnect giữa chừng).
        services.HubDaCauHinh = () => CoordinationRuntime.Client is not null;

        services.QueryOrderStatistics = async (fromUtc, toUtcExclusive, shopLogin, ct) =>
        {
            try
            {
                if (CoordinationRuntime.Client is not { } client)
                {
                    return null;
                }

                // Gửi THẲNG hai mốc UTC màn hình đã tính (biên [from, to)) — hub không tự quy đổi ngày theo giờ máy chủ.
                var stats = await client.GetOrderStatisticsAsync(fromUtc, toUtcExclusive, shopLogin, ct)
                    .ConfigureAwait(false);
                return stats is null ? null : MapSharedStats(stats);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[OrdersModuleHost] Đọc thống kê đơn từ hub lỗi: " + ex.Message);
                return null;
            }
        };
    }

    /// <summary>
    /// Chuyển DTO thống kê từ hub sang model local của module Đơn hàng.
    /// </summary>
    private static XuLyDonShopee.App.Services.SharedOrderStatistics MapSharedStats(HubSharedOrderStatistics stats) => new(
        stats.TotalOrders,
        stats.TotalItems,
        stats.NeedsAction,
        stats.Delivered,
        stats.Cancelled,
        stats.Revenue,
        stats.AverageOrder,
        stats.ActiveOrders,
        stats.WithTracking,
        stats.WithFinalAmount,
        stats.LastSyncedUtc,
        stats.StatusRows.Select(x => new XuLyDonShopee.App.Services.SharedStatBreakdown(
            x.Label, x.OrderCount, x.Value, x.Percentage)).ToList(),
        stats.ShopRows.Select(x => new XuLyDonShopee.App.Services.SharedShopStatRow(
            x.Shop, x.OrderCount, x.ItemCount, x.Revenue, x.Average, x.TrackingRate)).ToList(),
        stats.CarrierRows.Select(x => new XuLyDonShopee.App.Services.SharedStatBreakdown(
            x.Label, x.OrderCount, x.Value, x.Percentage)).ToList(),
        stats.PaymentRows.Select(x => new XuLyDonShopee.App.Services.SharedStatBreakdown(
            x.Label, x.OrderCount, x.Value, x.Percentage)).ToList());

    /// <summary>
    /// RÓT hook đọc SỐ ĐƠN "chuẩn bị hàng" chung toàn hệ thống (tab "Kết quả" của màn Tài khoản) — mẫu
    /// <see cref="WireHubPush"/>. Hub trả list <c>(shopUsername, count)</c> → đổi thành map
    /// <c>shop_login → count</c> (hub khoá shop theo ĐÚNG <c>shop_login</c> client đẩy lên nên tra thẳng được).
    /// <para><b>BẪY null vs rỗng:</b> hub chưa kết nối / lỗi / hub CŨ chưa có route → trả <c>null</c> = "không hỏi
    /// được" (màn GIỮ số cục bộ). TUYỆT ĐỐI không trả map rỗng ở các ca này — rỗng nghĩa là "hub bảo 0 đơn" và sẽ
    /// làm lưới về 0 mỗi lần rớt mạng.</para>
    /// Nuốt mọi lỗi (log <c>Trace</c>) trả null — trừ hủy CHỦ ĐỘNG (ct: người dùng đổi ngày/đổi tài khoản liên tục)
    /// cho xuyên để màn bỏ lượt cũ.
    /// </summary>
    private static void WirePrepareStatsRead(AppServices services)
    {
        services.QueryPrepareStats = async (day, ct) =>
        {
            try
            {
                if (!CoordinationRuntime.Active || CoordinationRuntime.Client is null)
                {
                    return null; // hub chưa kết nối → "không hỏi được", KHÔNG phải "0 đơn"
                }

                var stats = await CoordinationRuntime.Client.GetPrepareStatsAsync(day, ct).ConfigureAwait(false);
                if (stats is null)
                {
                    return null; // hub không phản hồi / hub cũ chưa có route
                }

                // Khoá map = shop_login, so khớp KHÔNG phân biệt hoa/thường: nhãn shop giữa account_shops của máy
                // và shops.username trên hub có thể lệch HOA/thường → tra Ordinal sẽ ra 0 một cách LẶNG.
                // Hai dòng hub trùng khoá sau khi bỏ hoa/thường là CÙNG một shop vật lý bị tách đôi → CỘNG DỒN
                // (lấy bản sau là mất số của bản kia). Giữ phép cộng kể cả khi hub đã gộp shop trùng — máy này có
                // thể đang nói chuyện với hub chưa nâng cấp.
                var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in stats)
                {
                    if (!string.IsNullOrWhiteSpace(s.ShopUsername))
                    {
                        var khoa = s.ShopUsername.Trim();
                        map.TryGetValue(khoa, out var dangCo);
                        map[khoa] = dangCo + s.Count;
                    }
                }
                return map;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // hủy CHỦ ĐỘNG (lượt sau đè lượt trước) → cho xuyên để màn bỏ lượt cũ
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[OrdersModuleHost] Đọc số chuẩn bị hàng từ hub lỗi: " + ex.Message);
                return null;
            }
        };
    }

    /// <summary>
    /// RÓT hook kéo DANH BẠ sub-acc Đơn hàng gộp từ mọi máy trên Hub (mẫu <see cref="WirePrepareStatsRead"/>) —
    /// máy MỚI dùng để tạo sẵn bản ghi tài khoản rỗng-mật-khẩu. Cổng kiểm là <c>Client</c> nên chạy được ở CẢ
    /// chế độ Full/Workspace lẫn chế độ Shopee. Hub chưa kết nối / lỗi / hub cũ 404 → trả <c>null</c> ("không hỏi
    /// được", khác list rỗng). Map DTO hub → kiểu của module (module không tham chiếu <c>Shopee.Core</c>).
    /// </summary>
    private static void WireOrdersDirectory(AppServices services)
    {
        services.QueryOrdersDirectory = async ct =>
        {
            try
            {
                if (CoordinationRuntime.Client is not { } client)
                {
                    return null; // hub chưa kết nối → "không hỏi được"
                }

                var dir = await client.GetOrdersAccountsDirectoryAsync(ct).ConfigureAwait(false);
                if (dir is null)
                {
                    return null; // offline / timeout / hub cũ chưa có route
                }

                return dir.Select(a => new OrdersDirectoryItem(
                    a.Login,
                    (a.Shops ?? new List<OrdersShopItem>())
                        .Select(s => (s.Login, s.Name)).ToList())).ToList();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // hủy CHỦ ĐỘNG → cho xuyên
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[OrdersModuleHost] Kéo danh bạ tài khoản từ hub lỗi: " + ex.Message);
                return null;
            }
        };
    }
}
