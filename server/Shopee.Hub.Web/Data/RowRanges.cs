namespace Shopee.Core.Scrape;

// COPY (không link) từ suite\Shopee.Core\Scrape\ScrapeProgressStore.cs — tách RowRange + RowRangeMath ra
// khỏi ScrapeProgressStore (store bám SuitePaths = windows). Giữ namespace Shopee.Core.Scrape để các file
// LINK (HubDtos/ICoordinationHub dùng RowRange) và HubDatabase (ledger merge) resolve nguyên vẹn.
// Bản SERVER giữ Merge/Normalize (ledger merge) + SubtractRows (mở lại dòng đã bỏ qua, POST /ledger/reopen-skipped);
// MaxRow/Complement (bản suite dùng cho scrape) đã lược bỏ vì server không gọi. NGUỒN SỰ THẬT vẫn ở suite\;
// nếu logic đổi bên đó, đồng bộ tay sang đây — hai bản LỆCH nhau thì cùng một nút "Cào lại dòng đã bỏ" cho ra
// hai vùng phủ khác nhau giữa Hub và máy client, mà lượt fold sau đó lấy bản Hub đè lên.

/// <summary>Một khoảng dòng [From..To] (đã bao gồm 2 đầu).</summary>
public sealed class RowRange
{
    public int From { get; set; }
    public int To { get; set; }
}

/// <summary>Toán khoảng dòng: gộp (merge) + chuẩn hoá (normalize) danh sách khoảng (không chồng/liền nhau).</summary>
public static class RowRangeMath
{
    /// <summary>Gộp thêm [from..to] vào danh sách, trả về danh sách đã gộp + sắp xếp (không chồng/ liền nhau).</summary>
    public static List<RowRange> Merge(IEnumerable<RowRange> existing, int from, int to)
    {
        var all = existing.Select(r => (r.From, r.To)).ToList();
        all.Add((from, to));
        return Normalize(all);
    }

    /// <summary>KHOÉT các dòng rời <paramref name="rows"/> khỏi vùng phủ: khoảng nào chứa một dòng bị khoét thì
    /// tách đôi (dòng ở giữa), cụt đầu/cụt đuôi, hoặc biến mất hẳn (khoảng 1 dòng). Dòng không thuộc khoảng nào
    /// thì bỏ qua. Kết quả đã Normalize.</summary>
    public static List<RowRange> SubtractRows(IReadOnlyList<RowRange> ranges, IReadOnlyCollection<int> rows)
    {
        var input = ranges.Where(r => r.To >= r.From).Select(r => (from: r.From, to: r.To));
        if (rows.Count == 0) return Normalize(input);

        // Cắt QUANH các dòng cần khoét, KHÔNG đi từng dòng của khoảng: bản per-row với một khoảng rác
        // To=int.MaxValue (server không validate RowRange client gửi) là vòng lặp chạy (gần như) vĩnh viễn —
        // ở HubDatabase nó nằm TRONG lock nên treo cả Hub. `start` dùng long vì row+1 tràn int ở biên.
        var cut = rows.Where(r => r > 0).Distinct().OrderBy(r => r).ToList();
        var kept = new List<(int from, int to)>();
        foreach (var (from, to) in input)
        {
            long start = from;
            foreach (var row in cut)
            {
                if (row < start) continue;
                if (row > to) break;
                if (row > start) kept.Add(((int)start, row - 1));
                start = (long)row + 1;
            }
            if (start <= to) kept.Add(((int)start, to));
        }
        return Normalize(kept);
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
}
