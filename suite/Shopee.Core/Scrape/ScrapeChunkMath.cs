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

    /// <summary>Một mảng VÁ trong hàng đợi của allocator: khoảng dòng + số lần khoảng ấy KHÔNG tiến được
    /// (stall). Chạm ngưỡng stall là dòng đầu khoảng bị BỎ QUA, nên stall phải thuộc về đúng khoảng đã kẹt.</summary>
    public readonly record struct PatchSlice(int From, int To, int Stall);

    /// <summary>
    /// Cắt mảng vá cho 1 worker: mảng còn NHIỀU dòng (≥ 2×số worker) thì lấy ~1/workers số dòng, phần DƯ trả
    /// lại hàng để worker rảnh khác chạy song song. <c>Remainder = null</c> = không cắt (worker ôm cả mảng).
    /// <para>Phần DƯ nhận <c>Stall = 0</c>, KHÔNG thừa hưởng stall của mảng gốc: stall đếm "khoảng này bắt đầu
    /// tại dòng X mà không tiến được lần nào" — dòng nghi kẹt nằm ở ĐẦU mảng và đi theo lát đầu. Phần dư bắt
    /// đầu ở dòng KHÁC, chưa kẹt lần nào; cho nó thừa hưởng stall=2 (như bản cũ) thì chỉ cần trượt MỘT lần là
    /// chạm ngưỡng 3 → bỏ oan một dòng chưa từng được thử đủ.</para>
    /// </summary>
    public static (PatchSlice Piece, PatchSlice? Remainder) SplitPatch(PatchSlice patch, int workers)
    {
        var size = patch.To - patch.From + 1;
        if (workers <= 1 || size < 2 * workers) return (patch, null);
        var take = (int)Math.Ceiling((double)size / workers);
        var pieceTo = patch.From + take - 1;
        return (patch with { To = pieceTo }, new PatchSlice(pieceTo + 1, patch.To, 0));
    }
}
