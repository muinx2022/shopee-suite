namespace UpdateProduct;

// Tách khỏi BigSellerProductUpdateRunner.cs (đợt D — pure move): ba type "ở nhờ" của module Update —
// LaneAbortedException (tín hiệu lane phải khởi động lại), WorkbookRecord (một dòng workbook đã map)
// và WorkbookRecordCache (cache dữ liệu shop nạp MỘT lần, dùng chung mọi lane).

/// <summary>Lỗi khiến một lane Update phải DỪNG bất thường (tab đóng, listing lỗi liên tục, captcha…)
/// — ném ra để supervisor <c>RunLanesAsync</c> KHỞI ĐỘNG LẠI lane thay vì để nó "nghỉ hưu" âm thầm.
/// Trước đây các nhánh lỗi dùng <c>break</c> (return bình thường) nên supervisor tưởng "hết việc" →
/// lane mất hẳn → worker rụng dần 5→1→0.</summary>
internal sealed class LaneAbortedException(string reason) : Exception(reason);

/// <summary>Một dòng workbook đã map sang dữ liệu update (khớp theo Shopee item id).</summary>
internal sealed record WorkbookRecord(string Link, string Sku, string ProductName, string Price, int LineIndex);

/// <summary>
/// Cache DÙNG CHUNG dữ liệu shop (dòng workbook → <see cref="WorkbookRecord"/>, khóa = Shopee item id) cho
/// TẤT CẢ lane update. Nạp MỘT LẦN trước khi mở Brave rồi chia sẻ (immutable) → thay vì mỗi lane tự đọc
/// workbook (N lần đọc + N lần khóa file + N lần parse). Đây là "cache chung từ dòng đến dòng" trước khi
/// từng lane tìm item id trên Listing và sửa.
/// </summary>
internal sealed class WorkbookRecordCache
{
    public IReadOnlyDictionary<string, WorkbookRecord> Records { get; }

    private WorkbookRecordCache(IReadOnlyDictionary<string, WorkbookRecord> records) => Records = records;

    /// <summary>Nạp + log (dùng ở tầng điều phối, nạp 1 lần cho mọi lane).</summary>
    public static async Task<WorkbookRecordCache> LoadAsync(BigSellerWorkflowSettings settings, Action<string> log, CancellationToken ct)
    {
        var (map, emptyRewriteRows) = await LoadRecordMapAsync(settings, ct).ConfigureAwait(false);
        if (emptyRewriteRows.Count > 0)
        {
            var preview = string.Join(", ", emptyRewriteRows.Take(10));
            log($"⚠ BỎ QUA {emptyRewriteRows.Count} dòng có cột G (Tên đã sửa) TRỐNG (vd dòng {preview}) — " +
                "chạy \"Update tên SP (AI)\" để điền cột G nếu muốn update các dòng này.");
        }
        log($"📒 Workbook (cache chung mọi lane): {map.Count} dòng (khớp theo Shopee item id).");
        return new WorkbookRecordCache(map);
    }

    // Đọc workbook → map item id → record. Khóa file khi đọc (chung file giữa nhiều account): serialize với
    // lúc "Update tên SP" đang GHI cột G → tránh đọc-khi-đang-ghi (IOException/đọc lệch). Thuần, không log.
    internal static async Task<(Dictionary<string, WorkbookRecord> map, List<int> emptyRewriteRows)> LoadRecordMapAsync(
        BigSellerWorkflowSettings settings, CancellationToken ct)
    {
        // HUB-MODE: dòng đã có tên-sửa lấy từ kho Hub (Postgres) — KHÔNG ánh xạ cột, KHÔNG khoá file. Đặt TRƯỚC
        // validate cột (cột chỉ áp cho workbook Excel). Excel-mode giữ nguyên toàn bộ nhánh dưới.
        if (settings.UseHubData)
            return await LoadRecordMapFromHubAsync(settings, ct).ConfigureAwait(false);

        // Cột bắt buộc: "Tên đã sửa" (tên để update) + ít nhất 1 trong (Item ID / Link) để khớp dòng.
        // 0 = "không dùng" → fail rõ ràng thay vì đẩy 0 vào ClosedXML (Cell(0) ném lỗi).
        if (settings.RewrittenNameColumn <= 0)
            throw new InvalidOperationException("Chưa map cột 'Tên đã sửa' cho shop (mục BigSeller → Ánh xạ cột).");
        if (settings.ItemIdColumn <= 0 && settings.LinkColumn <= 0)
            throw new InvalidOperationException("Cần map ít nhất 'Item ID' hoặc 'Link' để khớp dòng (mục BigSeller → Ánh xạ cột).");

        var map = new Dictionary<string, WorkbookRecord>();
        var emptyRewriteRows = new List<int>();   // dòng có SP để update nhưng cột G (Tên đã sửa) còn trống
        await WorkbookSheetReader.ForEachDataRowAsync(settings, (row, r) =>
        {
            var link = WorkbookSheetReader.Cell(row, settings.LinkColumn);
            var price = WorkbookSheetReader.Cell(row, settings.PriceColumn);
            var sku = WorkbookSheetReader.Cell(row, settings.SkuColumn);
            var colE = WorkbookSheetReader.Cell(row, settings.ItemIdColumn);
            var rewritten = row.Cell(settings.RewrittenNameColumn).GetString().Trim();   // Tên đã sửa (đã validate > 0)

            var rowId = WorkbookSheetReader.RowId(colE, link);
            if (string.IsNullOrWhiteSpace(rowId)) return;

            // Cột G trống → BỎ QUA riêng dòng đó (không update tên gốc cột F), vẫn chạy tiếp các dòng khác.
            if (string.IsNullOrWhiteSpace(rewritten)) { emptyRewriteRows.Add(r); return; }
            map[rowId] = new WorkbookRecord(link, sku, rewritten, price, r);
        }, ct).ConfigureAwait(false);

        return (map, emptyRewriteRows);
    }

    // HUB-MODE của LoadRecordMapAsync: server đã LỌC đúng dòng cần update (name_rewritten non-blank AND có
    // itemId/link) trong [StartRow..EndRow] và trả field cấu trúc → dựng CÙNG map itemId→WorkbookRecord như nhánh
    // Excel (itemId ưu tiên ItemId, rỗng thì suy từ Link; LineIndex=RowNo; ProductName=NameRewritten; Price=PriceSale).
    // emptyRewriteRows rỗng: server không trả dòng "cột G trống" nên KHÔNG có gì để cảnh báo. KHÔNG khoá file.
    private static async Task<(Dictionary<string, WorkbookRecord> map, List<int> emptyRewriteRows)> LoadRecordMapFromHubAsync(
        BigSellerWorkflowSettings settings, CancellationToken ct)
    {
        var (client, start, end) = WorkbookSheetReader.BeginHubRead(settings);
        var rows = await client.GetProductRecordMapAsync(settings.AccountId, settings.DataSheet, start, end, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("⛔ Hub chưa sẵn sàng (kho sản phẩm Postgres) — thử lại sau.");

        var map = new Dictionary<string, WorkbookRecord>();
        foreach (var r in rows)
        {
            var rowId = WorkbookSheetReader.RowId(r.ItemId, r.Link);
            if (string.IsNullOrWhiteSpace(rowId)) continue;
            map[rowId] = new WorkbookRecord(r.Link, r.Sku, r.NameRewritten, r.PriceSale, r.RowNo);
        }
        return (map, new List<int>());
    }
}
