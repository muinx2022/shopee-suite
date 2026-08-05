using System.Text;
using System.Text.Json;
using Shopee.Core.Ai;

namespace ShopeeStatApp.Services;

/// <summary>
/// Phân loại sản phẩm vào danh mục lá Shopee bằng AI. Gửi theo lô tên sản phẩm + danh sách danh mục (đánh số),
/// nhận về index danh mục cho từng sản phẩm.
/// <para>Phần gọi HTTP (endpoint/header/schema từng nhà cung cấp + retry) KHÔNG còn ở đây — uỷ hết cho
/// <see cref="AiChat"/> (client 3 provider dùng chung của suite). File này chỉ còn dựng prompt, gom lô và
/// đọc kết quả JSON.</para>
/// </summary>
public sealed class CategoryAiUpdater
{
    private readonly AiConfig _cfg;

    /// <summary>Số lần thử một lô (gồm lần đầu) — giữ mức của bản cũ (1 + 8 lần thử lại khi 429).</summary>
    private const int MaxAttemptsPerBatch = 9;

    public CategoryAiUpdater(AiConfig cfg) => _cfg = cfg;

    /// <summary>Phân loại 1 lô tên sản phẩm. Trả về mảng cùng độ dài <paramref name="names"/>:
    /// index danh mục trong <paramref name="categoryPaths"/>, hoặc -1 nếu không xác định.</summary>
    public async Task<int[]> ClassifyAsync(IReadOnlyList<string> names, IReadOnlyList<string> categoryPaths, CancellationToken ct)
    {
        // Thiếu key là lỗi CẤU HÌNH — chặn ngay, đừng để vòng retry thử lại 9 lần rồi mới báo.
        if (!_cfg.HasActiveKey)
            throw new InvalidOperationException($"Chưa cấu hình API key cho {_cfg.Provider} (trang Cấu hình AI trên Hub).");

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

        // maxAttemptsTransient: 429 đáng thử lại 9 lần (rate limit rồi sẽ hết) nhưng lỗi mạng/timeout mà cũng
        // ôm 9 × 120s là màn danh mục treo ~20 phút — bản cũ ném NGAY lỗi mạng, nên chặn trần 3 cho lớp đó.
        var text = await AiChat.ExecuteWithRetryAsync(
            c => AiChat.CompleteAsync(_cfg, sys, user, c, temperature: 0, maxTokens: 8192, jsonMode: true),
            ct, maxAttempts: MaxAttemptsPerBatch, maxAttemptsTransient: AiChat.DefaultMaxAttempts,
            label: "Phân loại danh mục AI").ConfigureAwait(false);
        var content = ExtractJsonObject(text);

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

    // Claude/Gemini đôi khi bọc JSON trong ```json ... ``` hoặc thêm chữ — lấy đúng object {...} ngoài cùng.
    private static string ExtractJsonObject(string s)
    {
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        return start >= 0 && end > start ? s.Substring(start, end - start + 1) : s;
    }
}
