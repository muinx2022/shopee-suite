using System.Globalization;
using ClosedXML.Excel;
using Shopee.Core.Coordination;

namespace Shopee.Hub;

/// <summary>
/// PARSE / WRITE workbook Excel ↔ dòng sản phẩm — hàm THUẦN (không đụng Postgres) để test được không cần DB.
/// <see cref="ProductDb"/> phần Import/Export chỉ gọi codec này rồi bơm/rút SQL. Layout chuẩn = 17 cột A..Q khớp
/// header của ExcelExporter (Search) và BigSellerColumnMap: A=link, B=giá gốc, C=giá bán, D=SKU, E=id gốc,
/// F=tên gốc, G=tên đã sửa, H..Q=danh mục…item id. 6 cột NGHIỆP VỤ (link/giá/SKU/item/tên/tên-sửa) cho ghi đè
/// vị trí (workbook layout khác); 11 cột còn lại đọc theo vị trí cố định.
/// </summary>
public static class ProductXlsxCodec
{
    /// <summary>17 header chuẩn — lấy NGUYÊN VĂN từ Shopee.Module.Search/Engine/ExcelExporter.cs (đừng đổi thứ tự).</summary>
    public static readonly string[] Headers =
    {
        "link sp", "Giá gốc", "Giá bán", "SKU", "ID sản phẩm gốc",
        "Tên sp", "Tên sp đã sửa", "Danh mục", "Shop", "Rating",
        "Đã bán/tháng", "Lượt thích", "Số đánh giá", "Khu vực shop",
        "Ảnh", "Shop ID", "Item ID",
    };

    /// <summary>Vị trí cột (1-based) của 6 field nghiệp vụ. Mặc định khớp BigSellerColumnMap (1,3,4,5,6,7).
    /// 0 = không dùng field đó → đọc thành rỗng.</summary>
    public sealed record ColumnOverrides(int Link, int Price, int Sku, int Item, int Name, int Rewritten)
    {
        public static readonly ColumnOverrides Default = new(1, 3, 4, 5, 6, 7);
    }

    /// <summary>1 dòng đã parse: số dòng TUYỆT ĐỐI trên sheet + 17 ô.</summary>
    public sealed record ParsedRow(int RowNo, ProductRowData Data);

    /// <summary>
    /// Đọc MỌI worksheet của file xlsx. Dòng 1 = header (bỏ qua). row_no = số dòng THẬT (dòng trống Excel lược
    /// bỏ → row_no có lỗ hổng là ĐÚNG). CHỈ giữ dòng có ≥1 ô non-blank trong 17 cột đọc được.
    /// </summary>
    public static List<(string Sheet, List<ParsedRow> Rows)> Parse(Stream xlsx, ColumnOverrides? overrides = null)
    {
        var ov = overrides ?? ColumnOverrides.Default;
        var result = new List<(string, List<ParsedRow>)>();
        using var wb = new XLWorkbook(xlsx);
        foreach (var ws in wb.Worksheets)
        {
            var rows = new List<ParsedRow>();
            // ws.RowsUsed() → IXLRow (cột TUYỆT ĐỐI); bỏ header (dòng 1); tăng dần theo số dòng.
            foreach (var row in ws.RowsUsed().Where(r => r.RowNumber() > 1).OrderBy(r => r.RowNumber()))
            {
                var data = new ProductRowData(
                    Link:          Col(row, ov.Link),
                    PriceOriginal: Col(row, 2),
                    PriceSale:     Col(row, ov.Price),
                    Sku:           Col(row, ov.Sku),
                    ItemId:        Col(row, ov.Item),
                    NameOriginal:  Col(row, ov.Name),
                    NameRewritten: Col(row, ov.Rewritten),
                    Category:      Col(row, 8),
                    ShopName:      Col(row, 9),
                    Rating:        Col(row, 10),
                    SoldMonth:     Col(row, 11),
                    Likes:         Col(row, 12),
                    Reviews:       Col(row, 13),
                    Region:        Col(row, 14),
                    Image:         Col(row, 15),
                    MetaShopId:    Col(row, 16),
                    MetaItemId:    Col(row, 17));

                if (AllBlank(data)) continue;   // dòng dùng-thật ở cột ngoài 17 → coi như trống, bỏ
                rows.Add(new ParsedRow(row.RowNumber(), data));
            }
            result.Add((ws.Name, rows));
        }
        return result;
    }

    /// <summary>
    /// Ghi các sheet ra 1 workbook xlsx (byte[]). Mỗi sheet 1 worksheet ĐÚNG tên; dòng 1 = 17 header chuẩn;
    /// dữ liệu đặt ĐÚNG <see cref="ParsedRow.RowNo"/> (lỗ hổng để trống). Luôn dùng layout chuẩn A..Q.
    /// </summary>
    public static byte[] Write(IReadOnlyList<(string Sheet, IReadOnlyList<ParsedRow> Rows)> sheets)
    {
        using var wb = new XLWorkbook();
        // ClosedXML không cho lưu workbook 0 worksheet → acc rỗng vẫn trả file hợp lệ (1 sheet chỉ có header).
        if (sheets.Count == 0)
        {
            var empty = wb.AddWorksheet("Sheet1");
            for (var c = 0; c < Headers.Length; c++)
                empty.Cell(1, c + 1).Value = Headers[c];
        }
        foreach (var (sheet, rows) in sheets)
        {
            var ws = wb.AddWorksheet(sheet);
            for (var c = 0; c < Headers.Length; c++)
                ws.Cell(1, c + 1).Value = Headers[c];

            foreach (var pr in rows)
            {
                var d = pr.Data;
                Put(ws, pr.RowNo, 1, d.Link);
                Put(ws, pr.RowNo, 2, d.PriceOriginal);
                Put(ws, pr.RowNo, 3, d.PriceSale);
                Put(ws, pr.RowNo, 4, d.Sku);
                Put(ws, pr.RowNo, 5, d.ItemId);
                Put(ws, pr.RowNo, 6, d.NameOriginal);
                Put(ws, pr.RowNo, 7, d.NameRewritten);
                Put(ws, pr.RowNo, 8, d.Category);
                Put(ws, pr.RowNo, 9, d.ShopName);
                Put(ws, pr.RowNo, 10, d.Rating);
                Put(ws, pr.RowNo, 11, d.SoldMonth);
                Put(ws, pr.RowNo, 12, d.Likes);
                Put(ws, pr.RowNo, 13, d.Reviews);
                Put(ws, pr.RowNo, 14, d.Region);
                Put(ws, pr.RowNo, 15, d.Image);
                Put(ws, pr.RowNo, 16, d.MetaShopId);
                Put(ws, pr.RowNo, 17, d.MetaItemId);
            }
        }
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    // Đọc ô (cột col 1-based) thành text nguyên trạng. col<1 → rỗng (field bị tắt).
    private static string Col(IXLRow row, int col) => col < 1 ? "" : CellText(row.Cell(col));

    // Ghi ô dạng TEXT (không set khi rỗng → giữ ô trống). Ghi mọi giá trị dạng chuỗi → item id/giá không bị
    // Excel đổi sang số → tránh ".0"/ký hiệu khoa học khi đọc lại.
    private static void Put(IXLWorksheet ws, int row, int col, string value)
    {
        if (!string.IsNullOrEmpty(value)) ws.Cell(row, col).Value = value;
    }

    /// <summary>Đọc ô về text ổn định: text → trim; số nguyên (item id, giá tròn) → KHÔNG ".0" và không rơi
    /// vào ký hiệu khoa học; các kiểu khác → chuỗi bất biến. Nhất quán với ScrapeWorkbook (đọc read-only).</summary>
    private static string CellText(IXLCell cell)
    {
        var v = cell.Value;
        if (v.IsBlank) return "";
        if (v.IsText) return v.GetText().Trim();
        if (v.IsNumber)
        {
            var d = v.GetNumber();
            // Số nguyên trong tầm long chính xác (< 2^53) → in không phần thập phân, không ngăn cách, không mũ.
            if (d == Math.Truncate(d) && Math.Abs(d) < 1e15)
                return ((long)d).ToString(CultureInfo.InvariantCulture);
            return d.ToString("0.###############", CultureInfo.InvariantCulture);
        }
        if (v.IsBoolean) return v.GetBoolean() ? "TRUE" : "FALSE";
        if (v.IsDateTime) return v.GetDateTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        if (v.IsTimeSpan) return v.GetTimeSpan().ToString();
        return cell.GetString().Trim();
    }

    private static bool AllBlank(ProductRowData d) =>
        string.IsNullOrEmpty(d.Link) && string.IsNullOrEmpty(d.PriceOriginal) && string.IsNullOrEmpty(d.PriceSale) &&
        string.IsNullOrEmpty(d.Sku) && string.IsNullOrEmpty(d.ItemId) && string.IsNullOrEmpty(d.NameOriginal) &&
        string.IsNullOrEmpty(d.NameRewritten) && string.IsNullOrEmpty(d.Category) && string.IsNullOrEmpty(d.ShopName) &&
        string.IsNullOrEmpty(d.Rating) && string.IsNullOrEmpty(d.SoldMonth) && string.IsNullOrEmpty(d.Likes) &&
        string.IsNullOrEmpty(d.Reviews) && string.IsNullOrEmpty(d.Region) && string.IsNullOrEmpty(d.Image) &&
        string.IsNullOrEmpty(d.MetaShopId) && string.IsNullOrEmpty(d.MetaItemId);
}
