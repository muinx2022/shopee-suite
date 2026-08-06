using Shopee.Hub;

namespace Shopee.Hub.Web.Services;

/// <summary>
/// Các số của HÀNG THẺ TỔNG QUAN đầu trang chủ (đợt H1.1) mà việc tính không nằm gọn trong một property của
/// trang. Tách ra khỏi <c>Fleet.razor.cs</c> để test được (trang Razor thì không).
/// </summary>
internal static class HomeOverview
{
    /// <summary>Trạng thái đơn coi là "đang chờ xử lý". CÙNG chuỗi mà tab Đơn hàng của trang Giao việc dùng cho
    /// cột "Đơn chờ hôm nay" — hai chỗ phải nói cùng một số.</summary>
    public const string TrangThaiCho = "Chờ lấy hàng";

    /// <summary>
    /// Tổng đơn <see cref="TrangThaiCho"/> XUẤT HIỆN trong ngày Việt Nam chứa <paramref name="now"/>, gộp mọi shop.
    /// <para>Dùng LẠI đúng truy vấn của "Đơn chờ hôm nay" (<see cref="HubDatabase.ShopOrderSummaries"/>): lọc theo
    /// <c>first_seen_at</c> — lần ĐẦU đơn lên hub — trong khoảng UTC của ngày VN. TUYỆT ĐỐI không đổi sang
    /// <c>synced_at</c>: cột đó bị ghi đè mỗi lần client đẩy lại nên đơn cũ sẽ nhảy vào hôm nay.</para>
    /// </summary>
    public static int DonChoHomNay(HubDatabase db, DateTimeOffset now)
    {
        var (fromUtc, toUtcExclusive) = GioVietNam.KhoangNgayUtc(now);
        return db.ShopOrderSummaries(TrangThaiCho, fromUtc, toUtcExclusive).Sum(s => s.Waiting);
    }
}
