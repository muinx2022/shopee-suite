namespace Shopee.Core.Scrape;

// COPY (không link) từ suite\Shopee.Core\Scrape\ScrapeProgressStore.cs — tách RowRange + RowRangeMath ra
// khỏi ScrapeProgressStore (store bám SuitePaths = windows). Giữ namespace Shopee.Core.Scrape để các file
// LINK (HubDtos/ICoordinationHub dùng RowRange) và HubDatabase (ledger merge) resolve nguyên vẹn.
// NGUỒN SỰ THẬT vẫn ở suite\; nếu logic đổi bên đó, đồng bộ tay sang đây (chỉ 2 kiểu thuần toán học này).

/// <summary>Một khoảng dòng [From..To] (đã bao gồm 2 đầu).</summary>
public sealed class RowRange
{
    public int From { get; set; }
    public int To { get; set; }
}

/// <summary>Toán khoảng dòng: gộp (merge) + lấy phần bù (complement) trong một đoạn [start..total].</summary>
public static class RowRangeMath
{
    public static int MaxRow(IReadOnlyList<RowRange> ranges) =>
        ranges.Count == 0 ? 0 : ranges.Max(r => r.To);

    /// <summary>Gộp thêm [from..to] vào danh sách, trả về danh sách đã gộp + sắp xếp (không chồng/ liền nhau).</summary>
    public static List<RowRange> Merge(IEnumerable<RowRange> existing, int from, int to)
    {
        var all = existing.Select(r => (r.From, r.To)).ToList();
        all.Add((from, to));
        return Normalize(all);
    }

    public static List<RowRange> Normalize(IEnumerable<(int from, int to)> input)
    {
        var sorted = input.Where(p => p.to >= p.from).OrderBy(p => p.from).ThenBy(p => p.to).ToList();
        var result = new List<RowRange>();
        foreach (var (from, to) in sorted)
        {
            if (result.Count > 0 && from <= result[^1].To + 1)
                result[^1].To = Math.Max(result[^1].To, to);   // chồng hoặc liền kề → nối
            else
                result.Add(new RowRange { From = from, To = to });
        }
        return result;
    }

    /// <summary>Phần bù: các khoảng trong [start..total] KHÔNG nằm trong <paramref name="completed"/>.</summary>
    public static List<(int from, int to)> Complement(IReadOnlyList<RowRange> completed, int start, int total)
    {
        var gaps = new List<(int, int)>();
        if (total < start) return gaps;

        var sorted = completed.Where(r => r.To >= r.From).OrderBy(r => r.From).ToList();
        var cursor = start;
        foreach (var r in sorted)
        {
            if (r.To < start) continue;
            var lo = Math.Max(r.From, start);
            if (lo > cursor) gaps.Add((cursor, Math.Min(lo - 1, total)));
            cursor = Math.Max(cursor, Math.Min(r.To, total) + 1);
            if (cursor > total) break;
        }
        if (cursor <= total) gaps.Add((cursor, total));
        return gaps;
    }
}

/// <summary>
/// COPY POCO từ suite\Shopee.Core\Scrape\ScrapeTargetConfigStore.cs (bỏ phần store bám SuitePaths).
/// Cấu hình scrape RIÊNG của một tài khoản BigSeller — web hub sửa qua file store config/scrape-targets.json.
/// </summary>
public sealed class ScrapeTargetConfig
{
    public string AccountId { get; set; } = "";       // tài khoản BigSeller
    public string? SelectedShopId { get; set; }        // shop (↔ sheet) đang chọn
    public bool IsSelected { get; set; }               // có tick để chạy không

    public int StartRow { get; set; } = 2;
    public int EndRow { get; set; }
    public int RowsPerAccount { get; set; } = 60;
    public int MaxProcess { get; set; } = 2;
    /// <summary>Số tk Shopee được "đóng khung" cố định cho tk BigSeller này.</summary>
    public int FrameSize { get; set; } = 10;
}
