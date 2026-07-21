namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Tách văn bản nhiều dòng thành danh sách API key KiotProxy (mỗi dòng một key).
/// Trim đầu/cuối, bỏ dòng trống, loại trùng (giữ thứ tự xuất hiện đầu tiên).
/// </summary>
public static class KiotProxyKeyParser
{
    public static List<string> Parse(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (var raw in lines)
        {
            var key = raw.Trim();
            if (key.Length > 0 && seen.Add(key))
            {
                result.Add(key);
            }
        }
        return result;
    }

    /// <summary>Ghép danh sách key thành văn bản chuẩn hóa (mỗi dòng một key, ngăn bằng '\n').</summary>
    public static string Join(IEnumerable<string> keys) => string.Join("\n", keys);
}
