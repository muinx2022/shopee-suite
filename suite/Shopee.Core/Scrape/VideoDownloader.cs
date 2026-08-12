using Shopee.Core.Infrastructure;

namespace Shopee.Core.Scrape;

/// <summary>Một ứng viên video của trang SP. <paramref name="Duration"/> = null nghĩa là KHÔNG ĐO ĐƯỢC thời
/// lượng (ứng viên lấy từ nhánh fallback: performance entries / thẻ script) — KHÁC HẲN "0 giây". Coi null là 0
/// (bản cũ) khiến MỌI ứng viên fallback bị bộ lọc &lt; 60s loại sạch → cả đường fallback thành code chết.</summary>
public sealed record VideoCandidate(string Url, double? Duration, string Label = "");
public sealed record VideoDownloadResult(bool Success, string? SavedPath, string? Url, double? Duration, long? Size, string? Error);

/// <summary>
/// Tải video native bằng HttpClient — THAY cho API Python (/video/download). Lọc các ứng viên có
/// thời lượng &lt; 60s (hoặc KHÔNG đo được), đo dung lượng (HEAD hoặc Range), chọn cái lớn nhất rồi tải về
/// thư mục đích.
/// </summary>
public static class VideoDownloader
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };

    public static async Task<VideoDownloadResult> DownloadBestAsync(
        string sku, IEnumerable<VideoCandidate> candidates, string outputDir, CancellationToken ct = default)
    {
        var all = candidates.ToList();
        var valid = all
            .Where(c => !string.IsNullOrWhiteSpace(c.Url)
                        && (c.Url.StartsWith("http://") || c.Url.StartsWith("https://"))
                        // KHÔNG đo được thời lượng (null) → VẪN NHẬN: ứng viên fallback không bao giờ có
                        // duration, loại nó đi là loại luôn cả đường fallback.
                        && (c.Duration is null || (c.Duration > 0 && c.Duration < 60)))
            .ToList();
        if (valid.Count == 0)
            // Phân biệt 2 ca cho người đọc log: trang KHÔNG có ứng viên nào vs có ứng viên nhưng bị lọc hết
            // (video dài ≥ 60s / URL không hợp lệ) — trước đây cả hai đều báo "không có video ứng viên < 60s".
            return new VideoDownloadResult(false, null, null, null, null,
                all.Count == 0
                    ? "Trang không có video ứng viên nào."
                    : $"Có {all.Count} video ứng viên nhưng không cái nào dùng được (dài ≥ 60s hoặc URL không hợp lệ).");

        // Đo dung lượng từng ứng viên, chọn cái lớn nhất.
        var sized = new List<(VideoCandidate c, long size)>();
        foreach (var c in valid)
            sized.Add((c, await ProbeSizeAsync(c.Url, ct).ConfigureAwait(false) ?? 0));
        sized.Sort((a, b) => b.size.CompareTo(a.size));
        var best = sized[0].c;

        try
        {
            // Van đĩa: ổ đích sắp đầy → BỎ tải (video là rác tái tạo được; không đáng lấp nốt ổ làm hỏng profile).
            if (!DiskSpaceGuard.HasFreeSpace(outputDir, DiskSpaceGuard.VideoMinFreeBytes))
                return new VideoDownloadResult(false, null, best.Url, best.Duration, sized[0].size,
                    $"Bỏ tải video: ổ đĩa đích còn trống < {DiskSpaceGuard.ToGb(DiskSpaceGuard.VideoMinFreeBytes)}.");

            Directory.CreateDirectory(outputDir);
            var dest = Path.Combine(outputDir, SanitizeFileName(sku) + ".mp4");
            var tmp = dest + ".part";

            using (var resp = await Http.GetAsync(best.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
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
            using var resp = await Http.SendAsync(head, ct).ConfigureAwait(false);
            if (resp.Content.Headers.ContentLength is { } len) return len;
        }
        catch { }
        try
        {
            using var get = new HttpRequestMessage(HttpMethod.Get, url);
            get.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var resp = await Http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
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
