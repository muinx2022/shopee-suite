using Shopee.Core.Coordination;
using Shopee.Hub;

namespace Shopee.Hub.Web.Services;

/// <summary>1 phần link chia cho 1 máy (kết quả partition).</summary>
public sealed record SearchSlice(string MachineId, string Hostname, int Start, int Count, string Label, bool Busy, string StateText);

/// <summary>
/// Bảng điều phối Search đa máy (port logic FleetViewModel: RecomputePartition + RunSearchForClient). Parse
/// file link (txt/xlsx), chia ĐỀU cho các máy được tick, tạo assignment op "search" (ghim máy) với
/// <see cref="SearchJobPayload"/>, và xuất Excel gộp từ kho search_products của Hub.
/// </summary>
public sealed class SearchBoardService
{
    private readonly HubDatabase _db;
    public SearchBoardService(HubDatabase db) => _db = db;

    // ── Link file đã nạp — giữ ở SINGLETON để sống qua reconnect/điều hướng của circuit Blazor (client rớt
    // WebSocket qua tunnel KHÔNG mất danh sách link đã chọn). Chỉ 1 admin nên chia sẻ 1 bản là đủ. ──
    public IReadOnlyList<string> Links { get; private set; } = [];
    public string FileName { get; private set; } = "";
    private readonly HashSet<int> _selLinks = new();

    public void SetLinks(List<string> links, string fileName)
    {
        Links = links; FileName = fileName ?? "";
        _selLinks.Clear();
        for (var i = 0; i < links.Count; i++) _selLinks.Add(i);   // mặc định chọn HẾT link
    }

    public bool IsLinkSelected(int i) => _selLinks.Contains(i);
    public void SetLinkSelected(int i, bool on) { if (on) _selLinks.Add(i); else _selLinks.Remove(i); }
    public void SetAllLinks(bool on) { _selLinks.Clear(); if (on) for (var i = 0; i < Links.Count; i++) _selLinks.Add(i); }
    public int SelectedLinkCount => _selLinks.Count;
    /// <summary>Chỉ các link ĐANG được tick — nguồn chia cho client (KHÔNG chia toàn bộ file).</summary>
    public List<string> ActiveLinks() => Links.Where((_, i) => _selLinks.Contains(i)).ToList();

    /// <summary>Tách link từ nội dung file .txt (mỗi dòng 1 link; bỏ trống + trùng, giữ thứ tự).</summary>
    public static List<string> ParseTxt(string content)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var links = new List<string>();
        foreach (var raw in content.Split('\n'))
        {
            var s = raw.Trim().TrimEnd('\r');
            if (s.Length == 0) continue;
            if (seen.Add(s)) links.Add(s);
        }
        return links;
    }

    /// <summary>Tách link từ file .xlsx: đọc cột A mọi sheet (bỏ trống + trùng, giữ thứ tự). Tự parse ClosedXML
    /// (KHÔNG phụ thuộc module Search bám Windows).</summary>
    public static List<string> ParseXlsx(Stream stream)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var links = new List<string>();
        using var wb = new XLWorkbook(stream);
        foreach (var ws in wb.Worksheets)
        {
            var col = ws.Column(1).CellsUsed();
            foreach (var cell in col)
            {
                var s = cell.GetString().Trim();
                if (s.Length == 0) continue;
                if (!s.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                if (seen.Add(s)) links.Add(s);
            }
        }
        return links;
    }

    /// <summary>Chia ĐỀU <paramref name="linkCount"/> link cho các máy (theo thứ tự): mỗi máy nhận base hoặc
    /// base+1 (các máy đầu nhận phần dư). Port RecomputePartition. Trả về slice theo THỨ TỰ máy truyền vào.</summary>
    public List<SearchSlice> Partition(IReadOnlyList<MachinePresence> selectedMachines, int linkCount, FleetSnapshot fleet)
    {
        var k = selectedMachines.Count;
        var baseShare = k == 0 ? 0 : linkCount / k;
        var remainder = k == 0 ? 0 : linkCount % k;
        var result = new List<SearchSlice>();
        var cursor = 0;
        for (var i = 0; i < k; i++)
        {
            var share = baseShare + (i < remainder ? 1 : 0);
            var m = selectedMachines[i];
            var (busy, txt) = SearchState(fleet, m.MachineId);
            var label = share > 0 ? $"link {cursor + 1}–{cursor + share}" : "(không có link)";
            result.Add(new SearchSlice(m.MachineId, m.Hostname, cursor, share, label, busy, txt));
            cursor += share;
        }
        return result;
    }

    /// <summary>Trạng thái việc search của 1 máy (đang chạy / chờ / rảnh) từ assignments.</summary>
    public static (bool busy, string text) SearchState(FleetSnapshot f, string machineId)
    {
        var asn = f.Assignments.FirstOrDefault(a => a.Op == MachineRoles.Search && a.Status is "queued" or "running"
            && (a.ClaimedByMachineId == machineId || a.TargetMachineId == machineId));
        if (asn is { Status: "running" }) return (true, $"▶ đang chạy (link {asn.StartRow}–{asn.EndRow})");
        if (asn is { Status: "queued" }) return (true, $"⏱ chờ nhận (link {asn.StartRow}–{asn.EndRow})");
        // Việc search vừa 'failed' (thường: kho tài khoản Shopee của máy trống / đang chạy search khác) → hiện
        // lý do vài phút để người dùng biết vì sao "giao mà không chạy", thay vì lặng lẽ về "• rảnh".
        var failed = f.Assignments
            .Where(a => a.Op == MachineRoles.Search && a.Status == "failed"
                && (a.ClaimedByMachineId == machineId || a.TargetMachineId == machineId))
            .OrderByDescending(a => a.UpdatedAt).FirstOrDefault();
        if (failed is not null && (DateTimeOffset.Now - failed.UpdatedAt) < TimeSpan.FromMinutes(3))
            return (false, $"✘ {(string.IsNullOrWhiteSpace(failed.LastError) ? "lỗi khi chạy" : failed.LastError)}");
        return (false, "• rảnh");
    }

    /// <summary>Giao đúng phần link của 1 máy (ghim, op "search"). Port RunSearchForClient. links = TẤT CẢ link
    /// đã chọn; slice cắt [start, start+count).</summary>
    public void Dispatch(SearchSlice slice, IReadOnlyList<string> allLinks, string sourceFile, int accountsPerClient, int lanes, string? region)
    {
        if (slice.Count <= 0) return;
        var end = slice.Start + slice.Count;               // 0-based, [start, end)
        if (slice.Start < 0 || end > allLinks.Count) return;
        var links = allLinks.Skip(slice.Start).Take(slice.Count).ToList();
        var payload = new SearchJobPayload
        {
            Links = links, AccountsPerClient = Math.Max(1, accountsPerClient),
            Lanes = Math.Max(1, lanes), Region = region, SourceFile = sourceFile,
        };
        _db.CreateAssignment(new CreateAssignmentRequest(
            "", $"{slice.MachineId}:{slice.Start}", sourceFile, MachineRoles.Search, slice.MachineId, true,
            slice.Start + 1, end, JsonSerializer.Serialize(payload)));
    }

    /// <summary>Việc search đang mở (queued/running) của 1 máy — để biết có gì mà Dừng.</summary>
    public static Assignment? OpenSearchAssignment(FleetSnapshot f, string machineId)
        => f.Assignments.FirstOrDefault(a => a.Op == MachineRoles.Search && a.Status is "queued" or "running"
            && (a.ClaimedByMachineId == machineId || a.TargetMachineId == machineId));

    /// <summary>Dừng việc search đang mở của 1 máy: huỷ assignment → client thấy 'canceled' ở nhịp poll (≤12s)
    /// → StopLocal → dừng cào. Trả true nếu có việc để huỷ.</summary>
    public bool StopMachine(FleetSnapshot f, string machineId)
    {
        var a = OpenSearchAssignment(f, machineId);
        if (a is null) return false;
        _db.CancelAssignment(a.Id);
        return true;
    }

    /// <summary>Dừng MỌI việc search đang mở (mọi máy). Trả số việc đã huỷ.</summary>
    public int StopAllSearch(FleetSnapshot f)
    {
        var n = 0;
        foreach (var a in f.Assignments.Where(a => a.Op == MachineRoles.Search && a.Status is "queued" or "running"))
        { _db.CancelAssignment(a.Id); n++; }
        return n;
    }

    // ── Kho gộp ──
    public int MergedCount() => _db.SearchProductCount();
    public void ClearMerged() => _db.ClearSearchProducts();

    /// <summary>Số SP kho gộp theo từng máy (kết quả search của từng client).</summary>
    public List<(string MachineId, int Count)> ResultCountByMachine() => _db.SearchProductCountByMachine();

    /// <summary>1 dòng sản phẩm gộp kèm máy đã đẩy.</summary>
    public sealed record SearchResultRow(string MachineId, long ItemId, string Name, decimal Price, int Sold,
        double Rating, string Category, string ShopName, string Location, string Link);

    /// <summary>Bảng sản phẩm gộp (giới hạn <paramref name="limit"/>) kèm máy đã đẩy — hiện kết quả từng client.</summary>
    public List<SearchResultRow> Results(int limit)
    {
        var rows = new List<SearchResultRow>();
        foreach (var (mid, _, json) in _db.AllSearchProductRows())
        {
            ProductResult? p = null;
            try { p = JsonSerializer.Deserialize<ProductResult>(json); } catch { }
            if (p is null || p.ItemId == 0) continue;
            rows.Add(new SearchResultRow(mid, p.ItemId, p.Name, p.PriceVnd, p.MonthlySold, p.Rating,
                p.Category, p.ShopName, p.ShopLocation, p.Link));
            if (rows.Count >= limit) break;
        }
        return rows;
    }

    /// <summary>Gộp toàn bộ blob JSON sản phẩm ở Hub → deserialize + dedup theo ItemId. Lọc theo giá/đã bán/
    /// danh mục (0/null = không lọc), rồi xuất Excel (1 sheet/danh mục) vào <paramref name="outputDir"/>.
    /// Trả đường dẫn file. Dùng ExcelExporter dùng chung với module Search.</summary>
    public string ExportMerged(string outputDir, decimal minPrice, decimal maxPrice, int minSold, IReadOnlyCollection<string>? categories)
    {
        var byId = new Dictionary<long, ProductResult>();
        foreach (var json in _db.AllSearchProductJson())
        {
            ProductResult? p = null;
            try { p = JsonSerializer.Deserialize<ProductResult>(json); } catch { }
            if (p is null || p.ItemId == 0) continue;
            byId[p.ItemId] = p;
        }
        IEnumerable<ProductResult> q = byId.Values;
        if (minPrice > 0) q = q.Where(p => p.PriceVnd >= minPrice);
        if (maxPrice > 0) q = q.Where(p => p.PriceVnd <= maxPrice);
        if (minSold > 0) q = q.Where(p => p.MonthlySold >= minSold);
        if (categories is { Count: > 0 })
            q = q.Where(p => categories.Contains(string.IsNullOrWhiteSpace(p.Category) ? "Khác" : p.Category.Trim()));

        var list = q.ToList();
        var fileName = $"tonghop-hub_{list.Count}sp_{DateTime.UtcNow:yyyyMMdd_HHmmss}.xlsx";
        return ExcelExporter.Export(list, outputDir, fileName);
    }

    /// <summary>Danh sách danh mục có trong kho gộp (cho bộ lọc export).</summary>
    public List<string> MergedCategories()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var json in _db.AllSearchProductJson())
        {
            try
            {
                var p = JsonSerializer.Deserialize<ProductResult>(json);
                if (p is not null) set.Add(string.IsNullOrWhiteSpace(p.Category) ? "Khác" : p.Category.Trim());
            }
            catch { }
        }
        return set.OrderBy(x => x).ToList();
    }
}
