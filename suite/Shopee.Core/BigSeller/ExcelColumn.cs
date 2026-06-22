namespace Shopee.Core.BigSeller;

/// <summary>Chuyển đổi số cột Excel (1-based) ↔ chữ cột (A, B, …, AA). 0 / rỗng = "không dùng".</summary>
public static class ExcelColumn
{
    /// <summary>Số cột (1-based) → chữ cột (1→A, 27→AA). 0/âm → rỗng.</summary>
    public static string ToLetter(int col)
    {
        if (col <= 0) return "";
        var s = "";
        while (col > 0) { col--; s = (char)('A' + col % 26) + s; col /= 26; }
        return s;
    }

    /// <summary>Chữ cột Excel → số cột (1-based). Rỗng/không hợp lệ → 0 (không dùng).</summary>
    public static int FromLetter(string? letter)
    {
        if (string.IsNullOrWhiteSpace(letter)) return 0;
        var col = 0;
        foreach (var ch in letter.Trim().ToUpperInvariant())
        {
            if (ch is < 'A' or > 'Z') return 0;
            col = col * 26 + (ch - 'A' + 1);
        }
        return col;
    }
}
