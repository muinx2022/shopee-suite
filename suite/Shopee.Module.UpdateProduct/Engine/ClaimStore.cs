using System.Collections.Concurrent;

namespace UpdateProduct;

/// <summary>
/// Chống 2 lane (worker song song) cùng xử lý 1 sản phẩm. In-process (N runner trong 1 tiến trình suite).
/// HAI mảng tách bạch theo khóa (rowKey / editId):
///  • <c>_inProgress</c> (mảng 1) — id đang có worker NHẢY VÀO sửa. FAIL thì <see cref="Release"/> để worker khác
///    còn cơ hội thử lại (KHÔNG khóa oan SP: bug production — kho media đầy → "fail 2 lần" từng khóa vĩnh viễn SP
///    dù sau khi dọn kho lane khác sửa được).
///  • <c>_done</c> (mảng 2) — id đã sửa THÀNH CÔNG. Khóa VĨNH VIỄN trong lượt chạy (không ai mở lại), sống xuyên
///    lane-restart vì ClaimStore dùng chung.
/// Lane phải <see cref="TryClaim"/> TRƯỚC khi mở/click; thất bại = key đã done HOẶC lane khác đang giữ → bỏ qua.
/// <see cref="Release"/> = nhả cho worker khác thử (lỗi tạm / fail); <see cref="MarkDone"/> = thành công, chốt lại.
/// </summary>
internal sealed class ClaimStore
{
    private readonly ConcurrentDictionary<string, byte> _inProgress = new(StringComparer.Ordinal);   // mảng 1: đang có worker sửa — fail thì NHẢ
    private readonly ConcurrentDictionary<string, byte> _done = new(StringComparer.Ordinal);          // mảng 2: đã sửa THÀNH CÔNG — khóa vĩnh viễn trong lượt chạy

    /// <summary>Giành quyền xử lý key. True nếu key CHƯA done và chưa lane nào đang giữ (lane này được phép xử lý).</summary>
    public bool TryClaim(string? key) => !string.IsNullOrEmpty(key) && !_done.ContainsKey(key!) && _inProgress.TryAdd(key!, 0);

    /// <summary>Trả lại key đang-giữ (để lane khác/loop sau thử lại) — dùng khi lỗi TẠM hoặc lane bỏ cuộc (fail).</summary>
    public void Release(string? key) { if (!string.IsNullOrEmpty(key)) _inProgress.TryRemove(key!, out _); }

    /// <summary>Đánh dấu key đã sửa THÀNH CÔNG → khóa vĩnh viễn (không ai mở lại). Thứ tự add <c>_done</c> TRƯỚC rồi
    /// mới remove <c>_inProgress</c>: giữa 2 bước key nằm ở CẢ HAI mảng nên KHÔNG có khe hở cho lane khác claim.</summary>
    public void MarkDone(string? key)
    {
        if (string.IsNullOrEmpty(key)) return;
        _done.TryAdd(key!, 0);
        _inProgress.TryRemove(key!, out _);
    }

    public bool IsClaimed(string? key) => !string.IsNullOrEmpty(key) && (_inProgress.ContainsKey(key!) || _done.ContainsKey(key!));
}
