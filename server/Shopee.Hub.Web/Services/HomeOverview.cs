using Shopee.Hub;

namespace Shopee.Hub.Web.Services;

/// <summary>
/// Các số của HÀNG THẺ TỔNG QUAN đầu trang chủ (đợt H1.1) mà việc tính không nằm gọn trong một property của
/// trang. Tách ra khỏi <c>Fleet.razor.cs</c> để test được (trang Razor thì không).
/// </summary>
internal static class HomeOverview
{
    /// <summary>NHÃN trạng thái "đang chờ xử lý" cho tooltip/tiêu đề các trang. Việc ĐẾM từ 11/08 không còn so
    /// exact chuỗi này nữa mà đi qua <c>ShopeeShippingNav.LaChuanBiHang</c> (contains, chịu biến thể) — cùng một
    /// định nghĩa với mọi bộ đếm còn lại của hệ (T6).</summary>
    public const string TrangThaiCho = "Chờ lấy hàng";

    /// <summary>
    /// Tổng đơn "đang chờ" (<c>LaChuanBiHang</c>) XUẤT HIỆN trong ngày Việt Nam chứa <paramref name="now"/>, gộp
    /// mọi shop.
    /// <para>Dùng LẠI đúng truy vấn của "Đơn chờ hôm nay" (<see cref="HubDatabase.ShopOrderSummaries"/>): lọc theo
    /// <c>first_seen_at</c> — lần ĐẦU đơn lên hub — trong khoảng UTC của ngày VN. TUYỆT ĐỐI không đổi sang
    /// <c>synced_at</c>: cột đó bị ghi đè mỗi lần client đẩy lại nên đơn cũ sẽ nhảy vào hôm nay.</para>
    /// </summary>
    public static int DonChoHomNay(HubDatabase db, DateTimeOffset now)
    {
        var (fromUtc, toUtcExclusive) = GioVietNam.KhoangNgayUtc(now);
        return db.ShopOrderSummaries(fromUtc, toUtcExclusive).Sum(s => s.Waiting);
    }
}
