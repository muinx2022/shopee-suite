namespace Shopee.Core.Scrape;

/// <summary>
/// Toán THUẦN của một khối dòng scrape (chunk) — tách khỏi engine để test gọi thẳng được.
/// Cùng chỗ với <see cref="RowRangeMath"/> (toán khoảng dòng) cho gọn một mối.
/// </summary>
public static class ScrapeChunkMath
{
    /// <summary>
    /// Dòng cuối ĐÃ cào xong của khối <c>[from..to]</c>, kẹp về khoảng hợp lệ <c>[from-1 .. to]</c>.
    /// Chưa xong dòng nào (null) → <c>from-1</c>.
    /// <para>QUAN TRỌNG — <paramref name="last"/> LỚN HƠN <paramref name="to"/> là giá trị RÁC, KHÔNG phải
    /// "xong cả khối": profile Brave dùng lại theo tk Shopee nên <c>runnerState.lastCompletedRow</c> của lượt
    /// TRƯỚC (vd 5000 của hôm qua) còn nguyên trong <c>Local Extension Settings</c>. Kẹp XUỐNG <c>to</c> như
    /// trước đây biến rác đó thành "đã xong tới dòng 12" khi khối 2–12 login fail ngay dòng đầu → cả khối bị
    /// ghi là đã cào mà không có dữ liệu. Nên trả <c>from-1</c> = coi như CHƯA làm gì (khối sẽ được vá lại).</para>
    /// </summary>
    public static int ClampLastDone(int? last, int from, int to)
    {
        var value = last ?? (from - 1);
        if (value > to) return from - 1;       // rác ngoài khối → chưa làm gì
        if (value < from - 1) return from - 1; // trước cả đầu khối → chưa làm gì
        return value;
    }
}
