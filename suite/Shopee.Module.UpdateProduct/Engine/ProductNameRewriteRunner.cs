using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClosedXML.Excel;
using Shopee.Core.Ai;

namespace UpdateProduct;

internal sealed class ProductNameRewriteRunner
{
    private const int OpenAiMaxRetries = 3;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

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
        var workbookPath = settings.WorkbookPath?.Trim();
        var sheetName = settings.DataSheet?.Trim();
        var startRow = Math.Max(2, settings.StartRow);
        var endRow = Math.Max(0, settings.EndRow);

        if (string.IsNullOrWhiteSpace(workbookPath) || !File.Exists(workbookPath))
            throw new FileNotFoundException($"Không tìm thấy workbook: {workbookPath}");
        if (string.IsNullOrWhiteSpace(sheetName))
            throw new InvalidOperationException("Thiếu tên sheet.");

        // Dùng cấu hình AI CHUNG ở Cài đặt (provider + model + key) — KHÔNG còn cứng OpenAI.
        var cfg = AiConfigStore.Shared.Current;
        if (!cfg.HasActiveKey)
            throw new InvalidOperationException($"Chưa cấu hình API key cho {cfg.Provider} (mục Cài đặt → Nhà cung cấp AI).");
        var batchSize = Math.Clamp(cfg.BatchSize, 1, 500);

        // Cột Excel theo cấu hình của shop. 0 = "không dùng" → fail rõ ràng (KHÔNG âm thầm rơi về cột
        // mặc định D/F/G — sẽ đọc/ghi nhầm cột). Đây là 3 cột BẮT BUỘC của rewrite.
        var skuCol = settings.SkuColumn;
        var nameCol = settings.ProductNameColumn;
        var rewrittenCol = settings.RewrittenNameColumn;
        if (skuCol <= 0 || nameCol <= 0 || rewrittenCol <= 0)
            throw new InvalidOperationException(
                "Chưa map đủ cột 'SKU' / 'Tên gốc' / 'Tên đã sửa' cho shop (mục BigSeller → Ánh xạ cột).");

        RewritePlan plan;
        using (await WorkbookFileLockHandle.AcquireAsync(workbookPath, ct))
        {
            ct.ThrowIfCancellationRequested();
            plan = BuildPlan(workbookPath, sheetName, startRow, endRow, nameCol, skuCol, rewrittenCol);
        }

        var rangeEnd = plan.LastIncludedRow;
        log($"📝 Rewrite tên (C#): workbook='{workbookPath}', sheet='{plan.SheetName}', rows={plan.FirstRow}-{rangeEnd}");
        log($"📝 AI: {cfg.Provider} · {cfg.ActiveModel} | Batch size: {batchSize}");

        if (plan.RowsToUpdate.Count == 0)
        {
            log($"✓ Không còn dòng cần rewrite (bỏ qua: {plan.SkippedNoName} thiếu cột F 'Tên sp', {plan.SkippedNoSku} thiếu cột D 'SKU', {plan.SkippedExisting} đã có cột G 'Tên sp đã sửa').");
            LogEmptyPlanDiagnostics(plan, log);
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
            var titles = await RequestSeoTitlesWithSplitAsync(cfg, batch, log, ct);
            if (titles.Count != batch.Count)
                throw new InvalidOperationException($"AI trả về số tiêu đề không khớp. Expected={batch.Count}, actual={titles.Count}");

            var updates = new List<(int RowNumber, string RewrittenName)>();
            for (var idx = 0; idx < batch.Count; idx++)
            {
                var originalName = batch[idx];
                var title = SplitNameCode(titles[idx]).Body;   // phòng khi AI lỡ thêm mã code ở cuối

                foreach (var rowEntry in plan.RowsByOriginalName.GetValueOrDefault(originalName, []))
                {
                    // Ghép SKU CỦA MÌNH theo cú pháp "keyword1 - keyword2 product-desc sku", cắt tối đa 120 ký tự (giữ SKU).
                    var finalName = BigSellerText.TruncateProductNamePreservingSku($"{title} {rowEntry.Sku}".Trim(), rowEntry.Sku, 120);
                    if (!string.IsNullOrWhiteSpace(finalName))
                        updates.Add((rowEntry.RowIndex, finalName));
                }
            }

            var batchUpdated = 0;
            var batchLogged = 0;
            const int MaxLogPerBatch = 20;
            var changedRows = new List<int>();
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

            // Đã Save xong batch → báo caller (đẩy ledger) đúng các dòng vừa ghi tên mới.
            foreach (var rn in changedRows) RowsDone?.Invoke(rn, rn);

            log($"💾 Đã save batch {i + 1}-{i + batch.Count}/{plan.UniqueNames.Count}: {batchUpdated} dòng đổi tên.");
            if (batchUpdated > 0)
                log("💾 Batch đã ghi xong — có thể chạy Update product (đóng Excel nếu đang mở file).");
        }

        log($"✓ Xong rewrite tên: {updatedCount} dòng thay đổi. Bỏ qua: {plan.SkippedNoName} thiếu 'Tên sp', {plan.SkippedNoSku} thiếu 'SKU', {plan.SkippedExisting} đã có 'Tên sp đã sửa'.");
    }

    // ── Viết lại TÊN: 1 lần gọi → tiêu đề SEO hoàn chỉnh (dùng AiConfig + prompt SEO trong Cài đặt) ──
    private static async Task<List<string>> RequestSeoTitlesWithSplitAsync(
        AiConfig cfg, List<string> names, Action<string> log, CancellationToken ct)
    {
        try
        {
            // Retry chung ở Core (AiChat): 429/5xx chờ lâu; key/quota/model sai (permanent) ném ngay; JSON hỏng
            // gói thành InvalidOperationException để nhánh chia-đôi bên dưới bắt được như bản cũ.
            return await AiChat.ExecuteWithRetryAsync(
                c => RequestSeoTitlesOnceAsync(cfg, names, c),
                ct,
                maxAttempts: OpenAiMaxRetries,
                label: "OpenAI seo-title",
                log: log,
                mapError: ex => ex is JsonException
                    ? new InvalidOperationException($"OpenAI seo-title JSON lỗi: {ex.Message}", ex)
                    : ex);
        }
        catch (AiHttpException) { throw; }   // key/quota/model sai → dừng, không chia nhỏ
        catch (InvalidOperationException ex)
        {
            if (names.Count <= 1)
            {
                log($"⚠ Viết tên 1 SP thất bại — giữ tên gốc. ({Shorten(ex.Message)})");
                return [names[0]];
            }
            var mid = names.Count / 2;
            log($"⚠ Viết tên batch {names.Count} lỗi — chia đôi ({mid}+{names.Count - mid}). ({Shorten(ex.Message)})");
            var left = await RequestSeoTitlesWithSplitAsync(cfg, names.Take(mid).ToList(), log, ct);
            var right = await RequestSeoTitlesWithSplitAsync(cfg, names.Skip(mid).ToList(), log, ct);
            left.AddRange(right);
            return left;
        }
    }

    private static async Task<List<string>> RequestSeoTitlesOnceAsync(AiConfig cfg, List<string> names, CancellationToken ct)
    {
        // System = prompt SEO người dùng cấu hình (Cài đặt) + đóng gói JSON cho xử lý nhiều sản phẩm.
        var system = cfg.EffectiveNameRewritePrompt +
            "\n\n[XỬ LÝ NHIỀU SẢN PHẨM] Bạn sẽ nhận JSON danh sách {index, name}. Áp dụng đúng quy tắc trên cho TỪNG name. " +
            "CHỈ trả về DUY NHẤT JSON: {\"items\":[{\"index\":0,\"title\":\"<tiêu đề SEO, KHÔNG kèm SKU>\"}]} — đủ mỗi index input, " +
            "title chỉ 1 dòng, không kèm giải thích/ghi chú/rào ```.";
        var user = JsonSerializer.Serialize(
            new { items = names.Select((name, index) => new { index, name }).ToList() }, JsonOptions);

        var outputText = await AiJsonAsync(cfg, system, user, ct, temperature: 0.4);

        using var parsed = JsonDocument.Parse(outputText);
        var items = parsed.RootElement.GetProperty("items").EnumerateArray().ToList();
        var byIndex = new Dictionary<int, string>();
        foreach (var item in items)
        {
            var idx = item.GetProperty("index").GetInt32();
            var title = (item.GetProperty("title").GetString() ?? "")
                .Replace('\n', ' ').Replace('\r', ' ').Trim();
            byIndex[idx] = title;
        }

        var result = new List<string>(names.Count);
        for (var i = 0; i < names.Count; i++)
        {
            if (!byIndex.TryGetValue(i, out var t) || string.IsNullOrWhiteSpace(t))
                throw new InvalidOperationException($"AI thiếu title index={i}.");
            result.Add(t);
        }
        return result;
    }


    /// <summary>Gọi AI (đa provider qua AiChat) yêu cầu trả JSON, rồi trích object JSON từ text trả về.
    /// Dùng cho cả 2 bước (parse cấu trúc + viết lại) — provider/model/key lấy từ AiConfig (Cài đặt).</summary>
    private static async Task<string> AiJsonAsync(AiConfig cfg, string system, string user, CancellationToken ct, double temperature = 0)
    {
        var text = await AiChat.CompleteAsync(cfg, system, user, ct, temperature, maxTokens: 8192).ConfigureAwait(false);
        var json = ExtractJsonObject(text);
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("AI không trả về JSON hợp lệ: " + Shorten(text));
        return json;
    }

    /// <summary>Trích object JSON {...} đầu→cuối từ text (bỏ rào ```json hoặc lời dẫn nếu model thêm).</summary>
    private static string ExtractJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : "";
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

    // ====== Name structure + cleaning (ported) ======

    private static string NormalizeText(string? value) => (value ?? "").Trim().ToLowerInvariant();

    private static string NormalizeDash(string text)
        => System.Text.RegularExpressions.Regex.Replace((text ?? "").Trim(), "\\s*[–—-]\\s*", " - ");

    private static (string Body, string? Code) SplitNameCode(string productName)
    {
        var normalized = NormalizeDash(productName);
        var match = System.Text.RegularExpressions.Regex.Match(normalized, "\\s+-\\s+([A-Z]\\d+)\\s*$");
        if (!match.Success)
            return (normalized, null);
        var body = normalized[..match.Index].Trim();
        return (body, match.Groups[1].Value);
    }


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

