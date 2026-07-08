namespace UpdateProduct;

/// <summary>
/// Tiện ích xử lý chuỗi tên sản phẩm dùng chung trong module Update. Gộp về đây method THỰC SỰ trùng
/// giữa <c>BigSellerProductUpdateRunner</c> và <c>ProductNameRewriteRunner</c> (các helper text khác chỉ
/// dùng ở 1 nơi nên giữ nguyên tại chỗ).
/// </summary>
internal static class BigSellerText
{
    /// <summary>
    /// Cắt tên SP về tối đa <paramref name="maxLength"/> ký tự, ƯU TIÊN giữ SKU ở cuối (bỏ bớt từ ở thân).
    /// Fallback: 1 từ duy nhất dài hơn maxLength → cắt cứng thay vì trả "" (title rỗng làm hỏng sản phẩm).
    /// </summary>
    public static string TruncateProductNamePreservingSku(string? productName, string? sku, int maxLength)
    {
        var name = (productName ?? "").Trim();
        sku = (sku ?? "").Trim();
        if (name.Length <= maxLength) return name;

        if (!string.IsNullOrWhiteSpace(sku) && name.EndsWith(sku, StringComparison.Ordinal))
        {
            var body = name[..^sku.Length].Trim();
            var words = body.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            while (words.Count > 0)
            {
                var candidate = $"{string.Join(" ", words)} {sku}".Trim();
                if (candidate.Length <= maxLength) return candidate;
                words.RemoveAt(words.Count - 1);
            }
            return sku.Length <= maxLength ? sku : sku[..maxLength].Trim();
        }

        var allWords = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (allWords.Count > 0)
        {
            var candidate = string.Join(" ", allWords).Trim();
            if (candidate.Length <= maxLength) return candidate;
            allWords.RemoveAt(allWords.Count - 1);
        }
        return name[..Math.Min(maxLength, name.Length)].Trim();
    }
}
