using System.Text;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Một dòng đơn hàng đã DỰNG SẴN chuỗi hiển thị (đúng thứ tự cột bảng màn "Đơn hàng") để xuất CSV.
/// Thuần dữ liệu, không dính DB/UI — tầng App map từ dòng đang hiển thị sang đây rồi đưa cho
/// <see cref="OrderCsvExporter"/>.
/// </summary>
public sealed record OrderExportRow(
    string Account,
    string OrderSn,
    string Buyer,
    string Product,
    string Total,
    string Estimate,
    string Payment,
    string Status,
    string Note,
    string Carrier,
    string Tracking,
    string SyncedAt);

/// <summary>
/// Dựng nội dung CSV cho đơn hàng theo chuẩn RFC 4180 (bọc ngoặc kép khi trường chứa <c>, " \r \n</c>,
/// nhân đôi ngoặc kép bên trong), kết mỗi dòng bằng CRLF. Hàm thuần, dễ test. Trước khi escape, mỗi
/// trường đi qua <see cref="SanitizeField"/> để CHỐNG CSV/formula injection (dữ liệu do người mua/Shopee
/// kiểm soát). Bản <see cref="BuildCsvWithBom"/> thêm BOM UTF-8 để Excel mở tiếng Việt không vỡ.
/// </summary>
public static class OrderCsvExporter
{
    /// <summary>Tiêu đề các cột (trùng thứ tự cột bảng và các trường của <see cref="OrderExportRow"/>).</summary>
    public static readonly string[] Headers =
    {
        "Tài khoản", "Mã đơn", "Người mua", "Sản phẩm", "Tổng tiền", "Ước tính",
        "Thanh toán", "Trạng thái", "Mô tả/Lý do hủy", "ĐVVC", "Mã vận đơn", "Sync lúc"
    };

    /// <summary>Dựng chuỗi CSV (header + từng dòng), CRLF cuối mỗi dòng. KHÔNG kèm BOM.</summary>
    public static string BuildCsv(IEnumerable<OrderExportRow> rows)
    {
        var sb = new StringBuilder();
        AppendRow(sb, Headers);
        foreach (var r in rows)
        {
            AppendRow(sb, new[]
            {
                r.Account, r.OrderSn, r.Buyer, r.Product, r.Total, r.Estimate,
                r.Payment, r.Status, r.Note, r.Carrier, r.Tracking, r.SyncedAt
            });
        }
        return sb.ToString();
    }

    /// <summary>Dựng CSV có BOM UTF-8 (3 byte EF BB BF) đứng đầu — Excel nhận UTF-8, tiếng Việt không lỗi phông.</summary>
    public static byte[] BuildCsvWithBom(IEnumerable<OrderExportRow> rows)
    {
        var body = Encoding.UTF8.GetBytes(BuildCsv(rows)); // Encoding.UTF8.GetBytes KHÔNG tự chèn BOM
        var result = new byte[3 + body.Length];
        result[0] = 0xEF;
        result[1] = 0xBB;
        result[2] = 0xBF;
        Buffer.BlockCopy(body, 0, result, 3, body.Length);
        return result;
    }

    /// <summary>Escape một trường theo RFC 4180: chỉ bọc ngoặc kép khi cần, nhân đôi ngoặc kép bên trong.</summary>
    public static string Escape(string? field)
    {
        var value = field ?? string.Empty;
        var mustQuote = value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;
        return mustQuote ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    /// <summary>
    /// Chống CSV/formula injection: nếu trường bắt đầu bằng ký tự Excel/Sheets có thể hiểu là công thức
    /// (<c>= + - @</c>, hoặc TAB 0x09 / CR 0x0D ở đầu), chèn dấu nháy đơn <c>'</c> phía trước để ép coi
    /// là văn bản. Áp dụng TRƯỚC <see cref="Escape"/> vì dữ liệu đơn (người mua, tên sản phẩm, lý do hủy)
    /// do bên ngoài kiểm soát.
    /// </summary>
    public static string SanitizeField(string? field)
    {
        var value = field ?? string.Empty;
        return value.Length > 0 && IsDangerousLead(value[0]) ? "'" + value : value;
    }

    private static bool IsDangerousLead(char c)
        => c is '=' or '+' or '-' or '@' or '\t' or '\r';

    private static void AppendRow(StringBuilder sb, IReadOnlyList<string> fields)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }
            sb.Append(Escape(SanitizeField(fields[i])));
        }
        sb.Append("\r\n");
    }
}
