using System.Globalization;
using System.Text;
using System.Text.Json;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Tách "Phân loại" từ <c>items_json</c> (mảng <c>{name, variation, amount, image}</c> extension quét sẵn ở
/// TRANG DANH SÁCH — KHÔNG cần mở trang chi tiết đơn) để hiện thành cột riêng. Shopee gộp cả hai dòng
/// "Phân loại:" và "SKU phân loại:" vào MỘT ô <c>.item-description</c> nên chuỗi thật có dạng
/// <c>"Nâu Be,39 [A322 A322]"</c> — đuôi ngoặc vuông là SKU lặp lại, CẮT BỎ (SKU đã có cột riêng).
/// Đơn nhiều sản phẩm → nối bằng <c>" · "</c>. Khi đã đọc được số lượng (≥1) gắn hậu tố <c>". SL: N"</c>.
/// <para>
/// Từ bản đọc sản phẩm ở TRANG CHI TIẾT (<c>SanPhamDonParser</c> — chỉ có bên client, KHÔNG link sang hub nên
/// nhắc bằng tên trần), mỗi phần tử có thể có thêm khóa <c>phanLoai</c> SẠCH sẵn — <see cref="ChuoiPhanLoai"/>
/// ưu tiên khóa đó, đơn cũ không có thì vẫn dùng <c>variation</c> + luật cắt đuôi như trước.
/// </para>
/// <para>
/// Dữ liệu đến từ web nên phải chịu được rác: JSON hỏng / thiếu field / <c>&amp;nbsp;</c> / chuỗi rỗng → trả
/// chuỗi RỖNG, KHÔNG ném. File này được LINK sang hub (<c>server/Shopee.Hub.Web</c>) để hub và client hiện
/// CÙNG một luật — sửa ở đây là đổi cả hai nơi.
/// </para>
/// </summary>
public static class PhanLoaiExtractor
{
    /// <summary>Dấu nối phân loại / số lượng của nhiều sản phẩm trong cùng một đơn.</summary>
    private const string Noi = " · ";

    /// <summary>Tiền tố Shopee gắn trước phân loại, tùy NGÔN NGỮ giao diện. Extension chỉ bóc bản tiếng Anh
    /// (<c>background.js</c>: <c>variation.replace(/^Variation\s*:?\s*/i, "")</c>) nên UI tiếng Việt còn nguyên
    /// "Phân loại:" trong dữ liệu đã lưu → bóc nốt ở đây.</summary>
    private static readonly string[] TienTo = { "Phân loại", "Variation" };

    /// <summary>
    /// Gắn hậu tố <c>". SL: N"</c> khi đã biết số lượng <paramref name="soLuong"/> ≥ 1 (kể cả 1).
    /// Không đọc được số / &lt; 1 → trả nguyên <paramref name="phanLoai"/> (không bịa SL).
    /// </summary>
    internal static string GanSoLuong(string phanLoai, int? soLuong)
    {
        if (soLuong is not >= 1)
        {
            return phanLoai ?? string.Empty;
        }

        var n = soLuong.Value.ToString(CultureInfo.InvariantCulture);
        var pl = phanLoai ?? string.Empty;
        return pl.Length > 0 ? pl + ". SL: " + n : "SL: " + n;
    }

    /// <summary>
    /// Chuỗi "Phân loại" của cả đơn từ <paramref name="itemsJson"/>: lấy <c>variation</c>/<c>phanLoai</c> của
    /// từng sản phẩm, dọn theo <see cref="DonGian"/>, gắn <c>. SL: N</c> khi có <c>amount</c>/<c>soLuong</c> ≥ 1,
    /// bỏ sản phẩm không có phân loại, nối bằng <c>" · "</c>. Không khử trùng (mỗi dòng SP giữ riêng để không
    /// mất số lượng). Rỗng / JSON hỏng → chuỗi rỗng (KHÔNG ném).
    /// </summary>
    public static string TuItemsJson(string? itemsJson)
    {
        if (string.IsNullOrWhiteSpace(itemsJson))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(itemsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var raw = ChuoiPhanLoai(item);
                if (raw is null)
                {
                    continue; // phần tử lạ / thiếu field → bỏ qua sản phẩm đó
                }

                var s = DonGian(raw);
                if (s.Length == 0)
                {
                    continue;
                }

                parts.Add(GanSoLuong(s, DocSoLuong(item)));
            }
            return string.Join(Noi, parts);
        }
        catch (JsonException)
        {
            return string.Empty; // items_json rác (đơn cũ / dữ liệu cụt) → coi như không có phân loại
        }
    }

    /// <summary>
    /// Chuỗi số lượng từng sản phẩm từ <paramref name="itemsJson"/> (khóa <c>amount</c>/<c>soLuong</c>), nối
    /// bằng <c>" · "</c> đúng thứ tự. Không có số nào ≥ 1 → chuỗi rỗng. JSON hỏng → chuỗi rỗng (KHÔNG ném).
    /// </summary>
    public static string SoLuongTuItemsJson(string? itemsJson)
    {
        if (string.IsNullOrWhiteSpace(itemsJson))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(itemsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var sl = DocSoLuong(item);
                if (sl is >= 1)
                {
                    parts.Add(sl.Value.ToString(CultureInfo.InvariantCulture));
                }
            }
            return string.Join(Noi, parts);
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Chuỗi SKU của cả đơn từ <paramref name="itemsJson"/>: lấy khóa <c>sku</c> của TỪNG sản phẩm (chỉ trang
    /// CHI TIẾT mới ghi), nối bằng <c>" · "</c> đúng thứ tự mảng — <b>KHÔNG khử trùng</b> (khác
    /// <see cref="TuItemsJson"/>: mỗi SKU là riêng biệt). Không có sản phẩm nào mang <c>sku</c> → chuỗi rỗng
    /// (caller lùi về field DB đơn-giá-trị <c>Sku</c>). Rỗng / <c>"[]"</c> / JSON hỏng → chuỗi rỗng (KHÔNG ném).
    /// </summary>
    public static string SkuTuItemsJson(string? itemsJson)
    {
        if (string.IsNullOrWhiteSpace(itemsJson))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(itemsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                if (!item.TryGetProperty("sku", out var v) || v.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                var s = v.GetString();
                if (string.IsNullOrWhiteSpace(s))
                {
                    continue;
                }
                parts.Add(s!.Replace('\u00A0', ' ').Trim());
            }
            return string.Join(Noi, parts);
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Chuỗi phân loại THÔ của một phần tử: ƯU TIÊN khóa <c>phanLoai</c> (trang CHI TIẾT đơn ghi vào — đã SẠCH,
    /// không dính đuôi <c>[SKU SKU]</c>), không có mới quay về <c>variation</c> (bản quét trang DANH SÁCH của đơn
    /// cũ — vẫn đi luật cắt đuôi trong <see cref="DonGian"/>). Phần tử lạ / cả hai khóa đều thiếu hoặc không phải
    /// chuỗi → <c>null</c>.
    /// </summary>
    private static string? ChuoiPhanLoai(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        if (item.TryGetProperty("phanLoai", out var p)
            && p.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(p.GetString()))
        {
            return p.GetString();
        }
        return item.TryGetProperty("variation", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
    }

    /// <summary>Đọc số lượng từ khóa <c>soLuong</c> hoặc <c>amount</c> (số hoặc chuỗi có chữ số). Không đọc được → null.</summary>
    private static int? DocSoLuong(JsonElement item)
    {
        foreach (var khoa in new[] { "soLuong", "amount" })
        {
            if (!item.TryGetProperty(khoa, out var v))
            {
                continue;
            }

            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n >= 1)
            {
                return n;
            }

            if (v.ValueKind == JsonValueKind.String)
            {
                var parsed = DocSoTuChuoi(v.GetString());
                if (parsed is >= 1)
                {
                    return parsed;
                }
            }
        }

        return null;
    }

    /// <summary>Giữ chữ số trong chuỗi rồi parse (tiền tố <c>x</c>/<c>×</c> tự rụng). Không được → null.</summary>
    private static int? DocSoTuChuoi(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is >= '0' and <= '9')
            {
                sb.Append(c);
            }
        }

        return sb.Length > 0 && int.TryParse(sb.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1
            ? n
            : null;
    }

    /// <summary>
    /// Dọn MỘT chuỗi <c>variation</c>: đổi <c>&amp;nbsp;</c> (U+00A0) thành khoảng trắng thường + trim, bóc tiền
    /// tố "Phân loại:" / "Variation:" (dấu hai chấm KHÔNG bắt buộc — giống regex của extension), rồi CẮT cặp
    /// ngoặc vuông ở CUỐI chuỗi (SKU lặp lại). Chỉ cắt ở cuối: phân loại có thể chính đáng chứa <c>'['</c> ở
    /// giữa. Null / rỗng / chỉ còn SKU → chuỗi rỗng.
    /// </summary>
    internal static string DonGian(string? variation)
    {
        if (string.IsNullOrWhiteSpace(variation))
        {
            return string.Empty;
        }

        // HTML gốc dùng &nbsp; sau dấu hai chấm ("Phân loại:&nbsp;KEM,38/39") → đổi về khoảng trắng thường.
        var s = variation.Replace('\u00A0', ' ').Trim();

        foreach (var tt in TienTo)
        {
            if (!s.StartsWith(tt, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var rest = s.Substring(tt.Length).TrimStart();
            if (rest.StartsWith(":", StringComparison.Ordinal))
            {
                rest = rest.Substring(1).TrimStart();
            }
            s = rest;
            break;
        }

        if (s.EndsWith("]", StringComparison.Ordinal))
        {
            var open = s.LastIndexOf('[');
            if (open >= 0)
            {
                s = s.Substring(0, open);
            }
        }

        return s.Trim();
    }
}
