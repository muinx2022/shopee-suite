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

    private static async Task RunAsync(BigSellerWorkflowSettings settings, Action<string> log, CancellationToken ct)
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
                    var finalName = TruncateProductNamePreservingSku($"{title} {rowEntry.Sku}".Trim(), rowEntry.Sku, 120);
                    if (!string.IsNullOrWhiteSpace(finalName))
                        updates.Add((rowEntry.RowIndex, finalName));
                }
            }

            var batchUpdated = 0;
            var batchLogged = 0;
            const int MaxLogPerBatch = 20;
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
            return await ExecuteWithRetryAsync(() => RequestSeoTitlesOnceAsync(cfg, names, ct), "seo-title", log, ct);
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

    private static async Task<List<ParsedStructure>> RequestParsedStructuresWithSplitAsync(
        AiConfig cfg,
        List<string> productNames,
        Action<string> log,
        CancellationToken ct)
    {
        try
        {
            return await ExecuteWithRetryAsync(
                () => RequestParsedStructuresOnceAsync(cfg, productNames, ct),
                "parse", log, ct);
        }
        catch (AiHttpException)
        {
            // Lỗi cấu hình/quyền (key/quota/model) lặp y nguyên ở mọi cỡ batch — không chia nhỏ, fail rõ ràng.
            throw;
        }
        catch (InvalidOperationException ex)
        {
            if (productNames.Count <= 1)
            {
                log($"⚠ Parse 1 tên thất bại — dùng heuristic tách tên. ({Shorten(ex.Message)})");
                return [InferProductNameStructure(productNames[0])];
            }

            var middle = productNames.Count / 2;
            log($"⚠ Parse batch {productNames.Count} tên lỗi — chia đôi thử lại ({middle}+{productNames.Count - middle}). ({Shorten(ex.Message)})");
            var left = await RequestParsedStructuresWithSplitAsync(cfg, productNames.Take(middle).ToList(), log, ct);
            var right = await RequestParsedStructuresWithSplitAsync(cfg, productNames.Skip(middle).ToList(), log, ct);
            left.AddRange(right);
            return left;
        }
    }

    /// <summary>
    /// Retry chung cho m?i request OpenAI: b?t c? l?i m?ng (HttpRequestException) và
    /// timeout HttpClient (TaskCanceledException không ph?i do user d?ng) — tru?c dây
    /// hai lo?i này l?t qua retry làm ch?t run ngay l?n l?i d?u tiên.
    /// 429/5xx ch? lâu hon (15s/30s) d? qua rate limit thay vì 2s/4s.
    /// </summary>
    private static async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> action,
        string label,
        Action<string> log,
        CancellationToken ct)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= OpenAiMaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await action();
            }
            catch (Exception ex) when (IsTransientOpenAiError(ex, ct))
            {
                lastError = ex is JsonException
                    ? new InvalidOperationException($"OpenAI {label} JSON lỗi: {ex.Message}", ex)
                    : ex;
                if (attempt == OpenAiMaxRetries)
                    break;

                var delay = ComputeRetryDelay(ex, attempt);
                log($"⚠ OpenAI {label} lỗi (lần {attempt}/{OpenAiMaxRetries}): {Shorten(ex.Message)} — thử lại sau {delay.TotalSeconds:0}s.");
                await Task.Delay(delay, ct);
            }
        }

        throw lastError ?? new InvalidOperationException($"OpenAI {label} request failed.");
    }

    private static bool IsTransientOpenAiError(Exception ex, CancellationToken ct) =>
        ex is InvalidOperationException or JsonException or HttpRequestException ||
        (ex is AiHttpException ah && !ah.IsPermanent) ||   // 429/5xx → retry; key/quota/model (permanent) → dừng
        (ex is TaskCanceledException && !ct.IsCancellationRequested);

    private static TimeSpan ComputeRetryDelay(Exception ex, int attempt) =>
        ex is OpenAiHttpException { IsRateLimitOrServer: true }
            or AiHttpException { StatusCode: 429 } or AiHttpException { StatusCode: >= 500 }
            ? TimeSpan.FromSeconds(15 * attempt)
            : TimeSpan.FromSeconds(2 * attempt);

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

    private sealed class OpenAiHttpException(int statusCode, string message) : InvalidOperationException(message)
    {
        public int StatusCode { get; } = statusCode;
        public bool IsRateLimitOrServer => StatusCode == 429 || StatusCode >= 500;
    }

    private const string ParseSystemPrompt =
        "Bạn sẽ nhận vào danh sách tên sản phẩm (tiếng Việt). " +
        "Hãy tách mỗi tên thành 4 trường: keyword_1, keyword_2, description, product_code. " +
        "keyword_1 là cụm từ chỉ loại sản phẩm chính và phải đủ cụ thể (ví dụ: 'Giày cao gót nữ', 'Sandal nữ'). " +
        "Không chọn keyword_1 quá chung chung chỉ là 'Giày'/'Dép' nếu trong tên có cụm cụ thể hơn. " +
        "keyword_2 chỉ là phân loại phụ NGẮN (ví dụ: 'Sandal nữ', 'Giày búp bê', 'Boots nữ'), có thể rỗng. " +
        "Không đưa chất liệu/đặc điểm/đối tượng/công dụng vào keyword_2 (ví dụ: kim tuyến, đính đá, quai ngọc, gót trong, đế vuông, cô dâu, đi tiệc, 5cm/7cm/8cm...). " +
        "Các phần đó phải để trong description. " +
        "description là phần còn lại (đặc điểm), có thể rỗng; chỉ tách, không viết lại. " +
        "product_code là mã sản phẩm nếu có (ví dụ B90429), nếu không có thì để rỗng. " +
        "Bắt buộc trả về đủ một item cho mỗi index input (0 đến N-1), không được bỏ sót index nào. " +
        "Không được bịa thêm thông tin mới. " +
        "CHỈ trả về JSON dạng: {\"items\":[{\"index\":0,\"keyword_1\":\"...\",\"keyword_2\":\"...\",\"description\":\"...\",\"product_code\":\"...\"}]} — không kèm giải thích, không rào ```.";

    private static async Task<List<ParsedStructure>> RequestParsedStructuresOnceAsync(
        AiConfig cfg,
        List<string> productNames,
        CancellationToken ct)
    {
        var userPrompt = JsonSerializer.Serialize(
            new { items = productNames.Select((name, index) => new { index, product_name = name }).ToList() }, JsonOptions);

        var outputText = await AiJsonAsync(cfg, ParseSystemPrompt, userPrompt, ct);

        using var parsed = JsonDocument.Parse(outputText);
        var items = parsed.RootElement.GetProperty("items").EnumerateArray().ToList();
        var byIndex = new Dictionary<int, ParsedStructure>();
        foreach (var item in items)
        {
            var idx = item.GetProperty("index").GetInt32();
            byIndex[idx] = new ParsedStructure
            {
                Keyword1 = (item.GetProperty("keyword_1").GetString() ?? "").Trim(),
                Keyword2 = (item.GetProperty("keyword_2").GetString() ?? "").Trim(),
                Description = (item.GetProperty("description").GetString() ?? "").Trim(),
                ProductCode = (item.GetProperty("product_code").GetString() ?? "").Trim(),
            };
        }

        var result = new List<ParsedStructure>(productNames.Count);
        for (var i = 0; i < productNames.Count; i++)
        {
            if (!byIndex.TryGetValue(i, out var s))
                throw new InvalidOperationException($"OpenAI parse thi?u item index={i}.");
            if (string.IsNullOrWhiteSpace(s.Keyword1))
                s = s with { Keyword1 = productNames[i].Trim() };
            result.Add(s);
        }

        return result;
    }

    private static async Task<List<string>> RequestRewrittenDescriptionsWithSplitAsync(
        AiConfig cfg,
        List<ParsedProduct> products,
        int versionCount,
        Action<string> log,
        CancellationToken ct)
    {
        try
        {
            return await ExecuteWithRetryAsync(
                () => RequestRewrittenDescriptionsOnceAsync(cfg, products, versionCount, ct),
                "rewrite", log, ct);
        }
        catch (AiHttpException)
        {
            // Lỗi cấu hình/quyền lặp y nguyên ở mọi cỡ batch — không chia nhỏ, fail rõ ràng.
            throw;
        }
        catch (InvalidOperationException ex)
        {
            if (products.Count <= 1)
            {
                // 1 item hỏng không đáng giết cả run — giữ mô tả gốc, EnsureSafeDescription
                // phía trên sẽ cleanup + cắt theo budget như bình thường.
                log($"⚠ Rewrite 1 sản phẩm thất bại — giữ mô tả gốc. ({Shorten(ex.Message)})");
                return [products[0].Description ?? ""];
            }

            var middle = products.Count / 2;
            log($"⚠ Rewrite batch {products.Count} sản phẩm lỗi — chia đôi thử lại ({middle}+{products.Count - middle}). ({Shorten(ex.Message)})");
            var left = await RequestRewrittenDescriptionsWithSplitAsync(
                cfg, ReindexParsedProducts(products.Take(middle).ToList()), versionCount, log, ct);
            var right = await RequestRewrittenDescriptionsWithSplitAsync(
                cfg, ReindexParsedProducts(products.Skip(middle).ToList()), versionCount, log, ct);
            left.AddRange(right);
            return left;
        }
    }

    private static async Task<List<string>> RequestRewrittenDescriptionsOnceAsync(
        AiConfig cfg,
        List<ParsedProduct> products,
        int versionCount,
        CancellationToken ct)
    {
        // System prompt = prompt người dùng cấu hình ở Cài đặt (BuildRewriteInstructions) + yêu cầu JSON.
        var system = BuildRewriteInstructions(versionCount) +
            " CHỈ trả về JSON dạng: {\"items\":[{\"index\":0,\"rewritten_descriptions\":[\"...\"]}]} — không kèm giải thích, không rào ```.";
        var userPrompt = JsonSerializer.Serialize(new { version_count = versionCount, products }, JsonOptions);

        var outputText = await AiJsonAsync(cfg, system, userPrompt, ct, temperature: 0.2);

        using var parsed = JsonDocument.Parse(outputText);
        var items = parsed.RootElement.GetProperty("items").EnumerateArray().ToList();

        var byIndex = new Dictionary<int, List<string>>();
        foreach (var item in items)
        {
            var index = item.GetProperty("index").GetInt32();
            var arr = item.GetProperty("rewritten_descriptions").EnumerateArray().Select(x => (x.GetString() ?? "").Trim()).ToList();
            byIndex[index] = arr;
        }

        var results = new List<string>(products.Count);
        for (var i = 0; i < products.Count; i++)
        {
            if (!byIndex.TryGetValue(i, out var arr) || arr.Count == 0)
                throw new InvalidOperationException($"OpenAI thi?u item index={i}.");
            results.Add(ChooseBestRewrite(arr, products[i]));
        }

        return results;
    }

    private static List<ParsedProduct> ReindexParsedProducts(List<ParsedProduct> products)
        => products.Select((product, index) => product with { Index = index }).ToList();

    private static string ChooseBestRewrite(List<string> candidates, ParsedProduct product)
    {
        var keyword1 = product.Keyword1 ?? "";
        var keyword2 = product.Keyword2 ?? "";
        var originalDesc = product.Description ?? "";
        var maxChars = product.MaxDescriptionChars;

        var structure = new ParsedStructure
        {
            Keyword1 = keyword1,
            Keyword2 = keyword2,
            Description = originalDesc,
            ProductCode = product.ProductCode ?? "",
        };

        var best = candidates.FirstOrDefault() ?? "";
        var bestScore = double.NegativeInfinity;
        foreach (var c in candidates)
        {
            var cleaned = CleanupDescription(c);
            if (string.IsNullOrWhiteSpace(cleaned))
                continue;

            var score = ScoreRewriteCandidate(cleaned, structure, maxChars);
            if (score > bestScore)
            {
                bestScore = score;
                best = cleaned;
            }
        }

        return best;
    }

    private static double ScoreRewriteCandidate(string desc, ParsedStructure structure, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(desc))
            return -1e9;

        var cleaned = CleanupDescription(desc);
        if (IsBadRewrittenDescription(cleaned))
            return -1e6;

        // Prefer using budget instead of being too short.
        var len = cleaned.Length;
        var target = Math.Max(12, maxChars);
        var ratio = target <= 0 ? 0 : Math.Min(1.0, (double)len / target);
        var lengthScore = ratio * 100.0;

        // Prefer Vietnamese diacritics (non-ascii).
        var nonAscii = cleaned.Count(ch => ch > 127);
        var diacriticsScore = Math.Min(30.0, nonAscii);

        // Penalize if contains keyword1/keyword2 verbatim.
        var norm = NormalizeText(cleaned);
        var penalty = 0.0;
        if (!string.IsNullOrWhiteSpace(structure.Keyword1) && norm.Contains(NormalizeText(structure.Keyword1)))
            penalty += 40;
        if (!string.IsNullOrWhiteSpace(structure.Keyword2) && norm.Contains(NormalizeText(structure.Keyword2)))
            penalty += 20;

        return lengthScore + diacriticsScore - penalty;
    }

    private static string ExtractResponseText(JsonElement root)
    {
        // Prefer output_text if present (common in Responses API).
        if (root.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
            return outputText.GetString() ?? "";

        // Fallback: traverse output[].content[] for text.
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            return "";

        var sb = new StringBuilder();
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var c in content.EnumerateArray())
            {
                if (c.TryGetProperty("type", out var type) && type.GetString() == "output_text" &&
                    c.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    sb.Append(text.GetString());
                }
            }
        }
        return sb.ToString();
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

    private static string BuildRewriteInstructions(int versionCount)
    {
        // Prompt người dùng cấu hình ở Cài đặt → System Prompt (rỗng = mặc định). Thay {versionCount}.
        var custom = Shopee.Core.Ai.AiConfigStore.Shared.Current.EffectiveNameRewritePrompt;
        if (!string.IsNullOrWhiteSpace(custom))
        {
            return custom.Contains("{versionCount}", StringComparison.Ordinal)
                ? custom.Replace("{versionCount}", versionCount.ToString())
                : $"{custom} Với mỗi sản phẩm tạo đúng {versionCount} rewritten_description.";
        }
        return
            "Mình s? g?i danh sách s?n ph?m dã du?c tách thành keyword_1, keyword_2, description, product_code " +
            "và max_description_chars. " +
            "Hãy dùng keyword_1 và keyword_2 d? hi?u dúng ng? c?nh s?n ph?m, sau dó CH? vi?t l?i ph?n description. " +
            $"V?i m?i s?n ph?m, t?o dúng {versionCount} rewritten_description m?i (các phuong án khác nhau). " +
            "Không du?c d?i keyword_1, keyword_2, product_code. " +
            "Không du?c tr? v? full product_name, ch? tr? v? rewritten_description. " +
            "Rewritten_description b?t bu?c có d? dài <= max_description_chars c?a t?ng item (gi?i h?n KÝ T?, không ph?i s? t?). " +
            "B?t bu?c vi?t ti?ng Vi?t CÓ D?U, không du?c vi?t không d?u/telex. " +
            "Gi? ch? hoa/ thu?ng t? nhiên (không vi?t toàn b? ch? thu?ng). " +
            "Rewritten_description nên c? g?ng dùng g?n h?t ngân sách ký t? (kho?ng 70% d?n 100% max_description_chars), tránh quá ng?n. " +
            "Rewritten_description ph?i là c?m t? mô t? d?c di?m tr?c ti?p c?a s?n ph?m, ki?u title. " +
            "Ðu?c phép d?a vào keyword_1 và keyword_2 d? hi?u lo?i s?n ph?m, nhung không du?c l?p nguyên van keyword_1 ho?c keyword_2 trong rewritten_description. " +
            "Ch? gi? 2 d?n 5 d?c di?m n?i b?t nh?t t? description g?c, nhung ph?i DI?N Ð?T L?I (paraphrase), không ch? xóa b?t t?. " +
            "Không du?c gi? nguyên c?m t? dài liên ti?p t? description g?c; uu tiên d?o tr?t t? c?m t? và dùng t? d?ng nghia. " +
            "Không b?t d?u rewritten_description b?ng các t? nhu giày, dôi giày, dép, sandal, boots. " +
            "Bám sát ý nghia description g?c, không thêm ý m?i. " +
            "Không dùng câu qu?ng cáo/generic nhu d? ph?i d?, phù h?p, k?t h?p trang ph?c, h?ng ngày, thanh l?ch, nh? nhàng, êm ái, ki?u dáng, phong cách, hoàn h?o, l?a ch?n tuy?t v?i, m?i d?p. " +
            "Không du?c dua product_code vào rewritten_description. " +
            "Không dùng d?u ph?y ho?c d?u ch?m trong rewritten_description. " +
            "Ví d? output h?p l?: rewritten_description='Ren lu?i dính dá thoáng khí n? tính'. " +
            "Ví d? output không h?p l?: 'Giày B?t N? - Giày Búp Bê ... - B91763'. " +
            "M?i item output ph?i gi? dúng index c?a item input tuong ?ng.";
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

    private static readonly (string, string)[] AttributeStartPatterns =
    [
        ("cao", ""),
        ("êm", "chân"),
        ("da", "m?m"),
        ("dính", "no"),
        ("dây", "cài"),
        ("quai", "cài"),
        ("may", "vi?n"),
        ("hot", "trend"),
        ("d?", "ph?i"),
        ("tôn", "dáng"),
        ("phong", "cách"),
    ];

    private static bool StartsWithPattern(string[] words, int index, (string, string) pattern)
    {
        var p1 = pattern.Item1;
        var p2 = pattern.Item2;
        if (string.IsNullOrWhiteSpace(p2))
            return index < words.Length && NormalizeText(words[index].Trim(" ,.;:".ToCharArray())) == p1;
        if (index + 1 >= words.Length)
            return false;
        return NormalizeText(words[index].Trim(" ,.;:".ToCharArray())) == p1
               && NormalizeText(words[index + 1].Trim(" ,.;:".ToCharArray())) == p2;
    }

    private static (string LockedContext, string? Code) InferLockedContext(string productName)
    {
        var (body, code) = SplitNameCode(productName);
        var parts = body.Split(" - ", 2, StringSplitOptions.None);
        if (parts.Length == 1)
            return (body, code);

        var firstPart = parts[0];
        var secondPart = parts[1];
        var secondWords = secondPart.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var stopIndex = secondWords.Length;
        for (var idx = 2; idx < secondWords.Length; idx++)
        {
            if (AttributeStartPatterns.Any(p => StartsWithPattern(secondWords, idx, p)))
            {
                stopIndex = idx;
                break;
            }
        }

        var lockedSecond = string.Join(" ", secondWords.Take(stopIndex)).Trim();
        return ($"{firstPart.Trim()} - {lockedSecond}".Trim(), code);
    }

    private static ParsedStructure InferProductNameStructure(string productName)
    {
        var (lockedContext, code) = InferLockedContext(productName);
        var parts = lockedContext.Split(" - ", 2, StringSplitOptions.None);
        var keyword1 = parts.Length == 2 ? parts[0] : lockedContext;
        var keyword2 = parts.Length == 2 ? parts[1] : "";

        var (body, _) = SplitNameCode(productName);
        var description = body;
        if (!string.IsNullOrWhiteSpace(keyword2))
        {
            var prefix = $"{keyword1} - {keyword2}";
            if (body.StartsWith(prefix, StringComparison.Ordinal))
                description = body[prefix.Length..].Trim();
        }
        else if (body.StartsWith(keyword1, StringComparison.Ordinal))
        {
            description = body[keyword1.Length..].Trim();
        }

        return new ParsedStructure
        {
            Keyword1 = keyword1.Trim(),
            Keyword2 = keyword2.Trim(),
            Description = description.Trim(' ', '-'),
            ProductCode = (code ?? "").Trim(),
        };
    }

    // keyword_2 nên ng?n (phân lo?i), không nh?i thu?c tính.
    private const int MaxKeyword2Words = 3;

    private static ParsedStructure NormalizeParsedStructureForRewrite(ParsedStructure structure)
    {
        structure = MoveAttributeWordsOutOfKeyword2(structure);

        var keyword2Words = (structure.Keyword2 ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (keyword2Words.Length <= MaxKeyword2Words)
            return structure;

        var shortKeyword2 = string.Join(" ", keyword2Words.Take(MaxKeyword2Words)).Trim();
        var overflow = string.Join(" ", keyword2Words.Skip(MaxKeyword2Words)).Trim();
        var currentDescription = structure.Description ?? "";

        var merged =
            (!string.IsNullOrWhiteSpace(overflow) && !string.IsNullOrWhiteSpace(currentDescription))
                ? $"{overflow} {currentDescription}".Trim()
                : (NullIfEmpty(overflow) ?? currentDescription);

        merged = System.Text.RegularExpressions.Regex.Replace(merged, "\\s+", " ").Trim(' ', '-');

        return structure with { Keyword2 = shortKeyword2, Description = merged };
    }

    private static ParsedStructure MoveAttributeWordsOutOfKeyword2(ParsedStructure structure)
    {
        var keyword2 = (structure.Keyword2 ?? "").Trim();
        if (string.IsNullOrWhiteSpace(keyword2))
            return structure;

        // If keyword_2 accidentally contains attributes, move those parts into description.
        // Example we want: keyword_2="Sandal N?", attributes like "kim tuy?n/gót trong/d? vuông..." -> description.
        var attributeStarts = new[]
        {
            "kim", "tuy?n", "kim tuy?n",
            "dính", "dá", "dính dá",
            "quai", "ng?c", "quai ng?c",
            "gót", "trong", "gót trong",
            "d?", "vuông", "d? vuông",
            "cao", "cm",
        };

        var words = keyword2.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count <= 2)
            return structure;

        var cutIndex = -1;
        for (var i = 0; i < words.Count; i++)
        {
            var w = NormalizeText(words[i]);
            if (attributeStarts.Contains(w))
            {
                cutIndex = i;
                break;
            }
        }

        // Special-case: if starts with "Sandal N? ..." keep exactly first 2 words.
        if (cutIndex < 0)
        {
            var prefix2 = string.Join(" ", words.Take(2));
            if (NormalizeText(prefix2) is "sandal n?" or "sandal nu")
                cutIndex = 2;
        }

        if (cutIndex <= 1)
            return structure;

        var kept = string.Join(" ", words.Take(cutIndex)).Trim();
        var moved = string.Join(" ", words.Skip(cutIndex)).Trim();
        if (string.IsNullOrWhiteSpace(moved))
            return structure with { Keyword2 = kept };

        var desc = (structure.Description ?? "").Trim();
        var merged = string.IsNullOrWhiteSpace(desc) ? moved : $"{moved} {desc}";
        merged = System.Text.RegularExpressions.Regex.Replace(merged, "\\s+", " ").Trim(' ', '-');
        return structure with { Keyword2 = kept, Description = merged };
    }

    private static int CalculateDescriptionCharBudget(ParsedStructure structure, string sku, int maxLength)
    {
        var keyword1 = (structure.Keyword1 ?? "").Trim();
        var keyword2 = (structure.Keyword2 ?? "").Trim();
        sku = (sku ?? "").Trim();

        var prefix = keyword2.Length > 0 ? $"{keyword1} - {keyword2}".Trim() : keyword1;
        var fixedLen = prefix.Length + sku.Length;
        if (prefix.Length > 0 && sku.Length > 0) fixedLen += 2;
        else if (prefix.Length > 0 || sku.Length > 0) fixedLen += 1;

        return Math.Max(0, maxLength - fixedLen);
    }

    private static string ComposeProductName(ParsedStructure structure, string description)
    {
        var keyword1 = (structure.Keyword1 ?? "").Trim();
        var keyword2 = (structure.Keyword2 ?? "").Trim();
        var productCode = (structure.ProductCode ?? "").Trim();
        var finalDescription = (description ?? "").Trim();

        var parts = new List<string> { keyword1 };
        if (!string.IsNullOrWhiteSpace(keyword2))
            parts.Add(keyword2);

        var body = string.Join(" - ", parts.Where(p => !string.IsNullOrWhiteSpace(p))).Trim();
        if (!string.IsNullOrWhiteSpace(finalDescription))
            body = $"{body} {finalDescription}".Trim();
        if (!string.IsNullOrWhiteSpace(productCode))
            return $"{body} - {productCode}";
        return body;
    }

    private static string CleanupDescription(string? description)
    {
        var cleaned = (description ?? "").Trim();
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, "[,.]", " ");

        var patterns = new[]
        {
            "\\bd? ph?i(?: d?| trang ph?c| outfit)?\\b",
            "\\bde phoi(?: do| trang phuc| outfit)?\\b",
            "\\bd? dàng ph?i(?: d?| trang ph?c| outfit)?\\b",
            "\\bde dang phoi(?: do| trang phuc| outfit)?\\b",
            "\\bh?ng ngày\\b",
            "\\bhang ngay\\b",
            "\\bphong cách\\b",
            "\\bphong cach\\b",
            "\\bthanh l?ch\\b",
            "\\bthanh lich\\b",
            "\\bnh? nhàng\\b",
            "\\bnhe nhang\\b",
            "\\bêm ái\\b",
            "\\bem ai\\b",
            "\\bki?u dáng\\b",
            "\\bkieu dang\\b",
            "\\bdáng v?\\b",
            "\\bdang ve\\b",
            "\\bl?a ch?n tuy?t v?i\\b",
            "\\blua chon tuyet voi\\b",
            "\\bhoàn h?o\\b",
            "\\bhoan hao\\b",
            "\\bm?i d?p\\b",
            "\\bmoi dip\\b",
            "\\bmang l?i s?\\b",
            "\\bmang lai su\\b",
            "\\bti?n d?ng\\b",
            "\\btien dung\\b",
            "\\bd? k?t h?p\\b",
            "\\bde ket hop\\b",
        };
        foreach (var p in patterns)
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, p, " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, "\\s+", " ").Trim(' ', ',', '.', '-', '–', '—');
        return cleaned;
    }

    private static string EnsureSafeDescription(string description, ParsedStructure structure, int maxChars)
    {
        var desc = (description ?? "").Trim();
        desc = CleanupDescription(desc);

        if (IsBadRewrittenDescription(desc))
            desc = CleanupDescription(structure.Description ?? "");

        var normalized = NormalizeText(desc);
        var keyword1 = NormalizeText(structure.Keyword1);
        var keyword2 = NormalizeText(structure.Keyword2);
        var productCode = NormalizeText(structure.ProductCode);

        if (!string.IsNullOrWhiteSpace(keyword1) && normalized.Contains(keyword1))
            desc = CleanupDescription(structure.Description ?? "");
        else if (!string.IsNullOrWhiteSpace(keyword2) && normalized.Contains(keyword2))
            desc = CleanupDescription(structure.Description ?? "");

        if (!string.IsNullOrWhiteSpace(productCode) && NormalizeText(desc).Contains(productCode))
            desc = CleanupDescription(System.Text.RegularExpressions.Regex.Replace(desc, System.Text.RegularExpressions.Regex.Escape(structure.ProductCode ?? ""), " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase));

        desc = LimitTextByCharsWithoutCuttingWords(desc, maxChars);
        return desc;
    }

    private static bool IsBadRewrittenDescription(string? description)
    {
        var normalized = NormalizeText(description);
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 6)
            return true;

        var blockedContains = new[]
        {
            ",",
            ".",
            " và ",
            "d? dàng",
            "de dang",
            "phù h?p",
            "phu hop",
            "k?t h?p",
            "ket hop",
            "trang ph?c",
            "trang phuc",
            "hàng ngày",
            "hang ngay",
            "ki?u dáng",
            "kieu dang",
            "phong cách",
            "phong cach",
            "thanh l?ch",
            "thanh lich",
            "nh? nhàng",
            "nhe nhang",
            "êm ái",
            "em ai",
            "dáng yêu",
            "dang yeu",
            "hoàn h?o",
            "hoan hao",
            "l?a ch?n",
            "lua chon",
            "tuy?t v?i",
            "tuyet voi",
            "m?i d?p",
            "moi dip",
            "dành cho",
            "danh cho",
        };
        if (blockedContains.Any(p => normalized.Contains(p)))
            return true;

        var last = words.Length > 0 ? words[^1] : "";
        if (new[] { "cho", "de", "d?", "voi", "v?i", "cung", "cùng", "va", "và" }.Contains(last))
            return true;

        var genericPrefixes = new[] { "giày ", "giay ", "dép ", "dep ", "sandal ", "boots ", "boot " };
        if (genericPrefixes.Any(p => normalized.StartsWith(p)))
            return true;

        // Heuristic: if almost no non-ascii chars, likely "không d?u"
        var nonAscii = (description ?? "").Count(ch => ch > 127);
        if (nonAscii <= 1)
            return true;

        return false;
    }

    private static string LimitTextByCharsWithoutCuttingWords(string text, int maxChars)
    {
        text = (text ?? "").Trim();
        if (maxChars <= 0)
            return "";
        if (text.Length <= maxChars)
            return text;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 0 && string.Join(" ", words).Length > maxChars)
            words.RemoveAt(words.Count - 1);
        return string.Join(" ", words).Trim();
    }

    private static string TruncateProductNamePreservingSku(string productName, string sku, int maxLength)
    {
        productName = (productName ?? "").Trim();
        sku = (sku ?? "").Trim();
        if (productName.Length <= maxLength)
            return productName;

        if (!string.IsNullOrWhiteSpace(sku) && productName.EndsWith(sku, StringComparison.Ordinal))
        {
            var body = productName[..^sku.Length].Trim();
            var words = body.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            while (words.Count > 0)
            {
                var candidate = $"{string.Join(" ", words)} {sku}".Trim();
                if (candidate.Length <= maxLength)
                    return candidate;
                words.RemoveAt(words.Count - 1);
            }
            return sku.Length <= maxLength ? sku : sku[..maxLength].Trim();
        }

        var allWords = productName.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (allWords.Count > 0)
        {
            var candidate = string.Join(" ", allWords).Trim();
            if (candidate.Length <= maxLength)
                return candidate;
            allWords.RemoveAt(allWords.Count - 1);
        }
        // Fallback: 1 từ duy nhất dài hơn maxLength → cắt cứng thay vì trả "" (title rỗng làm hỏng sản phẩm).
        // Khớp hành vi bản trong BigSellerProductUpdateRunner.
        return productName[..Math.Min(maxLength, productName.Length)].Trim();
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

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

    private sealed record ParsedProduct
    {
        [JsonPropertyName("index")] public int Index { get; init; }
        [JsonPropertyName("keyword_1")] public string Keyword1 { get; init; } = "";
        [JsonPropertyName("keyword_2")] public string Keyword2 { get; init; } = "";
        [JsonPropertyName("description")] public string Description { get; init; } = "";
        [JsonPropertyName("product_code")] public string ProductCode { get; init; } = "";
        [JsonPropertyName("max_description_chars")] public int MaxDescriptionChars { get; init; }
    }

    private sealed record ParsedStructure
    {
        public string Keyword1 { get; init; } = "";
        public string Keyword2 { get; init; } = "";
        public string Description { get; init; } = "";
        public string ProductCode { get; init; } = "";
    }
}

