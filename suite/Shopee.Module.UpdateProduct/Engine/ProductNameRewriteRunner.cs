using ClosedXML.Excel;
using Shopee.Core.Ai;
using Shopee.Core.Coordination;

namespace UpdateProduct;

internal sealed class ProductNameRewriteRunner
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _task;

    /// <summary>Bắn (rowIndex, rowIndex) mỗi dòng vừa GHI XONG tên mới vào workbook (sau khi Save batch) →
    /// caller đẩy lên ledger Hub để Thống kê biết "shop này đã rewrite những dòng nào".</summary>
    public event Action<int, int>? RowsDone;

    public bool IsRunning
    {
        get { lock (_gate) return _task is { IsCompleted: false }; }
    }

    public void Start(BigSellerWorkflowSettings settings, Action<string> log, Action? onExit)
    {
        lock (_gate)
        {
            if (_task is { IsCompleted: false })
                throw new InvalidOperationException("Rewrite tên đang chạy.");

            var cts = new CancellationTokenSource();
            _cts = cts;
            // Giữ tham chiếu cts cục bộ: continuation chạy trên thread khác, không đọc/null hoá field
            // dùng chung (tránh race với Stop()/IsRunning gây ObjectDisposedException/NRE).
            _task = Task.Run(() => RunAsync(settings, log, cts.Token), cts.Token).ContinueWith(t =>
            {
                try
                {
                    if (t.Exception is not null)
                        log($"✖ Rewrite tên lỗi: {t.Exception.GetBaseException().Message}");
                    else if (t.IsCanceled)
                        log("■ Rewrite tên đã dừng theo yêu cầu.");
                }
                finally
                {
                    lock (_gate)
                    {
                        cts.Dispose();
                        if (ReferenceEquals(_cts, cts))
                            _cts = null;
                    }
                    onExit?.Invoke();
                }
            }, TaskScheduler.Default);
        }
    }

    public void Stop(Action<string>? log = null)
    {
        CancellationTokenSource? cts;
        lock (_gate)
            cts = _cts;
        if (cts is null)
            return;
        try
        {
            cts.Cancel();
            log?.Invoke("Đã yêu cầu dừng rewrite tên.");
        }
        catch (ObjectDisposedException)
        {
            // Run vừa kết thúc và continuation đã dispose cts — coi như đã dừng.
        }
        catch (Exception ex)
        {
            log?.Invoke($"Không dừng được: {ex.Message}");
        }
    }

    private async Task RunAsync(BigSellerWorkflowSettings settings, Action<string> log, CancellationToken ct)
    {
        // HUB-MODE: nguồn dòng-chờ-rewrite là kho Hub (Postgres) — KHÔNG cần workbook file, KHÔNG ánh xạ cột,
        // KHÔNG mở XLWorkbook (đọc lẫn ghi). Excel-mode giữ nguyên từng byte ở các nhánh else bên dưới.
        var useHub = settings.UseHubData;
        HubClient? client = null;
        if (useHub)
            client = CoordinationRuntime.Client
                ?? throw new InvalidOperationException("⛔ Tk ở chế độ kho Hub nhưng chưa kết nối Hub — kiểm tra Cài đặt → Hub rồi chạy lại.");

        var workbookPath = settings.WorkbookPath?.Trim() ?? "";   // non-null: hub-mode có thể rỗng (không dùng); excel-mode guard dưới chặn rỗng
        var sheetName = settings.DataSheet?.Trim();
        var startRow = Math.Max(2, settings.StartRow);
        var endRow = Math.Max(0, settings.EndRow);

        if (!useHub && (string.IsNullOrWhiteSpace(workbookPath) || !File.Exists(workbookPath)))
            throw new FileNotFoundException($"Không tìm thấy workbook: {workbookPath}");
        if (string.IsNullOrWhiteSpace(sheetName))
            throw new InvalidOperationException("Thiếu tên sheet.");

        // Dùng cấu hình AI CHUNG lấy TƯƠI từ Hub (provider + model + key) — KHÔNG còn cứng OpenAI.
        var cfg = await HubAiConfig.GetAsync(ct).ConfigureAwait(false);
        if (!cfg.HasActiveKey)
            throw new InvalidOperationException($"Chưa cấu hình API key cho {cfg.Provider} (trang Cấu hình AI trên Hub).");
        var batchSize = Math.Clamp(cfg.BatchSize, 1, 500);
        // LÕI viết-lại-tên tách về Core (dùng chung với hub) — client giữ NGUYÊN hành vi: truyền hàm cắt-giữ-SKU
        // của module + để engine dùng AiChat mặc định. Prompt/retry/parse/fallback nằm trong engine (byte-đúng).
        var engine = new NameRewriteEngine(cfg, BigSellerText.TruncateProductNamePreservingSku);

        // Cột Excel theo cấu hình của shop. 0 = "không dùng" → fail rõ ràng (KHÔNG âm thầm rơi về cột
        // mặc định D/F/G — sẽ đọc/ghi nhầm cột). Đây là 3 cột BẮT BUỘC của rewrite (chỉ áp cho workbook).
        var skuCol = settings.SkuColumn;
        var nameCol = settings.ProductNameColumn;
        var rewrittenCol = settings.RewrittenNameColumn;
        if (!useHub && (skuCol <= 0 || nameCol <= 0 || rewrittenCol <= 0))
            throw new InvalidOperationException(
                "Chưa map đủ cột 'SKU' / 'Tên gốc' / 'Tên đã sửa' cho shop (mục BigSeller → Ánh xạ cột).");

        RewritePlan plan;
        if (useHub)
        {
            // Server đã LỌC đúng luật BuildPlan (Tên gốc + SKU non-blank, Tên-sửa blank) trong [startRow..endRow].
            plan = await BuildPlanFromHubAsync(client!, settings.AccountId, sheetName, startRow, endRow, nameCol, skuCol, rewrittenCol, ct);
        }
        else
        {
            using (await WorkbookFileLockHandle.AcquireAsync(workbookPath, ct))
            {
                ct.ThrowIfCancellationRequested();
                plan = BuildPlan(workbookPath, sheetName, startRow, endRow, nameCol, skuCol, rewrittenCol);
            }
        }

        var rangeEnd = plan.LastIncludedRow;
        log(useHub
            ? $"📝 Rewrite tên (kho Hub): acct='{settings.AccountId}', sheet='{plan.SheetName}', rows={plan.FirstRow}-{rangeEnd}"
            : $"📝 Rewrite tên (C#): workbook='{workbookPath}', sheet='{plan.SheetName}', rows={plan.FirstRow}-{rangeEnd}");
        log($"📝 AI: {cfg.Provider} · {cfg.ActiveModel} | Batch size: {batchSize}");

        if (plan.RowsToUpdate.Count == 0)
        {
            if (useHub)
                log($"✓ Không còn dòng cần rewrite trên kho Hub (sheet '{plan.SheetName}', dòng {plan.FirstRow}+): mọi dòng đã có tên-sửa hoặc thiếu Tên gốc/SKU.");
            else
            {
                log($"✓ Không còn dòng cần rewrite (bỏ qua: {plan.SkippedNoName} thiếu cột F 'Tên sp', {plan.SkippedNoSku} thiếu cột D 'SKU', {plan.SkippedExisting} đã có cột G 'Tên sp đã sửa').");
                LogEmptyPlanDiagnostics(plan, log);
            }
            return;
        }

        log($"📝 Cần rewrite: {plan.UniqueNames.Count} tên unique / {plan.RowsToUpdate.Count} dòng.");

        var updatedCount = 0;
        for (var i = 0; i < plan.UniqueNames.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = plan.UniqueNames.Skip(i).Take(Math.Min(batchSize, plan.UniqueNames.Count - i)).ToList();
            log($"📝 Rewrite batch {i + 1}-{i + batch.Count}/{plan.UniqueNames.Count} — đang gọi AI…");

            // 1 lần gọi AI: tên gốc → tiêu đề SEO hoàn chỉnh (keyword1 - keyword2 + cụm mô tả, KHÔNG kèm SKU).
            var titles = await engine.RewriteTitlesAsync(batch, log, ct);
            if (titles.Count != batch.Count)
                throw new InvalidOperationException($"AI trả về số tiêu đề không khớp. Expected={batch.Count}, actual={titles.Count}");

            var updates = new List<(int RowNumber, string RewrittenName)>();
            for (var idx = 0; idx < batch.Count; idx++)
            {
                var originalName = batch[idx];
                foreach (var rowEntry in plan.RowsByOriginalName.GetValueOrDefault(originalName, []))
                {
                    // Ghép SKU CỦA MÌNH theo cú pháp "keyword1 - keyword2 product-desc sku", cắt tối đa 120 ký tự (giữ SKU).
                    var finalName = engine.ComposeFinalName(titles[idx], rowEntry.Sku);
                    if (!string.IsNullOrWhiteSpace(finalName))
                        updates.Add((rowEntry.RowIndex, finalName));
                }
            }

            var batchUpdated = 0;
            var batchLogged = 0;
            const int MaxLogPerBatch = 20;
            var changedRows = new List<int>();
            if (useHub)
            {
                // HUB-MODE: server chỉ trả dòng CHƯA có tên-sửa → mọi dòng trong batch là ghi MỚI (không cần dedup
                // 'current != rewrittenName' như Excel). WRITE-AHEAD ra journal TRƯỚC khi POST (chống mất tiền AI khi
                // mất mạng/503), rồi POST; POST OK → flush tồn đọng cũ nhân tiện (fire-and-forget); POST fail → log
                // + TIẾP batch sau (kết quả đã nằm journal, TryFlushAsync đẩy lại sau). KHÔNG mở XLWorkbook.
                var items = new List<ProductRewrittenItem>(updates.Count);
                foreach (var (rowNumber, rewrittenName) in updates)
                {
                    items.Add(new ProductRewrittenItem(rowNumber, rewrittenName));
                    updatedCount++;
                    batchUpdated++;
                    changedRows.Add(rowNumber);
                    if (batchLogged < MaxLogPerBatch)
                    {
                        log($"Row {rowNumber}");
                        log($"Viết lại: {rewrittenName}");
                        batchLogged++;
                    }
                }
                if (items.Count > 0)
                {
                    PendingRewriteJournal.Append(settings.AccountId, plan.SheetName, items);
                    try
                    {
                        await client!.PostProductRewrittenAsync(
                            new ProductRewrittenRequest(settings.AccountId, plan.SheetName, items), ct).ConfigureAwait(false);
                        _ = FlushJournalQuietlyAsync(client!);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { log($"⚠ Đẩy tên-sửa lên Hub lỗi (đã ghi journal, sẽ tự flush sau): {Shorten(ex.Message)}"); }
                }
            }
            else
            {
                using (await WorkbookFileLockHandle.AcquireAsync(workbookPath, ct))
                {
                    ct.ThrowIfCancellationRequested();
                    using var wb = new XLWorkbook(workbookPath);
                    var ws = ResolveWorksheet(wb, sheetName);
                    EnsureRewrittenNameColumnHeader(ws, plan.RewrittenNameColumn);

                    foreach (var (rowNumber, rewrittenName) in updates)
                    {
                        var beforeName = (ws.Cell(rowNumber, plan.ProductNameColumn).GetValue<string>() ?? "").Trim();
                        var cell = ws.Cell(rowNumber, plan.RewrittenNameColumn);
                        var current = (cell.GetValue<string>() ?? "").Trim();
                        if (current != rewrittenName)
                        {
                            cell.Value = rewrittenName;
                            updatedCount++;
                            batchUpdated++;
                            changedRows.Add(rowNumber);

                            if (batchLogged < MaxLogPerBatch)
                            {
                                log($"Row {rowNumber}");
                                log($"Trước: {beforeName}");
                                log($"Viết lại: {rewrittenName}");
                                batchLogged++;
                            }
                        }
                    }

                    wb.Save();
                }
            }

            // Đã ghi xong batch (Excel: Save; Hub: POST/journal) → báo caller (đẩy ledger) đúng các dòng vừa ghi tên mới.
            foreach (var rn in changedRows) RowsDone?.Invoke(rn, rn);

            log($"💾 Đã save batch {i + 1}-{i + batch.Count}/{plan.UniqueNames.Count}: {batchUpdated} dòng đổi tên.");
            if (batchUpdated > 0)
                log("💾 Batch đã ghi xong — có thể chạy Update product (đóng Excel nếu đang mở file).");
        }

        log($"✓ Xong rewrite tên: {updatedCount} dòng thay đổi. Bỏ qua: {plan.SkippedNoName} thiếu 'Tên sp', {plan.SkippedNoSku} thiếu 'SKU', {plan.SkippedExisting} đã có 'Tên sp đã sửa'.");
    }

    private static string Shorten(string message)
    {
        message = (message ?? "").ReplaceLineEndings(" ").Trim();
        return message.Length <= 300 ? message : message[..300] + "…";
    }

    private static RewritePlan BuildPlan(
        string workbookPath, string sheetName, int startRow, int endRow,
        int productNameColumn, int skuColumn, int rewrittenNameColumn)
    {
        using var wb = new XLWorkbook(workbookPath);
        var ws = ResolveWorksheet(wb, sheetName);

        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
        var firstRow = Math.Max(2, startRow);
        var lastIncludedRow = endRow > 0 ? Math.Min(endRow, lastRow) : lastRow;

        var rowsToUpdate = new List<(int RowIndex, string OriginalName, string Sku)>();
        var uniqueNames = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rowsByName = new Dictionary<string, List<(int RowIndex, string Sku)>>(StringComparer.Ordinal);
        var skuByName = new Dictionary<string, string>(StringComparer.Ordinal);

        var skippedNoName = 0;
        var skippedNoSku = 0;
        var skippedExisting = 0;

        for (var r = firstRow; r <= lastIncludedRow; r++)
        {
            var originalName = (ws.Cell(r, productNameColumn).GetValue<string>() ?? "").Trim();
            var sku = (ws.Cell(r, skuColumn).GetValue<string>() ?? "").Trim();

            if (string.IsNullOrWhiteSpace(originalName))
            {
                skippedNoName++;
                continue;
            }
            if (string.IsNullOrWhiteSpace(sku))
            {
                skippedNoSku++;
                continue;
            }

            var currentRewritten = (ws.Cell(r, rewrittenNameColumn).GetValue<string>() ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(currentRewritten))
            {
                skippedExisting++;
                continue;
            }

            rowsToUpdate.Add((r, originalName, sku));
            rowsByName.TryAdd(originalName, []);
            rowsByName[originalName].Add((r, sku));
            if (!skuByName.ContainsKey(originalName))
                skuByName[originalName] = sku;
            if (seen.Add(originalName))
                uniqueNames.Add(originalName);
        }

        return new RewritePlan
        {
            WorkbookPath = workbookPath,
            SheetName = ws.Name,
            ProductNameColumn = productNameColumn,
            SkuColumn = skuColumn,
            RewrittenNameColumn = rewrittenNameColumn,
            FirstRow = firstRow,
            LastIncludedRow = lastIncludedRow,
            RowsToUpdate = rowsToUpdate,
            UniqueNames = uniqueNames,
            RowsByOriginalName = rowsByName,
            SkuByOriginalName = skuByName,
            SkippedNoName = skippedNoName,
            SkippedNoSku = skippedNoSku,
            SkippedExisting = skippedExisting,
        };
    }

    // HUB-MODE của BuildPlan: server (GetRewritePendingAsync) đã lọc đúng luật (name_original + sku non-blank,
    // name_rewritten blank) trong [startRow..endRow] và ORDER BY row_no → dựng CÙNG RewritePlan như đọc workbook
    // (dedup theo tên gốc, gom rows theo tên, SKU đầu-tiên mỗi tên). Skip counts = 0 (server không trả dòng bị lọc).
    // Column* mang theo cho đủ record nhưng KHÔNG dùng khi ghi (hub-mode POST theo RowNo, không đụng cột Excel).
    private static async Task<RewritePlan> BuildPlanFromHubAsync(
        HubClient client, string acct, string sheetName, int startRow, int endRow,
        int productNameColumn, int skuColumn, int rewrittenNameColumn, CancellationToken ct)
    {
        var firstRow = Math.Max(2, startRow);
        var rows = await client.GetProductRewritePendingAsync(acct, sheetName, firstRow, endRow, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("⛔ Hub chưa sẵn sàng (kho sản phẩm Postgres) — thử lại sau.");

        var rowsToUpdate = new List<(int RowIndex, string OriginalName, string Sku)>();
        var uniqueNames = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rowsByName = new Dictionary<string, List<(int RowIndex, string Sku)>>(StringComparer.Ordinal);
        var skuByName = new Dictionary<string, string>(StringComparer.Ordinal);
        var lastIncludedRow = firstRow;

        foreach (var r in rows)
        {
            var originalName = (r.NameOriginal ?? "").Trim();
            var sku = (r.Sku ?? "").Trim();
            if (string.IsNullOrWhiteSpace(originalName) || string.IsNullOrWhiteSpace(sku)) continue;   // phòng thủ (server đã lọc)
            if (r.RowNo > lastIncludedRow) lastIncludedRow = r.RowNo;

            rowsToUpdate.Add((r.RowNo, originalName, sku));
            rowsByName.TryAdd(originalName, []);
            rowsByName[originalName].Add((r.RowNo, sku));
            if (!skuByName.ContainsKey(originalName))
                skuByName[originalName] = sku;
            if (seen.Add(originalName))
                uniqueNames.Add(originalName);
        }

        return new RewritePlan
        {
            WorkbookPath = "",
            SheetName = sheetName,
            ProductNameColumn = productNameColumn,
            SkuColumn = skuColumn,
            RewrittenNameColumn = rewrittenNameColumn,
            FirstRow = firstRow,
            LastIncludedRow = lastIncludedRow,
            RowsToUpdate = rowsToUpdate,
            UniqueNames = uniqueNames,
            RowsByOriginalName = rowsByName,
            SkuByOriginalName = skuByName,
            SkippedNoName = 0,
            SkippedNoSku = 0,
            SkippedExisting = 0,
        };
    }

    /// <summary>Flush tồn đọng journal fire-and-forget sau POST thành công — nuốt MỌI lỗi (kể cả OCE) để không
    /// sinh unobserved-task-exception; không truyền ct (chạy độc lập với vòng rewrite hiện tại).</summary>
    private static async Task FlushJournalQuietlyAsync(HubClient client)
    {
        try { await PendingRewriteJournal.TryFlushAsync(client).ConfigureAwait(false); } catch { }
    }

    private static void LogEmptyPlanDiagnostics(RewritePlan plan, Action<string> log)
    {
        using var wb = new XLWorkbook(plan.WorkbookPath);
        var ws = ResolveWorksheet(wb, plan.SheetName);

        var sampleRows = new List<int> { plan.FirstRow };
        if (plan.FirstRow > 2)
            sampleRows.Add(plan.FirstRow - 1);
        if (plan.FirstRow + 1 <= plan.LastIncludedRow)
            sampleRows.Add(plan.FirstRow + 1);

        log("📋 Mẫu dữ liệu (cột A=link, D=SKU, F=Tên sp, G=Tên sp đã sửa):");
        foreach (var row in sampleRows.Distinct().OrderBy(r => r))
        {
            var link = TrimCell(ws.Cell(row, 1));
            var sku = TrimCell(ws.Cell(row, plan.SkuColumn));
            var name = TrimCell(ws.Cell(row, plan.ProductNameColumn));
            var rewritten = TrimCell(ws.Cell(row, plan.RewrittenNameColumn));
            log($"   dòng {row}: A={(string.IsNullOrWhiteSpace(link) ? "(trống)" : "có link")}, D={(string.IsNullOrWhiteSpace(sku) ? "(trống)" : sku)}, F={(string.IsNullOrWhiteSpace(name) ? "(trống)" : name)}, G={(string.IsNullOrWhiteSpace(rewritten) ? "(trống)" : rewritten)}");
        }

        if (plan.SkippedNoName > 0 && string.IsNullOrWhiteSpace(TrimCell(ws.Cell(plan.FirstRow, plan.ProductNameColumn))))
            log($"⚠ Từ dòng {plan.FirstRow} chưa có 'Tên sp' (cột F). Cần crawl/nạp dữ liệu (SKU + tên) trước khi rewrite, hoặc đặt Start row ở dòng đã có F và D.");
    }

    private static string TrimCell(IXLCell cell) => (cell.GetValue<string>() ?? "").Trim();

    private static IXLWorksheet ResolveWorksheet(XLWorkbook wb, string sheetName)
    {
        var desired = NormalizeText(sheetName);
        foreach (var ws in wb.Worksheets)
        {
            if (NormalizeText(ws.Name) == desired)
                return ws;
        }
        throw new InvalidOperationException($"Không tìm th?y sheet: {sheetName}");
    }

    private static void EnsureRewrittenNameColumnHeader(IXLWorksheet ws, int rewrittenNameColumn)
    {
        var header = (ws.Cell(1, rewrittenNameColumn).GetValue<string>() ?? "").Trim();
        if (string.IsNullOrWhiteSpace(header))
            ws.Cell(1, rewrittenNameColumn).Value = "Tên sp đã sửa";
    }

    // ====== Name structure + cleaning ======

    private static string NormalizeText(string? value) => (value ?? "").Trim().ToLowerInvariant();


    private sealed record RewritePlan
    {
        public required string WorkbookPath { get; init; }
        public required string SheetName { get; init; }
        public required int ProductNameColumn { get; init; }
        public required int SkuColumn { get; init; }
        public required int RewrittenNameColumn { get; init; }
        public required int FirstRow { get; init; }
        public required int LastIncludedRow { get; init; }
        public required List<(int RowIndex, string OriginalName, string Sku)> RowsToUpdate { get; init; }
        public required List<string> UniqueNames { get; init; }
        public required Dictionary<string, List<(int RowIndex, string Sku)>> RowsByOriginalName { get; init; }
        public required Dictionary<string, string> SkuByOriginalName { get; init; }
        public required int SkippedNoName { get; init; }
        public required int SkippedNoSku { get; init; }
        public required int SkippedExisting { get; init; }
    }

}

