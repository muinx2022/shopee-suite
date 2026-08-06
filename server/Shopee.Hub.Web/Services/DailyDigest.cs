using Shopee.Core.Coordination;
using Shopee.Hub;

namespace Shopee.Hub.Web.Services;

/// <summary>Số liệu MỘT tin tổng kết ngày (H2.1) — gom sẵn để dựng tin, tách khỏi việc đọc DB/fleet.
/// <paramref name="TheoShop"/> đã sắp GIẢM DẦN theo số đơn (bên dựng tin chỉ cắt top).</summary>
internal sealed record SoLieuTongKet(
    int TongDonCho,
    IReadOnlyList<(string Shop, int SoDon)> TheoShop,
    int MaTraMoi,
    int ShopCanhBaoDiaChi,
    int MayOffline);

/// <summary>
/// LÕI THUẦN của tin tổng kết cuối ngày (H2.1) — tách khỏi <see cref="DailyDigestService"/> để test được không
/// cần dựng host/webhook/đồng hồ.
/// <para>
/// <b>Chống gửi trùng khi hub restart quanh giờ gửi:</b> mốc "đã gửi ngày d" được ghi vào bảng <c>settings</c>
/// (KHÔNG để trong bộ nhớ như cảnh báo máy offline — hub restart lúc 21:05 mà quên mốc là bắn tin thứ hai ngay).
/// So sánh theo NGÀY VIỆT NAM dạng <c>yyyy-MM-dd</c>.
/// </para>
/// <para>
/// Điều kiện gửi là <c>giờ VN &gt;= giờ hẹn</c> chứ không phải bằng đúng: hub tắt suốt 21:00–23:00 rồi lên lại thì
/// vẫn gửi tổng kết của NGÀY ĐÓ (muộn còn hơn mất). Sang ngày mới (0h) thì ngày đã đổi nên tin cũ không đuổi theo.
/// </para>
/// </summary>
internal static class DailyDigest
{
    /// <summary>Giờ gửi mặc định (giờ VN) khi admin chưa đặt ở /settings.</summary>
    public const int GioMacDinh = 21;

    /// <summary>Kẹp giờ người dùng nhập vào 0..23 (ô number vẫn gõ tay được số ngoài min/max).</summary>
    public const int GioMin = 0;
    public const int GioMax = 23;

    /// <summary>HÀM THUẦN của "giờ gửi đang có hiệu lực": thiếu/hỏng → <see cref="GioMacDinh"/>.</summary>
    public static int KepGio(string? raw) =>
        int.TryParse((raw ?? "").Trim(), out var n) ? Math.Clamp(n, GioMin, GioMax) : GioMacDinh;

    /// <summary>Nhãn NGÀY VIỆT NAM (<c>yyyy-MM-dd</c>) của mốc <paramref name="now"/> — khoá chống gửi trùng.</summary>
    public static string NgayVn(DateTimeOffset now) => GioVietNam.Doi(now).ToString("yyyy-MM-dd");

    /// <summary>
    /// Đã tới lượt gửi tổng kết của ngày chứa <paramref name="now"/> chưa: đã qua <paramref name="gio"/> (giờ VN)
    /// VÀ ngày đó chưa từng gửi (<paramref name="ngayDaGui"/> = giá trị đọc từ settings, rỗng = chưa gửi bao giờ).
    /// <paramref name="ngayHomNay"/> luôn trả nhãn ngày VN hiện tại để caller ghi lại mốc sau khi gửi.
    /// </summary>
    public static bool DenLuotGui(DateTimeOffset now, int gio, string? ngayDaGui, out string ngayHomNay)
    {
        ngayHomNay = NgayVn(now);
        if (GioVietNam.Doi(now).Hour < gio) return false;
        return !string.Equals((ngayDaGui ?? "").Trim(), ngayHomNay, StringComparison.Ordinal);
    }

    /// <summary>
    /// Gom số liệu tổng kết cho NGÀY VIỆT NAM chứa <paramref name="now"/>:
    /// <list type="bullet">
    /// <item>đơn "chuẩn bị hàng" phát sinh hôm nay theo shop — DÙNG LẠI đúng truy vấn của thẻ "Đơn chờ hôm nay"
    /// (<see cref="HubDatabase.ShopOrderSummaries"/>, lọc <c>first_seen_at</c> trong khoảng UTC của ngày VN) để
    /// tin nhắn và trang chủ không bao giờ nói hai con số khác nhau;</item>
    /// <item>mã trả hàng mới hôm nay (<see cref="HubDatabase.CountReturnCodesInRange"/>);</item>
    /// <item>số shop còn banner địa chỉ ĐANG MỞ;</item>
    /// <item>số máy mất nhịp quá <paramref name="nguongOffline"/> — cùng ngưỡng với cảnh báo máy offline để hai
    /// tính năng không có hai định nghĩa "offline".</item>
    /// </list>
    /// Shop chưa có <c>username</c> (id lạ) → nhãn là id, không bỏ dòng (đơn vẫn phải được đếm).
    /// </summary>
    public static SoLieuTongKet GomSoLieu(
        HubDatabase db, FleetSnapshot fleet, DateTimeOffset now, TimeSpan nguongOffline)
    {
        var (fromUtc, toUtcExclusive) = GioVietNam.KhoangNgayUtc(now);

        var ten = db.ListShops().ToDictionary(s => s.Id, s => string.IsNullOrWhiteSpace(s.Username) ? s.Name : s.Username!);
        var theoShop = db.ShopOrderSummaries(HomeOverview.TrangThaiCho, fromUtc, toUtcExclusive)
            .Where(s => s.Waiting > 0)
            .Select(s => (Shop: ten.TryGetValue(s.ShopId, out var n) && !string.IsNullOrWhiteSpace(n)
                              ? n
                              : s.ShopId.ToString(), SoDon: s.Waiting))
            .OrderByDescending(x => x.SoDon)
            .ThenBy(x => x.Shop, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var mayOffline = fleet.Machines.Count(m => now - m.LastSeen > nguongOffline);

        return new SoLieuTongKet(
            theoShop.Sum(x => x.SoDon),
            theoShop,
            db.CountReturnCodesInRange(fromUtc, toUtcExclusive),
            db.ActivePickupAlerts().Count,
            mayOffline);
    }
}
