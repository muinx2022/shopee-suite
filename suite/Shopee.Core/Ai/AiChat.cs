using System.Net.Http;
using System.Net.Http.Headers;

namespace Shopee.Core.Ai;

/// <summary>
/// Client chat AI thống nhất cho 3 nhà cung cấp (OpenAI/Anthropic/Gemini). Mọi tính năng AI của
/// suite (viết lại tên/mô tả, phân loại danh mục) gọi qua đây để dùng đúng provider/model/key đã
/// cấu hình chung. Trả về text.
/// </summary>
public static class AiChat
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    /// <summary>Gọi 1 lượt chat: trả về nội dung text. Ném exception nếu lỗi (kèm body lỗi).</summary>
    /// <param name="jsonMode">Bật CHẾ ĐỘ JSON của nhà cung cấp — model bị RÀNG BUỘC chỉ trả JSON hợp lệ
    /// (OpenAI <c>response_format: json_object</c>, Gemini <c>responseMimeType: application/json</c>).
    /// Anthropic KHÔNG có công tắc tương đương nên cờ này không tác dụng ở nhánh Anthropic — prompt phải tự
    /// dặn "chỉ trả JSON" và người gọi nên bóc <c>{…}</c> ra khỏi text (vd rào ```json).</param>
    public static async Task<string> CompleteAsync(
        AiConfig cfg, string systemPrompt, string userPrompt, CancellationToken ct = default,
        double temperature = 0.7, int maxTokens = 4096, bool jsonMode = false)
    {
        if (!cfg.HasActiveKey)
            throw new InvalidOperationException($"Chưa cấu hình API key cho {cfg.Provider} (trang Cấu hình AI trên Hub).");

        var model = cfg.ActiveModel;
        var key = cfg.ActiveApiKey;
        HttpRequestMessage req;

        switch (cfg.ProviderKind)
        {
            case AiProviderKind.Anthropic:
                req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
                req.Headers.Add("x-api-key", key);
                req.Headers.Add("anthropic-version", "2023-06-01");
                req.Content = JsonContent(new
                {
                    model,
                    max_tokens = maxTokens,
                    temperature,
                    system = systemPrompt,
                    messages = new[] { new { role = "user", content = userPrompt } },
                });
                break;

            case AiProviderKind.Gemini:
                // Gửi key qua header x-goog-api-key thay vì query string → key không lọt vào URL trong
                // log/exception (HttpRequestException hay kèm URL).
                var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";
                req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Add("x-goog-api-key", key);
                // Dictionary (không phải anonymous type) để BỎ HẲN khóa responseMimeType khi không bật jsonMode —
                // gửi null là API từ chối.
                var genCfg = new Dictionary<string, object?> { ["temperature"] = temperature };
                if (jsonMode) genCfg["responseMimeType"] = "application/json";
                req.Content = JsonContent(new
                {
                    systemInstruction = new { parts = new[] { new { text = systemPrompt } } },
                    contents = new[] { new { role = "user", parts = new[] { new { text = userPrompt } } } },
                    generationConfig = genCfg,
                });
                break;

            default: // OpenAI
                req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                var openAiBody = new Dictionary<string, object?>
                {
                    ["model"] = model,
                    ["temperature"] = temperature,
                    ["messages"] = new[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userPrompt },
                    },
                };
                if (jsonMode) openAiBody["response_format"] = new { type = "json_object" };
                req.Content = JsonContent(openAiBody);
                break;
        }

        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new AiHttpException((int)resp.StatusCode, $"AI {cfg.Provider} lỗi {(int)resp.StatusCode}: {Trunc(body)}",
                ReadRetryAfterMs(resp));

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        return cfg.ProviderKind switch
        {
            AiProviderKind.Anthropic => root.GetProperty("content")[0].GetProperty("text").GetString() ?? "",
            AiProviderKind.Gemini => root.GetProperty("candidates")[0].GetProperty("content")
                .GetProperty("parts")[0].GetProperty("text").GetString() ?? "",
            _ => root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "",
        };
    }

    /// <summary>Gọi AI có KÈM 1 ẢNH (đọc captcha/ảnh) — trả text. Dùng đúng provider/model/key đã cấu hình.
    /// gpt-4.1-mini / claude-haiku / gemini-flash đều hỗ trợ vision. temperature 0 + maxTokens nhỏ cho OCR.</summary>
    public static async Task<string> CompleteVisionAsync(
        AiConfig cfg, string systemPrompt, string userText, byte[] imagePng, CancellationToken ct = default,
        double temperature = 0, int maxTokens = 16)
    {
        if (!cfg.HasActiveKey)
            throw new InvalidOperationException($"Chưa cấu hình API key cho {cfg.Provider} (trang Cấu hình AI trên Hub).");

        var model = cfg.ActiveModel;
        var key = cfg.ActiveApiKey;
        var b64 = Convert.ToBase64String(imagePng);
        HttpRequestMessage req;

        switch (cfg.ProviderKind)
        {
            case AiProviderKind.Anthropic:
                req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
                req.Headers.Add("x-api-key", key);
                req.Headers.Add("anthropic-version", "2023-06-01");
                req.Content = JsonContent(new
                {
                    model,
                    max_tokens = maxTokens,
                    temperature,
                    system = systemPrompt,
                    messages = new[] { new { role = "user", content = new object[]
                    {
                        new { type = "text", text = userText },
                        new { type = "image", source = new { type = "base64", media_type = "image/png", data = b64 } },
                    } } },
                });
                break;

            case AiProviderKind.Gemini:
                var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";
                req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Add("x-goog-api-key", key);
                req.Content = JsonContent(new
                {
                    systemInstruction = new { parts = new[] { new { text = systemPrompt } } },
                    contents = new[] { new { role = "user", parts = new object[]
                    {
                        new { text = userText },
                        new { inline_data = new { mime_type = "image/png", data = b64 } },
                    } } },
                    generationConfig = new { temperature },
                });
                break;

            default: // OpenAI
                req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                req.Content = JsonContent(new
                {
                    model,
                    temperature,
                    max_tokens = maxTokens,
                    messages = new object[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = new object[]
                        {
                            new { type = "text", text = userText },
                            new { type = "image_url", image_url = new { url = "data:image/png;base64," + b64 } },
                        } },
                    },
                });
                break;
        }

        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new AiHttpException((int)resp.StatusCode, $"AI {cfg.Provider} lỗi {(int)resp.StatusCode}: {Trunc(body)}",
                ReadRetryAfterMs(resp));

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        return cfg.ProviderKind switch
        {
            AiProviderKind.Anthropic => root.GetProperty("content")[0].GetProperty("text").GetString() ?? "",
            AiProviderKind.Gemini => root.GetProperty("candidates")[0].GetProperty("content")
                .GetProperty("parts")[0].GetProperty("text").GetString() ?? "",
            _ => root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "",
        };
    }

    /// <summary>Số lần thử mặc định (gồm lần đầu) cho một lệnh gọi AI.</summary>
    public const int DefaultMaxAttempts = 3;

    /// <summary>
    /// Chạy một hành động gọi AI với RETRY THỐNG NHẤT cho toàn suite (thay các vòng retry tự chế trước đây ở
    /// ProductUpdate.GenerateDescription + NameRewrite.ExecuteWithRetry). Phân loại lỗi:
    ///  • <see cref="AiHttpException.IsPermanent"/> (400/401/403/404 = key/quota/model sai) → NÉM NGAY, không retry.
    ///  • Người dùng hủy (token <paramref name="ct"/> đã cancel) → ném ngay.
    ///  • MỌI lỗi còn lại (mạng, timeout HttpClient, JSON hỏng, nội dung không hợp lệ do action tự ném…) → TẠM, thử lại.
    /// Backoff tuyến tính theo lần: 429/5xx chờ lâu (<paramref name="rateLimitDelayMs"/>×lần — nhưng nếu server
    /// gửi <c>Retry-After</c> thì CHỜ ĐÚNG số đó), còn lại <paramref name="delayMs"/>×lần. Hết lượt → ném lỗi
    /// cuối (đã qua <paramref name="mapError"/> nếu có); caller tự quyết nuốt (trả rỗng) hay để ném lên.
    /// <paramref name="delay"/> cho phép truyền hàm chờ tôn trọng Pause (DelayAsync) thay cho Task.Delay.
    /// <paramref name="maxAttemptsTransient"/> chặn TRẦN RIÊNG cho lỗi tạm KHÔNG-phải-429/5xx (mạng, timeout,
    /// JSON hỏng): 0 = dùng chung <paramref name="maxAttempts"/>. Dùng khi caller muốn 429 được thử lại nhiều
    /// (rate limit rồi sẽ hết) nhưng mạng đứt thì bỏ cuộc sớm thay vì ôm timeout × N lần.
    /// </summary>
    public static async Task<T> ExecuteWithRetryAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken ct = default,
        int maxAttempts = DefaultMaxAttempts,
        int delayMs = 2000,
        int rateLimitDelayMs = 15000,
        string label = "AI",
        Action<string>? log = null,
        Func<Exception, Exception>? mapError = null,
        Func<int, CancellationToken, Task>? delay = null,
        int maxAttemptsTransient = 0)
    {
        delay ??= (ms, c) => Task.Delay(ms, c);
        var transientCap = maxAttemptsTransient > 0 ? Math.Min(maxAttemptsTransient, maxAttempts) : maxAttempts;
        var transientCount = 0;
        Exception? lastError = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await action(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsStopError(ex, ct))
            {
                throw;   // key/quota/model sai hoặc người dùng hủy → dừng, không retry
            }
            catch (Exception ex)
            {
                lastError = mapError?.Invoke(ex) ?? ex;
                var isRate = IsRateLimitOrServer(ex);
                if (!isRate) transientCount++;
                if (attempt == maxAttempts || (!isRate && transientCount >= transientCap))
                    break;
                var ms = isRate && ex is AiHttpException { RetryAfterMs: > 0 } ah
                    ? Math.Max(ah.RetryAfterMs!.Value, 1000)
                    : isRate ? rateLimitDelayMs * attempt : delayMs * attempt;
                // Mẫu số của log = trần THỰC của lớp lỗi hiện tại — kẻo cap transient 3/maxAttempts 9 mà
                // log "lần 3/9" rồi bỏ cuộc trông như bug.
                var cap = isRate ? maxAttempts : transientCap;
                log?.Invoke($"⚠ {label} lỗi (lần {attempt}/{cap}): {Trunc(ex.Message)} — thử lại sau {ms / 1000}s.");
                await delay(ms, ct).ConfigureAwait(false);
            }
        }
        throw lastError ?? new InvalidOperationException($"{label} thất bại sau {maxAttempts} lần.");
    }

    /// <summary>Đọc header <c>Retry-After</c> (giây hoặc HTTP-date) về ms; null nếu không có/không hiểu.</summary>
    private static int? ReadRetryAfterMs(HttpResponseMessage resp)
    {
        var ra = resp.Headers.RetryAfter;
        if (ra is null) return null;
        if (ra.Delta is { } d && d > TimeSpan.Zero) return (int)Math.Min(d.TotalMilliseconds, 120_000);
        if (ra.Date is { } when)
        {
            var wait = when - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) return (int)Math.Min(wait.TotalMilliseconds, 120_000);
        }
        return null;
    }

    /// <summary>Lỗi phải DỪNG ngay (không retry): lỗi cấu hình/quyền AI (permanent) hoặc người dùng đã hủy.</summary>
    private static bool IsStopError(Exception ex, CancellationToken ct) =>
        (ex is AiHttpException ah && ah.IsPermanent) ||
        (ex is OperationCanceledException && ct.IsCancellationRequested);

    /// <summary>429 (rate limit) hoặc 5xx (server) → nên chờ LÂU hơn trước khi thử lại.</summary>
    private static bool IsRateLimitOrServer(Exception ex) =>
        ex is AiHttpException { StatusCode: 429 } or AiHttpException { StatusCode: >= 500 };

    private static StringContent JsonContent(object o) =>
        new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");

    private static string Trunc(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
