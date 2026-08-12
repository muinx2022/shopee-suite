using Shopee.Core.Coordination;

namespace Shopee.Core.Scrape;

/// <summary>Một dòng link đã lấy được từ nguồn dữ liệu (workbook Excel hoặc kho Hub).</summary>
public sealed record SheetLinkItem(
    int RowNumber,
    Dictionary<string, object?> RowData,
    string Link);

/// <summary>Kết quả lấy link của một khoảng dòng (+ thống kê dòng bị bỏ qua ở phía Excel).</summary>
public sealed record SheetLinkFetchResult(
    List<SheetLinkItem> Items,
    int SkippedMissingProductName,
    int SkippedMissingLink);

/// <summary>
/// Nguồn dữ liệu link cho vòng scrape — hai chế độ dùng CHUNG một giao diện:
/// Excel-mode đọc native qua <see cref="ScrapeWorkbook"/>, hub-mode gọi kho sản phẩm (Postgres) qua
/// <see cref="CoordinationRuntime.Client"/>. Trước đây nằm trong <c>ExtensionRunnerAutomation</c> của module
/// MultiBrave — thuần đọc dữ liệu, không dính CDP/extension, nên chuyển về cạnh <see cref="ScrapeWorkbook"/>.
/// </summary>
public static class ScrapeLinkSource
{
    // ĐỌC NATIVE (ClosedXML) thay cho API Python — workbook truyền per-instance (chạy song song nhiều BigSeller).
    // HUB-MODE (useHubData): tổng dòng = Rows (tổng dền = MỌI dòng có thật) của sheet trên kho Hub (Postgres) —
    // khớp TotalDataRows của Excel (dense đánh trên mọi dòng, kể cả dòng thiếu link/tên) → chỉ-số-dồn khớp GetProductLinks.
    public static async Task<int> ResolveEndRowAsync(
        string workbookPath, string sheet, int startRow, CancellationToken ct,
        bool useHubData = false, string accountId = "")
    {
        int total;
        if (useHubData)
        {
            var client = CoordinationRuntime.Client
                ?? throw new InvalidOperationException("⛔ Tk ở chế độ kho Hub nhưng chưa kết nối Hub — kiểm tra Cài đặt → Hub rồi chạy lại.");
            var sheets = await client.GetProductSheetsAsync(accountId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("⛔ Hub chưa sẵn sàng (kho sản phẩm Postgres) — thử lại sau.");
            // OrdinalIgnoreCase như mọi chỗ khác so tên sheet (Excel không phân biệt hoa/thường) — so Ordinal
            // ở đây làm tổng dòng = 0 khi hoa/thường lệch, tức "vượt quá số dòng sheet" oan.
            total = sheets.FirstOrDefault(s => string.Equals(s.Sheet, sheet, StringComparison.OrdinalIgnoreCase))?.Rows ?? 0;
        }
        else
        {
            total = ScrapeWorkbook.TotalDataRows(workbookPath, sheet);
        }
        if (total < startRow)
            throw new InvalidOperationException($"Từ dòng {startRow} vượt quá số dòng sheet ({total}).");
        return total;
    }

    public static async Task<SheetLinkFetchResult> FetchSheetLinksAsync(
        string workbookPath,
        string sheet,
        int startRow,
        int endRow,
        CancellationToken ct,
        bool useHubData = false,
        string accountId = "")
    {
        if (useHubData)
        {
            var client = CoordinationRuntime.Client
                ?? throw new InvalidOperationException("⛔ Tk ở chế độ kho Hub nhưng chưa kết nối Hub — kiểm tra Cài đặt → Hub rồi chạy lại.");
            var links = await client.GetProductLinksAsync(accountId, sheet, startRow, endRow, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("⛔ Hub chưa sẵn sàng (kho sản phẩm Postgres) — thử lại sau.");
            // Server đánh chỉ-số-dồn (DenseIndex) trên MỌI dòng (dòng thiếu link/tên vẫn chiếm chỗ, khớp Excel) rồi
            // CHỈ TRẢ dòng HỢP LỆ (link + tên gốc non-blank) trong khoảng dense → dòng bị lọc không xuất hiện ở đây
            // (client không cần đếm skip riêng). RowData mang SKU (+ tên gốc/link) để ExtractSkuFromRow đặt tên video
            // {sku}.mp4 y như đọc từ cột SKU của workbook.
            var hubItems = links
                .Select(l => new SheetLinkItem(
                    l.DenseIndex,
                    new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["sku"] = l.Sku,
                        ["name"] = l.NameOriginal,
                        ["link"] = l.Link,
                    },
                    l.Link))
                .ToList();
            return new SheetLinkFetchResult(hubItems, 0, 0);
        }

        var res = ScrapeWorkbook.FetchLinks(workbookPath, sheet, startRow, endRow);

        var items = res.Items
            .Select(it => new SheetLinkItem(
                it.RowNumber,
                it.Values.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                it.Link))
            .ToList();

        return new SheetLinkFetchResult(
            items, res.SkippedMissingProductName, res.SkippedMissingLink);
    }
}
