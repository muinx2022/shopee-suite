using System.Text.Json;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Tách "Phân loại" từ <c>items_json</c> (mảng <c>{name, variation, amount, image}</c> extension quét sẵn ở
/// TRANG DANH SÁCH — KHÔNG cần mở trang chi tiết đơn) để hiện thành cột riêng. Shopee gộp cả hai dòng
/// "Phân loại:" và "SKU phân loại:" vào MỘT ô <c>.item-description</c> nên chuỗi thật có dạng
/// <c>"Nâu Be,39 [A322 A322]"</c> — đuôi ngoặc vuông là SKU lặp lại, CẮT BỎ (SKU đã có cột riêng).
/// Đơn nhiều sản phẩm → nối bằng <c>" · "</c>.
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
    /// <summary>Dấu nối phân loại của nhiều sản phẩm trong cùng một đơn.</summary>
    private const string Noi = " · ";

    /// <summary>Tiền tố Shopee gắn trước phân loại, tùy NGÔN NGỮ giao diện. Extension chỉ bóc bản tiếng Anh
    /// (<c>background.js</c>: <c>variation.replace(/^Variation\s*:?\s*/i, "")</c>) nên UI tiếng Việt còn nguyên
    /// "Phân loại:" trong dữ liệu đã lưu → bóc nốt ở đây.</summary>
    private static readonly string[] TienTo = { "Phân loại", "Variation" };

    /// <summary>
    /// Chuỗi "Phân loại" của cả đơn từ <paramref name="itemsJson"/>: lấy <c>variation</c> của từng sản phẩm, dọn
    /// theo <see cref="DonGian"/>, bỏ sản phẩm không có phân loại, bỏ TRÙNG LẶP LIÊN TIẾP rồi nối bằng
    /// <c>" · "</c>. Rỗng / <c>"[]"</c> / JSON hỏng / không phải mảng → chuỗi rỗng (KHÔNG ném).
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

                // Đơn nhiều SP cùng một phân loại → không lặp lại "Kem,36 · Kem,36".
                if (parts.Count > 0 && string.Equals(parts[^1], s, StringComparison.Ordinal))
                {
                    continue;
                }
                parts.Add(s);
            }
            return string.Join(Noi, parts);
        }
        catch (JsonException)
        {
            return string.Empty; // items_json rác (đơn cũ / dữ liệu cụt) → coi như không có phân loại
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
