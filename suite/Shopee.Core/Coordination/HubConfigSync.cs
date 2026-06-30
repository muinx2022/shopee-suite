using Shopee.Core.Accounts;
using Shopee.Core.Ai;
using Shopee.Core.BigSeller;
using Shopee.Core.Infrastructure;

namespace Shopee.Core.Coordination;

/// <summary>
/// Đồng bộ cấu hình với Hub. Kéo = tải file cấu hình + cookie + AI + workbook từ Hub rồi GỘP/APPEND vào
/// store (dedup theo Id+login/email). Đẩy = upload file cấu hình. Chạy khi người dùng bấm "Đồng bộ acc"
/// HOẶC tự động 1 lần khi CLIENT vừa kết nối được Hub (xem <see cref="HttpCoordinationHub"/>). Dùng chung
/// logic gộp với <see cref="BackupService"/>.
/// </summary>
public sealed class HubConfigSync
{
    private readonly HubClient _client;
    public HubConfigSync(HubClient client) => _client = client;

    private static string SharedFile(string name) => Path.Combine(SuitePaths.ModuleDir("shared"), name);
    private static string CookieDir => Path.Combine(SuitePaths.ModuleDir("shared"), "bigseller-cookies");

    /// <summary>Đẩy cấu hình hiện tại của máy này lên Hub (làm "nguồn" cho các máy khác kéo về). CHỈ đẩy file có
    /// nội dung KHÁC bản trên Hub (so size + SHA-256) → gọi định kỳ rất rẻ (workbook lớn không upload lại vô ích).</summary>
    public async Task<string> PushAsync(CancellationToken ct = default)
    {
        Dictionary<string, FileManifestEntry> manifest;
        try { manifest = (await _client.ManifestAsync(ct)).ToDictionary(m => m.Name, m => m, StringComparer.OrdinalIgnoreCase); }
        catch { manifest = new(StringComparer.OrdinalIgnoreCase); }   // không lấy được manifest → cứ đẩy (an toàn)

        int n = 0;
        if (await PushIfChangedAsync(manifest, SharedFile("accounts.json"), "config/accounts.json", ct)) n++;
        if (await PushIfChangedAsync(manifest, SharedFile("bigseller.json"), "config/bigseller.json", ct)) n++;
        if (await PushIfChangedAsync(manifest, SharedFile("ai.json"), "config/ai.json", ct)) n++;
        if (await PushIfChangedAsync(manifest, SharedFile("scrape-targets.json"), "config/scrape-targets.json", ct)) n++;

        if (Directory.Exists(CookieDir))
            foreach (var f in Directory.GetFiles(CookieDir, "*.json"))
                if (await PushIfChangedAsync(manifest, f, "cookies/" + Path.GetFileName(f), ct)) n++;

        // Workbook Excel (dữ liệu sản phẩm) — đẩy theo từng tk BigSeller để máy khác kéo về chạy được.
        foreach (var acct in BigSellerStore.Shared.Accounts)
        {
            if (string.IsNullOrWhiteSpace(acct.WorkbookPath) || !File.Exists(acct.WorkbookPath)) continue;
            try
            {
                if (await PushIfChangedAsync(manifest, acct.WorkbookPath,
                        $"workbooks/{acct.Id}/{Path.GetFileName(acct.WorkbookPath)}", ct)) n++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        return $"Đã đẩy {n} file thay đổi (cấu hình + cookie + workbook) lên Hub.";
    }

    /// <summary>Upload 1 file nếu nội dung KHÁC bản trên Hub (size hoặc SHA-256 khác). Trả true nếu đã upload.</summary>
    private async Task<bool> PushIfChangedAsync(Dictionary<string, FileManifestEntry> manifest, string localPath, string remoteName, CancellationToken ct)
    {
        if (!File.Exists(localPath)) return false;
        var len = new FileInfo(localPath).Length;
        if (manifest.TryGetValue(remoteName, out var m) && m.Size == len
            && string.Equals(LocalSha256(localPath), m.Hash, StringComparison.OrdinalIgnoreCase))
            return false;   // không đổi → bỏ qua
        await _client.UploadAsync(remoteName, await File.ReadAllBytesAsync(localPath, ct), null, ct);
        return true;
    }

    /// <summary>Kéo tài khoản + proxy + cookie + AI từ Hub về, gộp/append vào máy này.</summary>
    public async Task<ImportResult> PullAccountsAsync(CancellationToken ct = default)
    {
        int bsA = 0, bsU = 0, bsS = 0, shA = 0, shS = 0, cookies = 0;
        var ai = false;

        // 1) Cookie trước (để RebaseBigSeller trỏ CookieFile vào file local vừa tải).
        // Bọc RIÊNG manifest: trước đây lỗi ở đây (401 token sai / mất kết nối) giết CẢ lần kéo + bị catch{}
        // ở auto-pull nuốt sạch → "kết nối được mà không sync". Giờ ném rõ để nút "Đồng bộ acc" hiện lỗi.
        List<FileManifestEntry> manifest;
        try { manifest = await _client.ManifestAsync(ct); }
        catch (OperationCanceledException) { throw; }
        catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        { throw new InvalidOperationException("Hub từ chối (401): API token client KHÔNG khớp token Hub. Vào Cài đặt → sửa token cho khớp rồi thử lại.", ex); }
        catch (Exception ex)
        { throw new InvalidOperationException("Không lấy được dữ liệu từ Hub (mất kết nối / Hub chưa bật): " + ex.Message, ex); }
        Directory.CreateDirectory(CookieDir);
        // Mỗi bước bọc try/catch RIÊNG: 1 file hỏng/tải lỗi KHÔNG làm hỏng cả lần kéo (tránh trạng thái dở dang).
        // OperationCanceledException vẫn để propagate (người dùng huỷ).
        foreach (var m in manifest.Where(m => m.Name.StartsWith("cookies/", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var bytes = await _client.DownloadAsync(m.Name, ct);
                if (bytes is null) continue;
                await File.WriteAllBytesAsync(Path.Combine(CookieDir, Path.GetFileName(m.Name)), bytes, ct);
                cookies++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }

        // 2) BigSeller (+ rebase CookieFile/WorkbookPath theo máy này).
        try
        {
            var bsBytes = await _client.DownloadAsync("config/bigseller.json", ct);
            if (bsBytes is not null)
            {
                var list = JsonSerializer.Deserialize<List<BigSellerAccount>>(NoBom(bsBytes)) ?? [];
                (bsA, bsU, bsS) = BackupService.MergeBigSeller(list, replace: false, rebaseDir: null);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }

        // 3) Shopee (+ proxy).
        try
        {
            var shBytes = await _client.DownloadAsync("config/accounts.json", ct);
            if (shBytes is not null)
            {
                var list = JsonSerializer.Deserialize<List<ShopeeAccount>>(NoBom(shBytes)) ?? [];
                (shA, shS) = BackupService.MergeShopee(list, replace: false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }

        // 4) AI (ghi đè — 1 cấu hình duy nhất).
        try
        {
            var aiBytes = await _client.DownloadAsync("config/ai.json", ct);
            if (aiBytes is not null && JsonSerializer.Deserialize<AiConfig>(NoBom(aiBytes)) is { } cfg)
            {
                AiConfigStore.Shared.Save(cfg);
                ai = true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }

        // 5) Workbook Excel — tải về hub-cache và TRỎ WorkbookPath sang bản local (để máy này scrape được).
        try
        {
            var accts = BigSellerStore.Shared.Accounts.ToList();
            var changed = false;
            foreach (var acct in accts)
            {
                var entry = manifest.FirstOrDefault(m => m.Name.StartsWith($"workbooks/{acct.Id}/", StringComparison.OrdinalIgnoreCase));
                if (entry is null) continue;
                var local = Path.Combine(SuitePaths.HubCacheDir, "workbooks", acct.Id, Path.GetFileName(entry.Name));
                Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                // Bỏ qua tải nếu bản local đã KHỚP hash manifest → auto-pull mỗi lần kết nối KHÔNG tải lại workbook lớn.
                if (!(File.Exists(local) && !string.IsNullOrEmpty(entry.Hash)
                      && string.Equals(LocalSha256(local), entry.Hash, StringComparison.OrdinalIgnoreCase)))
                {
                    var bytes = await _client.DownloadAsync(entry.Name, ct);
                    if (bytes is null) continue;
                    await File.WriteAllBytesAsync(local, bytes, ct);
                }
                if (!string.Equals(acct.WorkbookPath, local, StringComparison.OrdinalIgnoreCase))
                {
                    acct.WorkbookPath = local;
                    changed = true;
                }
            }
            if (changed) BigSellerStore.Shared.ReplaceAll(accts);
        }
        catch { }

        return new ImportResult(bsA, bsS, shA, shS, ai, cookies, bsU);
    }

    /// <summary>Bỏ BOM UTF-8 (EF BB BF) ở đầu nếu có. Các store ghi config bằng Encoding.UTF8 (CÓ BOM); deserialize
    /// THẲNG byte[] sẽ ném "'0xEF' is an invalid start of a value" → đây là lý do BigSeller/Shopee/AI = 0 khi kéo
    /// (Hub đọc bằng ReadAllText nên tự bỏ BOM → không lộ lỗi). Bỏ BOM ở client cho khớp.</summary>
    private static ReadOnlySpan<byte> NoBom(byte[] b) =>
        b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? b.AsSpan(3) : b.AsSpan();

    /// <summary>SHA-256 (hex hoa) của 1 file local — so với manifest.Hash để bỏ qua tải workbook không đổi.</summary>
    private static string LocalSha256(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs));
    }
}
