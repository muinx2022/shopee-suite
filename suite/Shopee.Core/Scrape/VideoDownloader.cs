namespace Shopee.Core.Scrape;

public sealed record VideoCandidate(string Url, double Duration, string Label = "");
public sealed record VideoDownloadResult(bool Success, string? SavedPath, string? Url, double Duration, long? Size, string? Error);

/// <summary>
/// Tải video native bằng HttpClient — THAY cho API Python (/video/download). Lọc các ứng viên có
/// thời lượng &lt; 60s, đo dung lượng (HEAD hoặc Range), chọn cái lớn nhất rồi tải về thư mục đích.
/// </summary>
public static class VideoDownloader
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };

    public static async Task<VideoDownloadResult> DownloadBestAsync(
        string sku, IEnumerable<VideoCandidate> candidates, string outputDir, CancellationToken ct = default)
    {
        var valid = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.Url)
                        && (c.Url.StartsWith("http://") || c.Url.StartsWith("https://"))
                        && c.Duration > 0 && c.Duration < 60)
            .ToList();
        if (valid.Count == 0)
            return new VideoDownloadResult(false, null, null, 0, null, "Không có video ứng viên < 60s.");

        // Đo dung lượng từng ứng viên, chọn cái lớn nhất.
        var sized = new List<(VideoCandidate c, long size)>();
        foreach (var c in valid)
            sized.Add((c, await ProbeSizeAsync(c.Url, ct) ?? 0));
        sized.Sort((a, b) => b.size.CompareTo(a.size));
        var best = sized[0].c;

        try
        {
            Directory.CreateDirectory(outputDir);
            var dest = Path.Combine(outputDir, SanitizeFileName(sku) + ".mp4");
            var tmp = dest + ".part";

            using (var resp = await Http.GetAsync(best.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
                await src.CopyToAsync(dst, ct);
            }
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(tmp, dest);

            var size = new FileInfo(dest).Length;
            return new VideoDownloadResult(true, dest, best.Url, best.Duration, size, null);
        }
        catch (Exception ex)
        {
            return new VideoDownloadResult(false, null, best.Url, best.Duration, sized[0].size, ex.Message);
        }
    }

    private static async Task<long?> ProbeSizeAsync(string url, CancellationToken ct)
    {
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await Http.SendAsync(head, ct);
            if (resp.Content.Headers.ContentLength is { } len) return len;
        }
        catch { }
        try
        {
            using var get = new HttpRequestMessage(HttpMethod.Get, url);
            get.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var resp = await Http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.Content.Headers.ContentRange?.Length is { } total) return total;
            if (resp.Content.Headers.ContentLength is { } len) return len;
        }
        catch { }
        return null;
    }

    private static string SanitizeFileName(string name)
    {
        var cleaned = string.Join("_", (name ?? "video").Split(Path.GetInvalidFileNameChars())).Trim().Trim('.');
        return string.IsNullOrEmpty(cleaned) ? "video" : cleaned;
    }
}
