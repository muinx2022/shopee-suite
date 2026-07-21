namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Hàm thuần đọc dữ liệu từ dashboard (to-do box) của Shopee Seller Centre. Tách khỏi Playwright để
/// test được: chỉ nhận vào text đã lấy từ DOM và trả về giá trị đã phân tích.
/// </summary>
public static class ShopeeDashboard
{
    /// <summary>
    /// Đọc số đơn từ text ô <c>item-title</c> (vd "0", "12", "1.234", "99+"). Bỏ mọi ký tự không phải
    /// chữ số (dấu chấm/phẩy ngăn nghìn, khoảng trắng, dấu "+", ...) rồi parse phần số còn lại.
    /// Rỗng/null/không có chữ số → <c>null</c>. ("99+" → 99; "1.234" → 1234). Nếu chuỗi số quá lớn để
    /// chứa trong <see cref="int"/> → <c>null</c> (không ném).
    /// </summary>
    public static int? ParseToShipCount(string? itemTitleText)
    {
        if (string.IsNullOrWhiteSpace(itemTitleText))
        {
            return null;
        }

        // Chỉ giữ lại các chữ số (loại dấu ngăn nghìn, khoảng trắng, "+", ký tự khác).
        var digits = new string(itemTitleText.Where(char.IsDigit).ToArray());
        if (digits.Length == 0)
        {
            return null;
        }

        // Parse phần số; quá lớn (tràn int) → null thay vì ném.
        return int.TryParse(digits, out var n) ? n : null;
    }
}
