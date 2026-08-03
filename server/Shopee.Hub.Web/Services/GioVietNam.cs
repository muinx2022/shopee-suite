namespace Shopee.Hub.Web.Services;

/// <summary>
/// Quy đổi mốc thời gian sang GIỜ VIỆT NAM để HIỂN THỊ (tin nhắn webhook, bảng trên web).
/// <para>
/// Hub chạy trên VM Ubuntu đặt giờ UTC, nên <c>DateTime.Now</c> / <c>DateTimeOffset.Now</c> ở đây là giờ UTC —
/// mọi mốc in ra cho người đọc đều LỆCH 7 TIẾNG. Mọi chỗ cần chuỗi giờ cho người đọc phải đi qua đây, KHÔNG
/// dùng <c>.Now</c> / <c>.LocalDateTime</c> nữa (giờ máy chủ không phải giờ người dùng).
/// </para>
/// Dùng ID IANA <c>Asia/Ho_Chi_Minh</c>: .NET 8 hiểu ID này trên CẢ Linux lẫn Windows (ICU), nên chạy được cả
/// khi dev trên máy Windows. Thiếu dữ liệu múi giờ (ảnh container tối giản) → lùi về UTC+7 cố định thay vì ném:
/// Việt Nam không có DST nên offset cố định là ĐÚNG, chỉ kém ở chỗ không tự theo luật nếu sau này đổi.
/// </summary>
public static class GioVietNam
{
    private const string IanaId = "Asia/Ho_Chi_Minh";
    private static readonly TimeSpan OffsetDuPhong = TimeSpan.FromHours(7);

    private static readonly TimeZoneInfo? Zone = TimZoneOrNull();

    private static TimeZoneInfo? TimZoneOrNull()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(IanaId); }
        catch (TimeZoneNotFoundException) { return null; }
        catch (InvalidTimeZoneException) { return null; }
    }

    /// <summary>Mốc <paramref name="at"/> quy về giờ Việt Nam.</summary>
    public static DateTimeOffset Doi(DateTimeOffset at)
        => Zone is null ? at.ToOffset(OffsetDuPhong) : TimeZoneInfo.ConvertTime(at, Zone);

    /// <summary>BÂY GIỜ theo giờ Việt Nam — thay cho <c>DateTime.Now</c> ở mọi chỗ dựng tin cho người đọc.</summary>
    public static DateTime BayGio() => Doi(DateTimeOffset.UtcNow).DateTime;

    /// <summary>Khoảng UTC nửa mở <c>[00:00 hôm nay, 00:00 ngày mai)</c> chứa
    /// <paramref name="at"/>, với ngày được xác định theo múi giờ Việt Nam. Dùng khoảng UTC này khi lọc các
    /// cột thời gian lưu trên hub; không dựa vào múi giờ của máy chủ.</summary>
    public static (DateTimeOffset FromUtc, DateTimeOffset ToUtcExclusive) KhoangNgayUtc(DateTimeOffset at)
    {
        var local = Doi(at);
        var fromLocal = new DateTimeOffset(local.Date, local.Offset);
        return (fromLocal.ToUniversalTime(), fromLocal.AddDays(1).ToUniversalTime());
    }

    /// <summary><paramref name="at"/> theo giờ Việt Nam, định dạng <paramref name="format"/>. Mốc rỗng
    /// (<c>default</c>/<see cref="DateTimeOffset.MinValue"/>) → chuỗi rỗng (bảng hiện "—" thay vì năm 0001).</summary>
    public static string DinhDang(DateTimeOffset at, string format)
        => at == default || at == DateTimeOffset.MinValue ? "" : Doi(at).ToString(format);
}
