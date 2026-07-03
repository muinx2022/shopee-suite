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
            throw new InvalidOperationException($"Chưa cấu hình API key cho {cfg.Provider} (mục Cài đặt).");

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
            throw new InvalidOperationException($"Chưa cấu hình API key cho {cfg.Provider} (mục Cài đặt).");

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

    private static StringContent JsonContent(object o) =>
        new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");

    private static string Trunc(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
