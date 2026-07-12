using Shopee.Core.Infrastructure;

namespace Shopee.Core.Progress;

/// <summary>
/// Tiến độ per-SẢN-PHẨM của một (tài khoản BigSeller + sheet + thao tác) cho 2 workflow Import-to-store và
/// Update-product. <see cref="Done"/> khoá theo ITEM ID Shopee (khớp đơn vị của runner): import → giá trị null
/// (chỉ cần biết "đã import"); update → tên đã điền lúc save (để lượt sau đổi tên là tự vào lại diện update).
/// Dùng cho Resume (kill/dừng giữa chừng rồi chạy lại KHÔNG import trùng / KHÔNG update lại SP đã xong).
/// </summary>
public sealed class OpProgress
{
    public string AccountId { get; set; } = "";     // tài khoản BigSeller
    public string Sheet { get; set; } = "";
    public string Op { get; set; } = "";             // "import" | "update"
    public string AccountName { get; set; } = "";    // tên hiển thị (tiện cho UI/thống kê)

    /// <summary>itemId Shopee → tên đã điền lúc save (import: null). Có mặt = SP đã xử lý xong ở op này.</summary>
    public Dictionary<string, string?> Done { get; set; } = new(StringComparer.Ordinal);

    /// <summary>idle | running | stopped | completed.</summary>
    public string Status { get; set; } = "idle";
    public DateTimeOffset? LastRunAt { get; set; }
}

/// <summary>
/// Kho tiến độ per-SP dùng chung, lưu tại %AppData%\ShopeeSuite\shared\op-progress.json. Khoá theo
/// (AccountId BigSeller + Sheet + Op). Thread-safe; lưu nguyên tử (file tạm → move) — bền với kill giữa chừng.
/// Mẫu y hệt <see cref="Scrape.ScrapeProgressStore"/> nhưng đơn vị là ITEM ID Shopee thay vì khoảng dòng.
/// </summary>
public sealed class OpProgressStore
{
    private static readonly Lazy<OpProgressStore> _shared = new(() => new OpProgressStore());
    public static OpProgressStore Shared => _shared.Value;

    private static readonly string FilePath = Path.Combine(SuitePaths.ModuleDir("shared"), "op-progress.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly List<OpProgress> _items = [];

    public event Action? Changed;

    private OpProgressStore() => Load();

    private static bool KeyEq(OpProgress p, string accountId, string sheet, string op) =>
        p.AccountId == accountId
        && string.Equals(p.Sheet ?? "", sheet ?? "", StringComparison.OrdinalIgnoreCase)
        && string.Equals(p.Op ?? "", op ?? "", StringComparison.Ordinal);

    public void Load()
    {
        lock (_lock)
        {
            _items.Clear();
            try
            {
                if (File.Exists(FilePath))
                {
                    var list = JsonSerializer.Deserialize<List<OpProgress>>(File.ReadAllText(FilePath, Encoding.UTF8));
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

    private OpProgress GetOrCreateLocked(string accountId, string sheet, string op, string accountName)
    {
        var p = _items.FirstOrDefault(x => KeyEq(x, accountId, sheet, op));
        if (p is null)
        {
            p = new OpProgress { AccountId = accountId, Sheet = sheet ?? "", Op = op ?? "", AccountName = accountName };
            _items.Add(p);
        }
        if (!string.IsNullOrWhiteSpace(accountName)) p.AccountName = accountName;
        return p;
    }

    /// <summary>Bản sao (đọc) các itemId đã xong của (acc + sheet + op) — rỗng nếu chưa có tiến độ.</summary>
    public IReadOnlyDictionary<string, string?> GetDone(string accountId, string sheet, string op)
    {
        lock (_lock)
        {
            var p = _items.FirstOrDefault(x => KeyEq(x, accountId, sheet, op));
            return p is null
                ? new Dictionary<string, string?>(StringComparer.Ordinal)
                : new Dictionary<string, string?>(p.Done, StringComparer.Ordinal);
        }
    }

    /// <summary>Ghi nhận N itemId ĐÃ xong (import: value=null; update: value=tên đã điền). GHI FILE NGAY để bền
    /// với kill giữa chừng. Idempotent — ghi lại cùng itemId chỉ cập nhật giá trị.</summary>
    public void MarkDone(string accountId, string sheet, string op, IEnumerable<KeyValuePair<string, string?>> items)
    {
        var list = items as ICollection<KeyValuePair<string, string?>> ?? items.ToList();
        if (list.Count == 0) return;
        lock (_lock)
        {
            var p = GetOrCreateLocked(accountId, sheet, op, "");
            foreach (var kv in list)
                if (!string.IsNullOrEmpty(kv.Key)) p.Done[kv.Key] = kv.Value;
            SaveLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>Bắt đầu lượt CHẠY: đặt trạng thái running (GIỮ NGUYÊN Done — resume tiếp phần còn thiếu). Reset về
    /// đầu là việc riêng của <see cref="Clear"/>.</summary>
    public void BeginRun(string accountId, string sheet, string op, string accountName)
    {
        lock (_lock)
        {
            var p = GetOrCreateLocked(accountId, sheet, op, accountName);
            p.Status = "running";
            p.LastRunAt = DateTimeOffset.Now;
            SaveLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>Kết thúc lượt chạy: completed (chạy trọn) hoặc stopped (bị hủy giữa chừng). GIỮ NGUYÊN Done.</summary>
    public void FinishRun(string accountId, string sheet, string op, bool completed)
    {
        lock (_lock)
        {
            var p = _items.FirstOrDefault(x => KeyEq(x, accountId, sheet, op));
            if (p is null) return;
            p.Status = completed ? "completed" : "stopped";
            SaveLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>Xoá hẳn tiến độ của 1 (acc + sheet + op) — cho nút "Chạy lại từ đầu" (làm lại toàn bộ SP).</summary>
    public void Clear(string accountId, string sheet, string op)
    {
        lock (_lock)
        {
            _items.RemoveAll(x => KeyEq(x, accountId, sheet, op));
            SaveLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>Danh sách gọn (acc, sheet, op, status) mọi key đang có — cho UI liệt kê tiến độ.</summary>
    public List<(string acc, string sheet, string op, string status)> Snapshot()
    {
        lock (_lock)
            return _items.Select(p => (p.AccountId, p.Sheet, p.Op, p.Status)).ToList();
    }
}
