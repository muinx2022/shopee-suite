using ClosedXML.Excel;
using Shopee.Core.Coordination;

namespace UpdateProduct;

/// <summary>
/// KHUNG DÙNG CHUNG cho các bước "nạp dòng của sheet shop" — trước đây chép bốn lần (Update và Import, mỗi bên
/// một nhánh Excel + một nhánh kho Hub): khoá file → mở workbook → chọn sheet → tính vùng [StartRow..EndRow] →
/// duyệt từng dòng; và bên Hub: lấy client → tính vùng → dựng khoá dòng.
/// Phần KHÁC nhau (đọc cột nào, dựng bản ghi gì) do người gọi truyền vào.
/// </summary>
internal static class WorkbookSheetReader
{
    /// <summary>
    /// Duyệt vùng dòng dữ liệu của sheet shop trong workbook Excel: KHOÁ file khi đọc (chung file giữa nhiều
    /// account — serialize với lúc "Update tên SP" đang GHI cột G, tránh đọc-khi-đang-ghi), chọn sheet
    /// <c>DataSheet</c> (rỗng = sheet đầu), tính <c>start = max(2, StartRow)</c> và
    /// <c>end = EndRow &gt; 0 ? min(EndRow, dòng cuối có dữ liệu) : dòng cuối</c>, rồi gọi
    /// <paramref name="onRow"/> cho từng dòng. Trả về đúng cặp (start, end) đã dùng để người gọi log.
    /// </summary>
    public static async Task<(int Start, int End)> ForEachDataRowAsync(
        BigSellerWorkflowSettings settings, Action<IXLRow, int> onRow, CancellationToken ct)
    {
        using var _ = await WorkbookFileLockHandle.AcquireAsync(settings.WorkbookPath, ct).ConfigureAwait(false);
        using var wb = new XLWorkbook(settings.WorkbookPath);
        var ws = string.IsNullOrWhiteSpace(settings.DataSheet)
            ? wb.Worksheets.First()
            : wb.Worksheet(settings.DataSheet);
        var start = Math.Max(2, settings.StartRow);
        var last = ws.LastRowUsed()?.RowNumber() ?? 0;
        var end = settings.EndRow > 0 ? Math.Min(settings.EndRow, last) : last;

        for (var r = start; r <= end; r++)
            onRow(ws.Row(r), r);

        return (start, end);
    }

    /// <summary>Ô của cột <paramref name="column"/> đã trim; cột = 0 ("không dùng") → rỗng, KHÔNG gọi
    /// <c>Cell(0)</c> (ClosedXML 1-based, <c>Cell(0)</c> ném lỗi).</summary>
    public static string Cell(IXLRow row, int column) =>
        column > 0 ? row.Cell(column).GetString().Trim() : "";

    /// <summary>Khoá dòng = Shopee item id: ưu tiên cột/field Item ID, rỗng thì suy từ link. "" = không định vị được.</summary>
    public static string RowId(string? itemId, string? link) =>
        !string.IsNullOrWhiteSpace(itemId) ? itemId.Trim() : (BigSellerCrawlHelper.ExtractShopeeId(link) ?? "");

    /// <summary>
    /// Mở đầu nhánh HUB-MODE: lấy <see cref="HubClient"/> (chưa kết nối Hub → ném lỗi có hướng dẫn) + vùng dòng
    /// gửi lên server (<c>start = max(2, StartRow)</c>; <c>end = EndRow</c>, 0 = đến hết vì server coi
    /// <c>toRow &lt;= 0</c> là không chặn trên). KHÔNG ánh xạ cột, KHÔNG khoá file.
    /// </summary>
    public static (HubClient Client, int Start, int End) BeginHubRead(BigSellerWorkflowSettings settings)
    {
        var client = CoordinationRuntime.Client
            ?? throw new InvalidOperationException("⛔ Tk ở chế độ kho Hub nhưng chưa kết nối Hub — kiểm tra Cài đặt → Hub rồi chạy lại.");
        return (client, Math.Max(2, settings.StartRow), settings.EndRow);
    }
}
