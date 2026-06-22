using Shopee.Core.Infrastructure;

namespace Shopee.Core.Scrape;

/// <summary>Một khoảng dòng [From..To] (đã bao gồm 2 đầu).</summary>
public sealed class RowRange
{
    public int From { get; set; }
    public int To { get; set; }
}

/// <summary>
/// Tiến độ scrape của một (tài khoản BigSeller + sheet): các dòng ĐÃ cào xong (gộp khoảng), danh sách
/// tài khoản Shopee đang được "đặt chỗ" cho BigSeller này, dòng cao nhất đã tới, và trạng thái lượt
/// chạy gần nhất. Dùng cho Resume (chạy tiếp đúng phần thiếu) + Thống kê + giữ/nhả tk Shopee.
/// </summary>
public sealed class ScrapeProgress
{
    public string AccountId { get; set; } = "";      // tài khoản BigSeller
    public string Sheet { get; set; } = "";
    public string AccountName { get; set; } = "";    // tên hiển thị (tiện cho thống kê)

    /// <summary>Các khoảng dòng đã cào xong, đã gộp + sắp xếp.</summary>
    public List<RowRange> Completed { get; set; } = [];

    /// <summary>Id các tk Shopee đang giữ cho BigSeller này (chỉ nhả khi chạy XONG hoặc nhả tay).</summary>
    public List<string> ReservedShopeeAccountIds { get; set; } = [];

    public int LastRowReached { get; set; }
    public int TotalRowsAtLastRun { get; set; }
    public int ShopeeAccountCount { get; set; }

    /// <summary>idle | running | stopped | completed.</summary>
    public string Status { get; set; } = "idle";
    public DateTimeOffset? LastRunAt { get; set; }
}

/// <summary>
/// Kho tiến độ scrape dùng chung, lưu tại %AppData%\ShopeeSuite\shared\scrape-progress.json.
/// Khoá theo (AccountId BigSeller + Sheet). Thread-safe; lưu nguyên tử (file tạm → move).
/// </summary>
public sealed class ScrapeProgressStore
{
    private static readonly Lazy<ScrapeProgressStore> _shared = new(() => new ScrapeProgressStore());
    public static ScrapeProgressStore Shared => _shared.Value;

    private static readonly string FilePath = Path.Combine(SuitePaths.ModuleDir("shared"), "scrape-progress.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly List<ScrapeProgress> _items = [];

    public event Action? Changed;

    private ScrapeProgressStore() => Load();

    private static bool KeyEq(ScrapeProgress p, string accountId, string sheet) =>
        p.AccountId == accountId && string.Equals(p.Sheet ?? "", sheet ?? "", StringComparison.OrdinalIgnoreCase);

    public void Load()
    {
        lock (_lock)
        {
            _items.Clear();
            try
            {
                if (File.Exists(FilePath))
                {
                    var list = JsonSerializer.Deserialize<List<ScrapeProgress>>(File.ReadAllText(FilePath, Encoding.UTF8));
                    if (list is not null) _items.AddRange(list);
                }
            }
            catch { }
        }
    }

    private void SaveLocked()
    {
        try
        {
            var json = JsonSerializer.Serialize(_items, JsonOpts);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { }
    }

    private void Save()
    {
        lock (_lock) SaveLocked();
        Changed?.Invoke();
    }

    /// <summary>Lấy bản sao tiến độ (đọc) — null nếu chưa có.</summary>
    public ScrapeProgress? Find(string accountId, string sheet)
    {
        lock (_lock)
        {
            var p = _items.FirstOrDefault(x => KeyEq(x, accountId, sheet));
            return p is null ? null : Clone(p);
        }
    }

    /// <summary>Toàn bộ tiến độ (đọc) — cho màn Thống kê.</summary>
    public IReadOnlyList<ScrapeProgress> All()
    {
        lock (_lock) return _items.Select(Clone).ToList();
    }

    /// <summary>Tất cả Id tk Shopee đang bị giữ bởi các BigSeller CHƯA hoàn thành (loại theo cặp đang resume).</summary>
    public HashSet<string> ReservedByOthers(string exceptAccountId, string exceptSheet)
    {
        lock (_lock)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in _items)
            {
                if (KeyEq(p, exceptAccountId, exceptSheet)) continue;
                if (string.Equals(p.Status, "completed", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var id in p.ReservedShopeeAccountIds) set.Add(id);
            }
            return set;
        }
    }

    private ScrapeProgress GetOrCreateLocked(string accountId, string sheet, string accountName)
    {
        var p = _items.FirstOrDefault(x => KeyEq(x, accountId, sheet));
        if (p is null)
        {
            p = new ScrapeProgress { AccountId = accountId, Sheet = sheet ?? "", AccountName = accountName };
            _items.Add(p);
        }
        if (!string.IsNullOrWhiteSpace(accountName)) p.AccountName = accountName;
        return p;
    }

    /// <summary>Bắt đầu lượt CHẠY (reset): xoá toàn bộ tiến độ + đặt chỗ tk, đặt trạng thái running.</summary>
    public void BeginFresh(string accountId, string sheet, string accountName, IEnumerable<string> shopeeAccountIds, int totalRows)
    {
        lock (_lock)
        {
            var p = GetOrCreateLocked(accountId, sheet, accountName);
            p.Completed.Clear();
            p.LastRowReached = 0;
            p.ReservedShopeeAccountIds = shopeeAccountIds.Distinct(StringComparer.Ordinal).ToList();
            p.ShopeeAccountCount = p.ReservedShopeeAccountIds.Count;
            p.TotalRowsAtLastRun = totalRows;
            p.Status = "running";
            p.LastRunAt = DateTimeOffset.Now;
            SaveLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>Bắt đầu lượt TIẾP TỤC (resume): giữ tiến độ cũ, cập nhật đặt chỗ tk + trạng thái running.</summary>
    public void BeginResume(string accountId, string sheet, string accountName, IEnumerable<string> shopeeAccountIds, int totalRows)
    {
        lock (_lock)
        {
            var p = GetOrCreateLocked(accountId, sheet, accountName);
            p.ReservedShopeeAccountIds = shopeeAccountIds.Distinct(StringComparer.Ordinal).ToList();
            p.ShopeeAccountCount = Math.Max(p.ShopeeAccountCount, p.ReservedShopeeAccountIds.Count);
            p.TotalRowsAtLastRun = totalRows;
            p.Status = "running";
            p.LastRunAt = DateTimeOffset.Now;
            SaveLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>Ghi nhận một khoảng dòng đã cào xong (gộp vào Completed). Dùng khi runner báo tiến độ.</summary>
    public void MarkCompleted(string accountId, string sheet, int from, int to)
    {
        if (to < from) return;
        lock (_lock)
        {
            var p = GetOrCreateLocked(accountId, sheet, "");
            p.Completed = RowRangeMath.Merge(p.Completed, from, to);
            p.LastRowReached = Math.Max(p.LastRowReached, RowRangeMath.MaxRow(p.Completed));
            SaveLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>Kết thúc lượt chạy: nếu đã xong hết [start..total] → completed + nhả hết tk; ngược lại stopped (giữ tk).</summary>
    public void FinishRun(string accountId, string sheet, int start, int total)
    {
        lock (_lock)
        {
            var p = _items.FirstOrDefault(x => KeyEq(x, accountId, sheet));
            if (p is null) return;
            var remaining = RowRangeMath.Complement(p.Completed, start, total);
            if (remaining.Count == 0 && total >= start)
            {
                p.Status = "completed";
                p.ReservedShopeeAccountIds.Clear();   // hoàn thành → nhả hết tk về kho
            }
            else
            {
                p.Status = "stopped";                  // còn dở → GIỮ tk cho lần resume
            }
            SaveLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>Các khoảng dòng CÒN THIẾU trong [start..total] (để resume chạy đúng phần chưa xong).</summary>
    public List<(int from, int to)> RemainingSegments(string accountId, string sheet, int start, int total)
    {
        lock (_lock)
        {
            var p = _items.FirstOrDefault(x => KeyEq(x, accountId, sheet));
            var completed = p?.Completed ?? [];
            return RowRangeMath.Complement(completed, start, total);
        }
    }

    /// <summary>Thêm 1 tk Shopee vào đặt-chỗ của 1 BigSeller (khi mượn bù lúc chạy — tk cũ dính captcha).</summary>
    public void AddReservedShopeeAccount(string accountId, string sheet, string shopeeAccountId)
    {
        lock (_lock)
        {
            var p = _items.FirstOrDefault(x => KeyEq(x, accountId, sheet));
            if (p is null || p.ReservedShopeeAccountIds.Contains(shopeeAccountId, StringComparer.Ordinal)) return;
            p.ReservedShopeeAccountIds.Add(shopeeAccountId);
            p.ShopeeAccountCount = Math.Max(p.ShopeeAccountCount, p.ReservedShopeeAccountIds.Count);
            SaveLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>Nhả thủ công 1 tk Shopee khỏi đặt chỗ của 1 BigSeller (khi treo/không phụ thuộc tiến trình).</summary>
    public void ReleaseShopeeAccount(string accountId, string sheet, string shopeeAccountId)
    {
        lock (_lock)
        {
            var p = _items.FirstOrDefault(x => KeyEq(x, accountId, sheet));
            if (p is null) return;
            p.ReservedShopeeAccountIds.RemoveAll(id => string.Equals(id, shopeeAccountId, StringComparison.Ordinal));
            SaveLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>Nhả TẤT CẢ tk Shopee đang giữ của 1 BigSeller (nhả tay toàn bộ).</summary>
    public void ReleaseAllShopeeAccounts(string accountId, string sheet)
    {
        lock (_lock)
        {
            var p = _items.FirstOrDefault(x => KeyEq(x, accountId, sheet));
            if (p is null) return;
            p.ReservedShopeeAccountIds.Clear();
            SaveLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>Xoá hẳn tiến độ của 1 (BigSeller + sheet).</summary>
    public void Clear(string accountId, string sheet)
    {
        lock (_lock)
        {
            _items.RemoveAll(x => KeyEq(x, accountId, sheet));
            SaveLocked();
        }
        Changed?.Invoke();
    }

    private static ScrapeProgress Clone(ScrapeProgress p) => new()
    {
        AccountId = p.AccountId,
        Sheet = p.Sheet,
        AccountName = p.AccountName,
        Completed = p.Completed.Select(r => new RowRange { From = r.From, To = r.To }).ToList(),
        ReservedShopeeAccountIds = [.. p.ReservedShopeeAccountIds],
        LastRowReached = p.LastRowReached,
        TotalRowsAtLastRun = p.TotalRowsAtLastRun,
        ShopeeAccountCount = p.ShopeeAccountCount,
        Status = p.Status,
        LastRunAt = p.LastRunAt,
    };
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
