using System.Collections.Concurrent;

namespace UpdateProduct;

/// <summary>
/// Chống 2 lane (worker song song) cùng xử lý 1 sản phẩm. In-process (N runner trong 1 tiến trình suite):
/// dùng ConcurrentDictionary claim theo khóa (rowKey / editId). Lane phải TryClaim TRƯỚC khi mở/click;
/// thất bại = lane khác đang giữ → bỏ qua. Release khi cần thử lại; claim ở yên khi đã xong (done).
/// </summary>
internal sealed class ClaimStore
{
    private readonly ConcurrentDictionary<string, byte> _claimed = new(StringComparer.Ordinal);

    /// <summary>Giành quyền xử lý key. True nếu chưa ai giữ (lane này được phép xử lý).</summary>
    public bool TryClaim(string? key) => !string.IsNullOrEmpty(key) && _claimed.TryAdd(key!, 0);

    /// <summary>Trả lại key (để dòng được lane khác/loop sau thử lại) — dùng khi lỗi TẠM.</summary>
    public void Release(string? key) { if (!string.IsNullOrEmpty(key)) _claimed.TryRemove(key!, out _); }

    public bool IsClaimed(string? key) => !string.IsNullOrEmpty(key) && _claimed.ContainsKey(key!);
}
