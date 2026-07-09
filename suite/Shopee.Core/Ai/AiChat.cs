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
    public static async Task<string> CompleteAsync(
        AiConfig cfg, string systemPrompt, string userPrompt, CancellationToken ct = default,
        double temperature = 0.7, int maxTokens = 4096)
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
                req.Content = JsonContent(new
                {
                    systemInstruction = new { parts = new[] { new { text = systemPrompt } } },
                    contents = new[] { new { role = "user", parts = new[] { new { text = userPrompt } } } },
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
                    messages = new[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userPrompt },
                    },
                });
                break;
        }

        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new AiHttpException((int)resp.StatusCode, $"AI {cfg.Provider} lỗi {(int)resp.StatusCode}: {Trunc(body)}");

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
            throw new AiHttpException((int)resp.StatusCode, $"AI {cfg.Provider} lỗi {(int)resp.StatusCode}: {Trunc(body)}");

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
    /// Backoff tuyến tính theo lần: 429/5xx chờ lâu (<paramref name="rateLimitDelayMs"/>×lần), còn lại
    /// <paramref name="delayMs"/>×lần. Hết lượt → ném lỗi cuối (đã qua <paramref name="mapError"/> nếu có);
    /// caller tự quyết nuốt (trả rỗng) hay để ném lên. <paramref name="delay"/> cho phép truyền hàm chờ
    /// tôn trọng Pause (DelayAsync) thay cho Task.Delay.
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
        Func<int, CancellationToken, Task>? delay = null)
    {
        delay ??= (ms, c) => Task.Delay(ms, c);
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
                if (attempt == maxAttempts)
                    break;
                var ms = IsRateLimitOrServer(ex) ? rateLimitDelayMs * attempt : delayMs * attempt;
                log?.Invoke($"⚠ {label} lỗi (lần {attempt}/{maxAttempts}): {Trunc(ex.Message)} — thử lại sau {ms / 1000}s.");
                await delay(ms, ct).ConfigureAwait(false);
            }
        }
        throw lastError ?? new InvalidOperationException($"{label} thất bại sau {maxAttempts} lần.");
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
