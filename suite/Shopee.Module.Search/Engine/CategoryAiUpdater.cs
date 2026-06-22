using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShopeeStatApp.Services;

/// <summary>
/// Phân loại sản phẩm vào danh mục lá Shopee bằng AI (OpenAI / Claude / Gemini). Gửi theo lô tên sản phẩm
/// + danh sách danh mục (đánh số), nhận về index danh mục cho từng sản phẩm. Logic prompt/gom lô như nhau,
/// chỉ phần gọi HTTP (endpoint, header, schema request/response) là khác theo từng nhà cung cấp.
/// </summary>
public sealed class CategoryAiUpdater
{
    public enum Provider { OpenAI, Claude, Gemini }

    private readonly Provider _provider;
    private readonly string _apiKey;
    private readonly string _model;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(180) };

    public CategoryAiUpdater(Provider provider, string apiKey, string model)
    {
        _provider = provider;
        _apiKey = apiKey;
        _model = model;
    }

    /// <summary>Tên hiển thị nhà cung cấp đang dùng (cho thông báo lỗi).</summary>
    public string ProviderName => _provider.ToString();

    /// <summary>Chuyển chuỗi cấu hình ("OpenAI"/"Claude"/"Gemini") sang enum; mặc định OpenAI.</summary>
    public static Provider ParseProvider(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "claude" or "anthropic" => Provider.Claude,
        "gemini" or "google" => Provider.Gemini,
        _ => Provider.OpenAI,
    };

    /// <summary>Phân loại 1 lô tên sản phẩm. Trả về mảng cùng độ dài <paramref name="names"/>:
    /// index danh mục trong <paramref name="categoryPaths"/>, hoặc -1 nếu không xác định.</summary>
    public async Task<int[]> ClassifyAsync(IReadOnlyList<string> names, IReadOnlyList<string> categoryPaths, CancellationToken ct)
    {
        var catSb = new StringBuilder();
        for (var i = 0; i < categoryPaths.Count; i++)
            catSb.Append(i).Append(": ").Append(categoryPaths[i]).Append('\n');

        var prodSb = new StringBuilder();
        for (var i = 0; i < names.Count; i++)
            prodSb.Append(i).Append(": ").Append((names[i] ?? "").Replace('\n', ' ').Replace('\r', ' ')).Append('\n');

        var sys =
            "Bạn là trợ lý phân loại sản phẩm trên sàn TMĐT Shopee. " +
            "Bạn nhận danh sách DANH MỤC (mỗi dòng dạng 'index: đường dẫn danh mục') và danh sách SẢN PHẨM (mỗi dòng 'index: tên'). " +
            "Với MỖI sản phẩm, hãy chọn ĐÚNG MỘT danh mục phù hợp nhất dựa trên TÊN sản phẩm, chỉ dùng index có trong danh sách danh mục. " +
            "Nếu không chắc, chọn danh mục gần đúng nhất. " +
            "Chỉ trả về JSON object (không giải thích, không markdown): {\"r\":[{\"i\":<index sản phẩm>,\"c\":<index danh mục>}, ...]} cho TẤT CẢ sản phẩm.";
        var user = "DANH MỤC:\n" + catSb + "\nSẢN PHẨM:\n" + prodSb;

        string body;
        for (var attempt = 0; ; attempt++)
        {
            using var req = BuildRequest(sys, user);
            using var resp = await Http.SendAsync(req, ct);
            body = await resp.Content.ReadAsStringAsync(ct);

            // 429 = vượt rate limit. Chờ đúng thời gian nhà cung cấp gợi ý rồi thử lại.
            if ((int)resp.StatusCode == 429 && attempt < 8)
            {
                await Task.Delay(RetryDelay(resp, body, attempt), ct);
                continue;
            }
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"{_provider} lỗi {(int)resp.StatusCode}: {Trunc(body, 400)}");
            break;
        }

        var content = ExtractContent(body);

        var result = new int[names.Count];
        Array.Fill(result, -1);
        try
        {
            using var rd = JsonDocument.Parse(content);
            if (rd.RootElement.TryGetProperty("r", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    var pi = e.TryGetProperty("i", out var iv) && iv.TryGetInt32(out var i2) ? i2 : -1;
                    var ci = e.TryGetProperty("c", out var cv) && cv.TryGetInt32(out var c2) ? c2 : -1;
                    if (pi >= 0 && pi < result.Length && ci >= 0 && ci < categoryPaths.Count)
                        result[pi] = ci;
                }
            }
        }
        catch { /* nội dung không phải JSON hợp lệ → giữ -1 */ }
        return result;
    }

    /// <summary>Phân loại TẤT CẢ tên sản phẩm theo lô + chạy song song (cho file lớn). Trả về mảng
    /// đường dẫn danh mục (chuỗi) cùng độ dài names; "" nếu không xác định. <paramref name="onProgress"/>
    /// nhận số dòng đã xong (gọi từ thread nền — caller tự marshal về UI nếu cần).</summary>
    public async Task<string[]> ClassifyAllAsync(
        IReadOnlyList<string> names, IReadOnlyList<string> categoryPaths,
        int batchSize, int maxParallel, Action<int>? onProgress, CancellationToken ct)
    {
        var result = new string[names.Count];
        Array.Fill(result, "");
        var batches = new List<(int Start, int Len)>();
        for (var s = 0; s < names.Count; s += batchSize)
            batches.Add((s, Math.Min(batchSize, names.Count - s)));

        using var sem = new SemaphoreSlim(Math.Max(1, maxParallel));
        var done = 0;
        var tasks = batches.Select(async b =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var slice = new List<string>(b.Len);
                for (var k = 0; k < b.Len; k++) slice.Add(names[b.Start + k]);
                var idx = await ClassifyAsync(slice, categoryPaths, ct);
                for (var k = 0; k < b.Len; k++)
                {
                    var ci = idx[k];
                    if (ci >= 0 && ci < categoryPaths.Count) result[b.Start + k] = categoryPaths[ci];
                }
            }
            finally
            {
                sem.Release();
                onProgress?.Invoke(Interlocked.Add(ref done, b.Len));
            }
        }).ToList();

        await Task.WhenAll(tasks);
        return result;
    }

    // Tạo request HTTP đúng chuẩn từng nhà cung cấp (endpoint + header + body).
    private HttpRequestMessage BuildRequest(string sys, string user)
    {
        switch (_provider)
        {
            case Provider.Claude:
            {
                var payload = new
                {
                    model = _model,
                    max_tokens = 8192,
                    temperature = 0,
                    system = sys,
                    messages = new object[] { new { role = "user", content = user } },
                };
                var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
                req.Headers.Add("x-api-key", _apiKey);
                req.Headers.Add("anthropic-version", "2023-06-01");
                req.Content = JsonBody(payload);
                return req;
            }
            case Provider.Gemini:
            {
                var payload = new
                {
                    systemInstruction = new { parts = new[] { new { text = sys } } },
                    contents = new object[] { new { role = "user", parts = new[] { new { text = user } } } },
                    generationConfig = new { temperature = 0, responseMimeType = "application/json" },
                };
                var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent?key={Uri.EscapeDataString(_apiKey)}";
                var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Content = JsonBody(payload);
                return req;
            }
            default: // OpenAI
            {
                var payload = new
                {
                    model = _model,
                    temperature = 0,
                    response_format = new { type = "json_object" },
                    messages = new object[]
                    {
                        new { role = "system", content = sys },
                        new { role = "user", content = user },
                    },
                };
                var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                req.Content = JsonBody(payload);
                return req;
            }
        }
    }

    // Lấy phần text model trả về theo schema từng nhà cung cấp, rồi tách JSON object {...}.
    private string ExtractContent(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var text = _provider switch
        {
            Provider.Claude => root.GetProperty("content")[0].GetProperty("text").GetString() ?? "{}",
            Provider.Gemini => root.GetProperty("candidates")[0].GetProperty("content")
                .GetProperty("parts")[0].GetProperty("text").GetString() ?? "{}",
            _ => root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "{}",
        };
        return ExtractJsonObject(text);
    }

    private static StringContent JsonBody(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    // Claude/Gemini đôi khi bọc JSON trong ```json ... ``` hoặc thêm chữ — lấy đúng object {...} ngoài cùng.
    private static string ExtractJsonObject(string s)
    {
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        return start >= 0 && end > start ? s.Substring(start, end - start + 1) : s;
    }

    // Thời gian chờ trước khi thử lại 429: ưu tiên header Retry-After, rồi "try again in Xs" trong body,
    // cuối cùng là backoff lũy thừa (tối đa 30s).
    private static TimeSpan RetryDelay(HttpResponseMessage resp, string body, int attempt)
    {
        if (resp.Headers.RetryAfter?.Delta is { } d && d > TimeSpan.Zero)
            return d + TimeSpan.FromMilliseconds(300);
        var m = Regex.Match(body, @"try again in ([\d.]+)\s*s", RegexOptions.IgnoreCase);
        if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var secs))
            return TimeSpan.FromSeconds(secs + 0.6);
        return TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
