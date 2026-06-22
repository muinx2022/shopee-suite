using ClosedXML.Excel;

namespace Shopee.Core.Scrape;

/// <summary>Một dòng link để scrape, đọc từ workbook.</summary>
public sealed record ScrapeLinkItem(int RowNumber, string Link, Dictionary<string, string> Values);

/// <summary>Kết quả đọc link từ một sheet (gồm các dòng hợp lệ + thống kê dòng bỏ qua).</summary>
public sealed record ScrapeLinkResult(
    List<ScrapeLinkItem> Items, int TotalDataRows, int SkippedMissingProductName, int SkippedMissingLink);

/// <summary>
/// Đọc workbook Excel native bằng ClosedXML — THAY cho API Python (/sheets, /data). Tái hiện đúng
/// ngữ nghĩa cũ: dòng 1 = header; start/end đánh chỉ số theo dòng DỮ LIỆU (1-based, sau header);
/// link = cột A; dòng hợp lệ phải có tên sản phẩm ở cột F (cột thứ 6). Mở read-only +
/// FileShare.ReadWrite để không khóa file khi người dùng đang mở bằng Excel.
/// </summary>
public static class ScrapeWorkbook
{
    public static List<string> ListSheets(string workbookPath)
    {
        var names = new List<string>();
        if (!File.Exists(workbookPath)) return names;
        try
        {
            using var wb = Open(workbookPath);
            foreach (var ws in wb.Worksheets) names.Add(ws.Name);
        }
        catch { }
        return names;
    }

    /// <summary>Tổng số dòng dữ liệu (sau header) của sheet — chỉ đếm dòng CÓ THẬT (bỏ header + bỏ
    /// dòng trống bị Excel lược), khớp <c>len(data_rows)</c> của API Python cũ.</summary>
    public static int TotalDataRows(string workbookPath, string sheet)
    {
        using var wb = Open(workbookPath);
        var ws = GetSheet(wb, sheet);
        return ws.RowsUsed().Count(r => r.RowNumber() > 1);
    }

    /// <summary>
    /// Lấy các link cần scrape trong khoảng [startRow, endRow] (chỉ số theo dòng DỮ LIỆU, 1-based).
    /// Tái hiện đúng API Python: danh sách dòng dữ liệu được NÉN (chỉ dòng có thật, bỏ header + bỏ
    /// dòng trống Excel lược bỏ); RowNumber = vị trí thứ tự trong danh sách nén (KHÔNG phải số dòng
    /// tuyệt đối trên sheet) để khớp tiến độ/resume với app gốc.
    /// </summary>
    public static ScrapeLinkResult FetchLinks(string workbookPath, string sheet, int startRow, int endRow)
    {
        if (startRow < 1) throw new ArgumentException("startRow phải >= 1.");
        if (endRow < startRow) throw new ArgumentException("endRow phải >= startRow.");

        using var wb = Open(workbookPath);
        var ws = GetSheet(wb, sheet);

        var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;

        // Header: ô dòng 1 hoặc dùng chữ cái cột làm tên mặc định (giống API cũ).
        var headers = new string[lastCol + 1];
        for (var c = 1; c <= lastCol; c++)
        {
            var h = ws.Cell(1, c).GetString().Trim();
            headers[c] = string.IsNullOrEmpty(h) ? ColumnLetter(c) : h;
        }

        // Danh sách NÉN các dòng dữ liệu có thật (RowNumber > 1), theo thứ tự tăng dần.
        var dense = ws.RowsUsed().Where(r => r.RowNumber() > 1).OrderBy(r => r.RowNumber()).ToList();
        var total = dense.Count;

        var items = new List<ScrapeLinkItem>();
        var skipName = 0;
        var skipLink = 0;

        // Vị trí p (1-based) trong danh sách nén = RowNumber; lấy [startRow .. min(endRow,total)].
        var end = Math.Min(endRow, total);
        for (var p = startRow; p <= end; p++)
        {
            var row = dense[p - 1];
            var values = new Dictionary<string, string>();
            for (var c = 1; c <= lastCol; c++)
                values[headers[c]] = row.Cell(c).GetString();

            // Cột F (thứ 6) = tên sản phẩm; trống thì bỏ qua.
            var productName = lastCol >= 6 ? row.Cell(6).GetString().Trim() : "";
            if (string.IsNullOrWhiteSpace(productName)) { skipName++; continue; }

            // Cột A = link.
            var link = NormalizeLink(row.Cell(1).GetString());
            if (string.IsNullOrWhiteSpace(link)) { skipLink++; continue; }

            items.Add(new ScrapeLinkItem(p, link, values));
        }

        return new ScrapeLinkResult(items, total, skipName, skipLink);
    }

    private static XLWorkbook Open(string path)
    {
        // Không khóa file: mở qua FileStream chia sẻ đọc/ghi.
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new XLWorkbook(fs);
    }

    private static IXLWorksheet GetSheet(XLWorkbook wb, string sheet)
    {
        if (wb.TryGetWorksheet(sheet, out var ws)) return ws;
        throw new InvalidOperationException($"Không tìm thấy sheet \"{sheet}\".");
    }

    private static string NormalizeLink(string? value)
    {
        var t = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(t)) return "";
        if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return t;
        if (t.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) return "https://" + t;
        return t;
    }

    private static string ColumnLetter(int index)
    {
        var s = "";
        while (index > 0) { index = Math.DivRem(index - 1, 26, out var r); s = (char)(65 + r) + s; }
        return s;
    }
}
