namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Chọn API key KiotProxy "RẢNH nhất" từ pool key CHUNG cho một phiên sắp mở: key có SỐ PHIÊN đang giữ
/// nhỏ nhất (key chưa phiên nào giữ coi như 0 → ưu tiên "rảnh"); hòa → key xuất hiện TRƯỚC trong pool
/// (ổn định, không phụ thuộc thứ tự duyệt dictionary).
/// <para>
/// Hàm THUẦN (không side-effect, không đọc/ghi trạng thái ngoài) → test được độc lập. Bộ cấp phát thật
/// (<c>AccountSessionManager</c>) giữ trạng thái accountId→key và gọi hàm này TRONG lock, truyền vào
/// <paramref name="usage"/> đã tính từ các phiên đang giữ key.
/// </para>
/// </summary>
public static class KiotKeyPool
{
    /// <summary>
    /// Chọn key ít bận nhất trong <paramref name="pool"/> theo <paramref name="usage"/> (key → số phiên
    /// đang giữ). Pool rỗng/null → <c>null</c>. Key KHÔNG có trong <paramref name="usage"/> coi như 0.
    /// Key có trong <paramref name="usage"/> nhưng KHÔNG thuộc pool bị BỎ QUA (không ảnh hưởng lựa chọn).
    /// Hòa (cùng số phiên) → key đứng TRƯỚC trong pool thắng (so sánh chặt <c>&lt;</c> theo thứ tự pool).
    /// </summary>
    public static string? PickLeastUsed(IReadOnlyList<string> pool, IReadOnlyDictionary<string, int> usage)
    {
        if (pool is null || pool.Count == 0)
        {
            return null;
        }

        string? best = null;
        var bestUsage = int.MaxValue;
        foreach (var key in pool)
        {
            var used = usage != null && usage.TryGetValue(key, out var c) ? c : 0;
            // So sánh CHẶT: chỉ thay khi nhỏ hơn hẳn → key đứng trước trong pool giữ ngôi khi hòa (ổn định).
            if (used < bestUsage)
            {
                bestUsage = used;
                best = key;
            }
        }
        return best;
    }
}
