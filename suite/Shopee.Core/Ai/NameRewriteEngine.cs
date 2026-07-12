namespace Shopee.Core.Ai;

/// <summary>
/// LÕI THUẦN của việc viết lại TÊN sản phẩm (tách từ <c>ProductNameRewriteRunner</c> để dùng chung client ↔ hub):
/// nhận danh sách TÊN GỐC unique → gọi AI (đa provider qua <see cref="AiConfig"/> + prompt SEO cấu hình trên Hub)
/// → tiêu đề SEO hoàn chỉnh (retry/chia-đôi/fallback khi AI trả thiếu, GIỮ NGUYÊN từng byte so với runner cũ), rồi
/// ghép SKU + cắt ≤120 ký tự (giữ SKU) qua <see cref="_truncate"/>.
///
/// KHÔNG đụng workbook/Postgres/ledger — 100% thuần → test độc lập bằng cách inject <see cref="AiCompleter"/> giả
/// (stub trả JSON mẫu) + <see cref="_truncate"/>. Client (runner) và hub cùng gọi engine này:
///  · <see cref="RewriteTitlesAsync"/> = tiêu đề THÔ theo từng batch (dedup + gọi AI 1 lần/tên) — caller ghép SKU
///    PER-DÒNG (mỗi dòng cùng tên gốc có thể SKU khác nhau) qua <see cref="ComposeFinalName"/>.
///  · <see cref="RewriteAsync"/> = tiện ích map tên-gốc → tên-mới-đã-ghép-SKU-cắt-120 (dùng SKU đại diện mỗi tên).
/// </summary>
public sealed class NameRewriteEngine
{
    /// <summary>Số lần thử mặc định (gồm lần đầu) cho một lệnh gọi AI seo-title (khớp runner cũ).</summary>
    private const int OpenAiMaxRetries = 3;
    /// <summary>Trần ký tự tên-sửa (khớp ProductNameRewriteRunner + ProductDb.RewrittenMaxLen).</summary>
    public const int MaxNameLength = 120;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Một lượt gọi chat AI (system + user → text). Mặc định ủy cho <see cref="AiChat.CompleteAsync"/>;
    /// test inject stub trả JSON mẫu để kiểm parse/ghép SKU mà KHÔNG chạm mạng.</summary>
    public delegate Task<string> AiCompleter(
        AiConfig cfg, string system, string user, double temperature, int maxTokens, CancellationToken ct);

    private readonly AiConfig _cfg;
    private readonly AiCompleter _complete;
    private readonly Func<string, string, int, string> _truncate;
    private readonly Func<int, CancellationToken, Task>? _retryDelay;

    /// <param name="cfg">Cấu hình AI (provider/model/key/prompt SEO) — nguồn Hub (config/ai.json).</param>
    /// <param name="truncate">Hàm cắt tên giữ SKU ở đuôi (client + hub cùng truyền
    /// <c>BigSellerText.TruncateProductNamePreservingSku</c> — file static thuần BCL đã link 2 nơi).</param>
    /// <param name="complete">Bộ gọi AI (null = <see cref="AiChat.CompleteAsync"/>). Test inject stub.</param>
    /// <param name="retryDelay">Hàm chờ giữa các lần retry (null = <c>Task.Delay</c> như runner cũ). Test inject no-op.</param>
    public NameRewriteEngine(
        AiConfig cfg,
        Func<string, string, int, string> truncate,
        AiCompleter? complete = null,
        Func<int, CancellationToken, Task>? retryDelay = null)
    {
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        _truncate = truncate ?? throw new ArgumentNullException(nameof(truncate));
        _complete = complete ?? ((c, s, u, t, m, ct) => AiChat.CompleteAsync(c, s, u, ct, t, m));
        _retryDelay = retryDelay;
    }

    /// <summary>Ghép SKU của MÌNH vào tiêu đề SEO thô rồi cắt ≤120 (giữ SKU). Bỏ mã code AI lỡ thêm ở đuôi tiêu đề
    /// (SplitNameCode) rồi dựng "{tiêu đề} {sku}" — KHỚP BYTE cách runner tạo tên.</summary>
    public string ComposeFinalName(string rawTitle, string sku)
    {
        var body = SplitNameCode(rawTitle).Body;   // phòng khi AI lỡ thêm mã code ở cuối
        var s = (sku ?? "").Trim();
        return _truncate($"{body} {s}".Trim(), s, MaxNameLength);
    }

    /// <summary>1 batch tên gốc unique → tiêu đề SEO THÔ song song danh sách (retry/chia-đôi/fallback giữ tên gốc
    /// khi 1 SP thất bại). Đây là bước ĐẮT (gọi AI) nên caller đã dedup theo tên trước khi gọi. Rỗng → rỗng.</summary>
    public Task<List<string>> RewriteTitlesAsync(IReadOnlyList<string> names, Action<string>? log, CancellationToken ct)
    {
        if (names is null || names.Count == 0) return Task.FromResult(new List<string>());
        return RequestSeoTitlesWithSplitAsync(names.ToList(), log, ct);
    }

    /// <summary>Tiện ích cấp cao: map tên-gốc → tên-mới-đã-ghép-SKU-cắt-120. Dùng SKU ĐẠI DIỆN của mỗi tên
    /// (<paramref name="entries"/> đã dedup theo tên). Batch theo <paramref name="batchSize"/> (clamp 1..500) để
    /// KHÔNG dội AI. Caller cần ghép SKU PER-DÒNG thì dùng <see cref="RewriteTitlesAsync"/> + <see cref="ComposeFinalName"/>.</summary>
    public async Task<Dictionary<string, string>> RewriteAsync(
        IReadOnlyList<(string Name, string Sku)> entries, int batchSize, Action<string>? log, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (entries is null || entries.Count == 0) return result;
        var size = Math.Clamp(batchSize, 1, 500);
        var names = entries.Select(e => e.Name).ToList();
        var skuByName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, sku) in entries)
            if (!skuByName.ContainsKey(name)) skuByName[name] = sku;

        for (var i = 0; i < names.Count; i += size)
        {
            ct.ThrowIfCancellationRequested();
            var batch = names.Skip(i).Take(Math.Min(size, names.Count - i)).ToList();
            var titles = await RewriteTitlesAsync(batch, log, ct).ConfigureAwait(false);
            for (var idx = 0; idx < batch.Count; idx++)
            {
                var name = batch[idx];
                var finalName = ComposeFinalName(titles[idx], skuByName.GetValueOrDefault(name, ""));
                if (!string.IsNullOrWhiteSpace(finalName))
                    result[name] = finalName;
            }
        }
        return result;
    }

    // ── Viết lại TÊN: 1 lần gọi → tiêu đề SEO hoàn chỉnh (dùng AiConfig + prompt SEO cấu hình trên Hub) ──
    private async Task<List<string>> RequestSeoTitlesWithSplitAsync(
        List<string> names, Action<string>? log, CancellationToken ct)
    {
        try
        {
            // Retry chung ở Core (AiChat): 429/5xx chờ lâu; key/quota/model sai (permanent) ném ngay; JSON hỏng
            // gói thành InvalidOperationException để nhánh chia-đôi bên dưới bắt được như bản cũ.
            return await AiChat.ExecuteWithRetryAsync(
                c => RequestSeoTitlesOnceAsync(names, c),
                ct,
                maxAttempts: OpenAiMaxRetries,
                label: "OpenAI seo-title",
                log: log,
                mapError: ex => ex is JsonException
                    ? new InvalidOperationException($"OpenAI seo-title JSON lỗi: {ex.Message}", ex)
                    : ex,
                delay: _retryDelay);
        }
        catch (AiHttpException) { throw; }   // key/quota/model sai → dừng, không chia nhỏ
        catch (InvalidOperationException ex)
        {
            if (names.Count <= 1)
            {
                log?.Invoke($"⚠ Viết tên 1 SP thất bại — giữ tên gốc. ({Shorten(ex.Message)})");
                return [names[0]];
            }
            var mid = names.Count / 2;
            log?.Invoke($"⚠ Viết tên batch {names.Count} lỗi — chia đôi ({mid}+{names.Count - mid}). ({Shorten(ex.Message)})");
            var left = await RequestSeoTitlesWithSplitAsync(names.Take(mid).ToList(), log, ct);
            var right = await RequestSeoTitlesWithSplitAsync(names.Skip(mid).ToList(), log, ct);
            left.AddRange(right);
            return left;
        }
    }

    private async Task<List<string>> RequestSeoTitlesOnceAsync(List<string> names, CancellationToken ct)
    {
        // System = prompt SEO người dùng cấu hình (trên Hub) + đóng gói JSON cho xử lý nhiều sản phẩm.
        var system = _cfg.EffectiveNameRewritePrompt +
            "\n\n[XỬ LÝ NHIỀU SẢN PHẨM] Bạn sẽ nhận JSON danh sách {index, name}. Áp dụng đúng quy tắc trên cho TỪNG name. " +
            "CHỈ trả về DUY NHẤT JSON: {\"items\":[{\"index\":0,\"title\":\"<tiêu đề SEO, KHÔNG kèm SKU>\"}]} — đủ mỗi index input, " +
            "title chỉ 1 dòng, không kèm giải thích/ghi chú/rào ```.";
        var user = JsonSerializer.Serialize(
            new { items = names.Select((name, index) => new { index, name }).ToList() }, JsonOptions);

        var outputText = await AiJsonAsync(system, user, ct, temperature: 0.4);

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

    /// <summary>Gọi AI (đa provider qua <see cref="_complete"/>) yêu cầu trả JSON, rồi trích object JSON từ text
    /// trả về. provider/model/key lấy từ AiConfig (trên Hub).</summary>
    private async Task<string> AiJsonAsync(string system, string user, CancellationToken ct, double temperature = 0)
    {
        var text = await _complete(_cfg, system, user, temperature, 8192, ct).ConfigureAwait(false);
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

    // ====== Name structure + cleaning (ported) ======

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
}
