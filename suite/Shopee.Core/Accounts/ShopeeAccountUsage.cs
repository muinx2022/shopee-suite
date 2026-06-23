namespace Shopee.Core.Accounts;

/// <summary>
/// Theo dõi tình trạng dùng tk Shopee theo THỜI GIAN CHẠY (runtime), dùng chung cho Scrape + Search.
/// CHỈ có ý nghĩa khi đang có ít nhất 1 lượt chạy; không chạy gì → mọi tk coi như "Chưa dùng".
/// 3 trạng thái: "Đang dùng" (đang mở/chiếm), "Đã dùng" (đã dùng trong lượt này rồi nhả), "Chưa dùng".
/// </summary>
public sealed class ShopeeAccountUsage
{
    public static ShopeeAccountUsage Shared { get; } = new();

    private readonly object _lock = new();
    private int _activeRuns;                                   // số lượt chạy (scrape/search) đang mở
    private readonly HashSet<string> _inUse = new(StringComparer.Ordinal);
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);   // đã dùng ít nhất 1 lần trong giai đoạn đang chạy

    /// <summary>Phát khi trạng thái đổi → UI làm mới cột "Tình trạng".</summary>
    public event Action? Changed;

    /// <summary>Có lượt chạy nào đang mở không (quyết định có hiển thị trạng thái hay coi tất cả "Chưa dùng").</summary>
    public bool Active { get { lock (_lock) return _activeRuns > 0; } }

    /// <summary>Bắt đầu 1 lượt chạy (Scrape/Search). Gọi đôi với <see cref="EndRun"/>.</summary>
    public void BeginRun()
    {
        lock (_lock) _activeRuns++;
        Changed?.Invoke();
    }

    /// <summary>Kết thúc 1 lượt chạy. Khi không còn lượt nào → xoá hết dấu (mọi tk về "Chưa dùng").</summary>
    public void EndRun()
    {
        lock (_lock)
        {
            if (--_activeRuns <= 0)
            {
                _activeRuns = 0;
                _inUse.Clear();
                _used.Clear();
            }
        }
        Changed?.Invoke();
    }

    public void MarkInUse(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        lock (_lock) { _inUse.Add(id); _used.Add(id); }
        Changed?.Invoke();
    }

    public void MarkInUse(IEnumerable<string> ids)
    {
        lock (_lock) foreach (var id in ids) if (!string.IsNullOrEmpty(id)) { _inUse.Add(id); _used.Add(id); }
        Changed?.Invoke();
    }

    public void MarkReleased(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        lock (_lock) _inUse.Remove(id);
        Changed?.Invoke();
    }

    public void MarkReleased(IEnumerable<string> ids)
    {
        lock (_lock) foreach (var id in ids) _inUse.Remove(id);
        Changed?.Invoke();
    }

    /// <summary>Trạng thái hiển thị của 1 tk: "Đang dùng" | "Đã dùng" | "Chưa dùng".</summary>
    public string Status(string id)
    {
        lock (_lock)
        {
            if (_activeRuns <= 0) return "Chưa dùng";
            if (_inUse.Contains(id)) return "Đang dùng";
            if (_used.Contains(id)) return "Đã dùng";
            return "Chưa dùng";
        }
    }
}
