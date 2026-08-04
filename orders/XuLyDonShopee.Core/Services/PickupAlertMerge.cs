namespace XuLyDonShopee.Core.Services;

/// <summary>Một dòng banner lỗi địa chỉ kéo từ Hub (kể cả đã dismiss) — dùng merge local.</summary>
public readonly record struct PickupAlertHubDong(
    string ShopLogin,
    string Province,
    bool Dismissed,
    long Rev);

/// <summary>Quyết định sau khi so một shop giữa Hub và local.</summary>
public enum MergePickupAlertAction
{
    /// <summary>Nhận trạng thái Hub về local (hiện/ẩn banner theo Hub) và nhớ <c>rev</c>.</summary>
    TheoHub,

    /// <summary>Local có thay đổi tại chỗ chưa lên Hub → đẩy lên Hub, KHÔNG nghe Hub lượt này.</summary>
    DayLenHub,

    /// <summary>Hai bên đã khớp (rev Hub không mới hơn) → không làm gì.</summary>
    GiuNguyen,
}

/// <summary>
/// Hàm thuần merge banner lỗi địa chỉ Hub ↔ local (test được, không đụng DB/UI).
/// <para>
/// KHÔNG dùng thời gian ở bất kỳ đâu trong đường quyết định. Mốc thời gian đến từ các máy có đồng hồ độc
/// lập; hai lần vá trước đem mốc máy này so mốc máy kia đều đẻ ca hỏng nặng hơn lỗi định vá (banner kẹt
/// không gỡ được / Hub từ chối bấm X vĩnh viễn). Thay bằng hai thứ đo được:
/// </para>
/// <list type="bullet">
/// <item><c>rev</c> — bộ đếm của RIÊNG Hub, tăng sau mỗi lần ghi. Một đồng hồ duy nhất cho cả fleet.</item>
/// <item><c>cho_day</c> — cờ outbox: local có thay đổi chưa đẩy được. Hub KHÔNG được đè, phải đẩy lại.</item>
/// </list>
/// </summary>
public static class PickupAlertMerge
{
    /// <summary>
    /// Quyết định cho MỘT shop.
    /// </summary>
    /// <param name="localCoDong">Local đã có dòng cho shop này chưa.</param>
    /// <param name="localChoDay">Local có thay đổi tại chỗ (phát hiện lỗi / bấm X) chưa đẩy được lên Hub.</param>
    /// <param name="localHubRev">Số hiệu bản ghi Hub mà local đã nhận (0 = chưa nhận lần nào).</param>
    /// <param name="hubRev">Số hiệu bản ghi hiện tại trên Hub; &lt;= 0 = Hub không có dòng này.</param>
    public static MergePickupAlertAction QuyetDinh(
        bool localCoDong,
        bool localChoDay,
        long localHubRev,
        long hubRev)
    {
        // Ưu tiên TUYỆT ĐỐI cho thay đổi tại chỗ: đây là chỗ vá lỗ "Hub chết đúng lúc phát hiện lỗi thì
        // tombstone cũ trên Hub xoá mất cảnh báo". Cờ chỉ hạ khi Hub đã NHẬN được (DanhDauDaDay).
        if (localChoDay)
        {
            return MergePickupAlertAction.DayLenHub;
        }

        // Hub không có dòng (chỉ local có, đã đồng bộ xong) → không có gì để nhận.
        if (hubRev <= 0)
        {
            return MergePickupAlertAction.GiuNguyen;
        }

        // Máy này chưa biết gì về shop → nhận Hub (kể cả tombstone: ghi để lần sau khỏi hỏi lại).
        if (!localCoDong)
        {
            return MergePickupAlertAction.TheoHub;
        }

        return hubRev > localHubRev
            ? MergePickupAlertAction.TheoHub
            : MergePickupAlertAction.GiuNguyen;
    }
}
