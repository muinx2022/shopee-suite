using System.Diagnostics.CodeAnalysis;

namespace Shopee.Suite.Infrastructure;

/// <summary>
/// Sổ job theo TỪNG TÀI KHOẢN (key = Account.Id), thread-safe, dùng CHUNG cho Scrape lẫn Update.
///
/// Gói trọn BẤT BIẾN mà trước đây mỗi nơi tự nhớ (dedup theo tk + add/remove dưới CÙNG 1 lock) vào một chỗ,
/// để lớp bug "rò job" (set state trước khi vào try/finally) không thể tái phát. Quy ước dùng:
/// <code>if (reg.TryAdd(id, job)) { try { …chạy… } finally { reg.Remove(id); } }</code>
/// — Remove LUÔN nằm trong finally.
///
/// <para>CHỈ là kho lưu thuần (không bắn event): refresh UI là việc của caller, đặt đúng chỗ trong try/finally
/// để giữ thứ tự dọn dẹp — registry không xen vào.</para>
/// <para><b>_sealed</b>: khi sổ rỗng + coordinator "chốt" (<see cref="SnapshotOrSeal"/>) thì chặn add mới —
/// thay cờ Finalizing cũ của Scrape.</para>
/// </summary>
internal sealed class PerAccountJobRegistry<TJob>
{
    private readonly object _lock = new();
    private readonly Dictionary<string, TJob> _jobs = new(StringComparer.Ordinal);
    private bool _sealed;

    public int Count { get { lock (_lock) return _jobs.Count; } }

    public bool Contains(string id) { lock (_lock) return _jobs.ContainsKey(id); }

    public bool TryGet(string id, [MaybeNullWhen(false)] out TJob job) { lock (_lock) return _jobs.TryGetValue(id, out job); }

    /// <summary>Thêm job nếu tk CHƯA có job nào VÀ sổ chưa bị chốt. true = đã thêm.</summary>
    public bool TryAdd(string id, TJob job) => TryAdd(id, () => job);

    /// <summary>
    /// Như <see cref="TryAdd(string,TJob)"/> nhưng job do <paramref name="factory"/> tạo, CHẠY DƯỚI lock — dùng khi
    /// factory phóng luôn Task nền của job (Scrape): Task có gọi <see cref="Remove"/> cũng phải chờ lock →
    /// đảm bảo "add trước, remove sau" như cài đặt cũ.
    /// </summary>
    public bool TryAdd(string id, Func<TJob> factory)
    {
        lock (_lock)
        {
            if (_sealed || _jobs.ContainsKey(id)) return false;
            _jobs[id] = factory();
            return true;
        }
    }

    public void Remove(string id) { lock (_lock) _jobs.Remove(id); }

    /// <summary>Sửa 1 job tại chỗ DƯỚI lock (vd gán JobHandle.Runner an toàn vs các snapshot khác). false nếu không thấy.</summary>
    public bool TryUpdate(string id, Action<TJob> mutate)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out var job)) return false;
            mutate(job);
            return true;
        }
    }

    /// <summary>Chiếu toàn bộ job sang T DƯỚI lock (đọc .Task/.Runner/.Seq an toàn) rồi xử lý ngoài lock.</summary>
    public List<T> SnapshotSelect<T>(Func<TJob, T> select)
    {
        lock (_lock) return _jobs.Values.Select(select).ToList();
    }

    /// <summary>
    /// Coordinator drain: nếu sổ RỖNG → CHỐT (chặn add mới) + trả mảng rỗng — ATOMIC; ngược lại chiếu job sang T.
    /// "Snapshot tasks HOẶC chốt" phải atomic để không job nào lọt vào sau khi coordinator thấy rỗng.
    /// </summary>
    public T[] SnapshotOrSeal<T>(Func<TJob, T> select)
    {
        lock (_lock)
        {
            if (_jobs.Count == 0) { _sealed = true; return Array.Empty<T>(); }
            return _jobs.Values.Select(select).ToArray();
        }
    }
}
