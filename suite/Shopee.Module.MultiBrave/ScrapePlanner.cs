using Shopee.Core.Scrape;

namespace Shopee.Modules.MultiBrave;

/// <summary>Phần được giao cho một account Shopee: khoảng dòng + danh sách link đọc từ sheet.</summary>
public sealed record ScrapeSlice(int FromRow, int ToRow, ScrapeLinkResult Links);

/// <summary>
/// Chia công việc scrape của một shop (1 sheet trong workbook) cho nhiều account Shopee: mỗi
/// account nhận một khối dòng liên tiếp <c>rowsPerAccount</c> dòng, đọc link bằng lớp native
/// (không qua Python). Đây là bước chuẩn bị trước khi engine v31 mở Brave chạy từng account.
/// </summary>
public static class ScrapePlanner
{
    public static List<ScrapeSlice> Plan(
        string workbookPath, string sheet, int startRow, int rowsPerAccount, int accountCount)
    {
        if (accountCount < 1) return [];
        var total = ScrapeWorkbook.TotalDataRows(workbookPath, sheet);
        var slices = new List<ScrapeSlice>();
        var cursor = Math.Max(1, startRow);
        for (var i = 0; i < accountCount && cursor <= total; i++)
        {
            var from = cursor;
            var to = Math.Min(total, from + Math.Max(1, rowsPerAccount) - 1);
            var links = ScrapeWorkbook.FetchLinks(workbookPath, sheet, from, to);
            slices.Add(new ScrapeSlice(from, to, links));
            cursor = to + 1;
        }
        return slices;
    }
}
