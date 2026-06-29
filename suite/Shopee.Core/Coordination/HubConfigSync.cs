using Shopee.Core.Accounts;
using Shopee.Core.Ai;
using Shopee.Core.BigSeller;
using Shopee.Core.Infrastructure;

namespace Shopee.Core.Coordination;

/// <summary>
/// Đồng bộ cấu hình THỦ CÔNG với Hub (người dùng bấm nút, không chạy nền). Kéo = tải file cấu hình
/// + cookie từ Hub rồi GỘP/APPEND vào store (dedup theo Id+login/email). Đẩy = upload file cấu hình.
/// Dùng chung logic gộp với <see cref="BackupService"/>.
/// </summary>
public sealed class HubConfigSync
{
    private readonly HubClient _client;
    public HubConfigSync(HubClient client) => _client = client;

    private static string SharedFile(string name) => Path.Combine(SuitePaths.ModuleDir("shared"), name);
    private static string CookieDir => Path.Combine(SuitePaths.ModuleDir("shared"), "bigseller-cookies");

    /// <summary>Đẩy cấu hình hiện tại của máy này lên Hub (làm "nguồn" cho các máy khác kéo về).</summary>
    public async Task<string> PushAsync(CancellationToken ct = default)
    {
        int n = 0;
        n += await PushFileAsync("accounts.json", "config/accounts.json", ct);
        n += await PushFileAsync("bigseller.json", "config/bigseller.json", ct);
        n += await PushFileAsync("ai.json", "config/ai.json", ct);
        n += await PushFileAsync("scrape-targets.json", "config/scrape-targets.json", ct);

        if (Directory.Exists(CookieDir))
            foreach (var f in Directory.GetFiles(CookieDir, "*.json"))
            {
                await _client.UploadAsync("cookies/" + Path.GetFileName(f), await File.ReadAllBytesAsync(f, ct), null, ct);
                n++;
            }

        // Workbook Excel (dữ liệu sản phẩm) — upload theo từng tk BigSeller để máy khác kéo về scrape được.
        foreach (var acct in BigSellerStore.Shared.Accounts)
        {
            if (string.IsNullOrWhiteSpace(acct.WorkbookPath) || !File.Exists(acct.WorkbookPath)) continue;
            try
            {
                await _client.UploadAsync($"workbooks/{acct.Id}/{Path.GetFileName(acct.WorkbookPath)}",
                    await File.ReadAllBytesAsync(acct.WorkbookPath, ct), null, ct);
                n++;
            }
            catch { }
        }
        return $"Đã đẩy {n} file (cấu hình + cookie + workbook) lên Hub.";
    }

    private async Task<int> PushFileAsync(string local, string remote, CancellationToken ct)
    {
        var path = SharedFile(local);
        if (!File.Exists(path)) return 0;
        await _client.UploadAsync(remote, await File.ReadAllBytesAsync(path, ct), null, ct);
        return 1;
    }

    /// <summary>Kéo tài khoản + proxy + cookie + AI từ Hub về, gộp/append vào máy này.</summary>
    public async Task<ImportResult> PullAccountsAsync(CancellationToken ct = default)
    {
        int bsA = 0, bsS = 0, shA = 0, shS = 0, cookies = 0;
        var ai = false;

        // 1) Cookie trước (để RebaseBigSeller trỏ CookieFile vào file local vừa tải).
        var manifest = await _client.ManifestAsync(ct);
        Directory.CreateDirectory(CookieDir);
        foreach (var m in manifest.Where(m => m.Name.StartsWith("cookies/", StringComparison.OrdinalIgnoreCase)))
        {
            var bytes = await _client.DownloadAsync(m.Name, ct);
            if (bytes is null) continue;
            await File.WriteAllBytesAsync(Path.Combine(CookieDir, Path.GetFileName(m.Name)), bytes, ct);
            cookies++;
        }

        // 2) BigSeller (+ rebase CookieFile/WorkbookPath theo máy này).
        var bsBytes = await _client.DownloadAsync("config/bigseller.json", ct);
        if (bsBytes is not null)
        {
            var list = JsonSerializer.Deserialize<List<BigSellerAccount>>(bsBytes) ?? [];
            (bsA, bsS) = BackupService.MergeBigSeller(list, replace: false, rebaseDir: null);
        }

        // 3) Shopee (+ proxy).
        var shBytes = await _client.DownloadAsync("config/accounts.json", ct);
        if (shBytes is not null)
        {
            var list = JsonSerializer.Deserialize<List<ShopeeAccount>>(shBytes) ?? [];
            (shA, shS) = BackupService.MergeShopee(list, replace: false);
        }

        // 4) AI (ghi đè — 1 cấu hình duy nhất).
        var aiBytes = await _client.DownloadAsync("config/ai.json", ct);
        if (aiBytes is not null && JsonSerializer.Deserialize<AiConfig>(aiBytes) is { } cfg)
        {
            AiConfigStore.Shared.Save(cfg);
            ai = true;
        }

        // 5) Workbook Excel — tải về hub-cache và TRỎ WorkbookPath sang bản local (để máy này scrape được).
        try
        {
            var accts = BigSellerStore.Shared.Accounts.ToList();
            var changed = false;
            foreach (var acct in accts)
            {
                var entry = manifest.FirstOrDefault(m => m.Name.StartsWith($"workbooks/{acct.Id}/", StringComparison.OrdinalIgnoreCase));
                if (entry is null) continue;
                var bytes = await _client.DownloadAsync(entry.Name, ct);
                if (bytes is null) continue;
                var local = Path.Combine(SuitePaths.HubCacheDir, "workbooks", acct.Id, Path.GetFileName(entry.Name));
                Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                await File.WriteAllBytesAsync(local, bytes, ct);
                if (!string.Equals(acct.WorkbookPath, local, StringComparison.OrdinalIgnoreCase))
                {
                    acct.WorkbookPath = local;
                    changed = true;
                }
            }
            if (changed) BigSellerStore.Shared.ReplaceAll(accts);
        }
        catch { }

        return new ImportResult(bsA, bsS, shA, shS, ai, cookies);
    }
}
