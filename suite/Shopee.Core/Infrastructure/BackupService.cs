using System.IO.Compression;
using System.Text.Json;
using Shopee.Core.Accounts;
using Shopee.Core.Ai;
using Shopee.Core.BigSeller;

namespace Shopee.Core.Infrastructure;

/// <summary>Chọn mục để sao lưu/khôi phục.</summary>
public sealed record BackupOptions(bool BigSeller, bool ShopeeAccounts, bool AiConfig);

/// <summary>Kết quả khôi phục để báo người dùng.</summary>
public sealed record ImportResult(
    int BigSellerAdded, int BigSellerSkipped, int ShopeeAdded, int ShopeeSkipped, bool AiImported, int CookiesCopied,
    int BigSellerUpdated = 0);

/// <summary>
/// Sao lưu / khôi phục dữ liệu suite ra/từ 1 file .zip để đồng bộ sang máy khác.
/// Gói: tài khoản BigSeller (+ cookie BigSeller), tài khoản Shopee + proxy, cấu hình AI (keys).
/// Khôi phục có 2 chế độ: GỘP (thêm mới, giữ cũ) hoặc THAY THẾ (ghi đè). Tự re-base đường dẫn
/// WorkbookPath + CookieFile (vốn tuyệt đối theo máy cũ) sang máy hiện tại.
/// </summary>
public static class BackupService
{
    private static string SharedDir => SuitePaths.ModuleDir("shared");
    private static string CookieDir => Path.Combine(SharedDir, "bigseller-cookies");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static void Export(string zipPath, BackupOptions opt)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        if (opt.BigSeller)
        {
            AddFile(zip, Path.Combine(SharedDir, "bigseller.json"), "bigseller.json");
            if (Directory.Exists(CookieDir))
                foreach (var f in Directory.GetFiles(CookieDir, "*.json"))
                    AddFile(zip, f, "bigseller-cookies/" + Path.GetFileName(f));
        }
        if (opt.ShopeeAccounts)
            AddFile(zip, Path.Combine(SharedDir, "accounts.json"), "accounts.json");
        if (opt.AiConfig)
            AddFile(zip, Path.Combine(SharedDir, "ai.json"), "ai.json");

        var manifest = zip.CreateEntry("manifest.json");
        using var w = new StreamWriter(manifest.Open());
        w.Write(JsonSerializer.Serialize(new { app = "ShopeeSuite", opt }, JsonOpts));
    }

    public static ImportResult Import(string zipPath, BackupOptions opt, bool replace, string? rebaseWorkbookDir)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        int bsAdded = 0, bsUpdated = 0, bsSkipped = 0, shAdded = 0, shSkipped = 0, cookies = 0;
        var aiImported = false;

        // 1) Giải nén cookie BigSeller trước (để re-base CookieFile khi nạp account).
        if (opt.BigSeller)
        {
            Directory.CreateDirectory(CookieDir);
            foreach (var e in zip.Entries.Where(e =>
                e.FullName.StartsWith("bigseller-cookies/", StringComparison.OrdinalIgnoreCase) && e.Name.Length > 0))
            {
                var dest = Path.Combine(CookieDir, e.Name);
                if (replace || !File.Exists(dest)) { e.ExtractToFile(dest, overwrite: true); cookies++; }
            }
        }

        // 2) Tài khoản BigSeller (gộp theo Email / thay thế).
        if (opt.BigSeller && zip.GetEntry("bigseller.json") is { } bsEntry)
        {
            var imported = Deserialize<List<BigSellerAccount>>(bsEntry) ?? [];
            (bsAdded, bsUpdated, bsSkipped) = MergeBigSeller(imported, replace, rebaseWorkbookDir);
        }

        // 3) Tài khoản Shopee (gộp theo login / thay thế).
        if (opt.ShopeeAccounts && zip.GetEntry("accounts.json") is { } shEntry)
        {
            var imported = Deserialize<List<ShopeeAccount>>(shEntry) ?? [];
            (shAdded, shSkipped) = MergeShopee(imported, replace);
        }

        // 4) Cấu hình AI (luôn ghi đè — là 1 cấu hình duy nhất).
        if (opt.AiConfig && zip.GetEntry("ai.json") is { } aiEntry && Deserialize<AiConfig>(aiEntry) is { } cfg)
        {
            AiConfigStore.Shared.Save(cfg);
            aiImported = true;
        }

        return new ImportResult(bsAdded, bsSkipped, shAdded, shSkipped, aiImported, cookies, bsUpdated);
    }

    /// <summary>Gộp danh sách BigSeller vào store (dùng chung cho import-zip + đồng bộ Hub). append = replace:false.
    /// Acc ĐÃ CÓ (theo Id/email) mà nội dung DÙNG CHUNG khác (shop/label/email/proxy) → CẬP NHẬT xuống (Hub là
    /// nguồn sự thật: sửa shop ở Hub lan tới mọi client), GIỮ NGUYÊN field local: cookie, workbook, lựa chọn UI.</summary>
    public static (int added, int updated, int skipped) MergeBigSeller(List<BigSellerAccount> imported, bool replace, string? rebaseDir)
    {
        var current = replace ? new List<BigSellerAccount>() : BigSellerStore.Shared.Accounts.ToList();
        int added = 0, updated = 0, skipped = 0;
        foreach (var a in imported)
        {
            var existing = replace ? null
                : current.FirstOrDefault(x => x.Id == a.Id)
                  ?? (EmailKey(a).Length > 0 ? current.FirstOrDefault(x => EmailKey(x) == EmailKey(a)) : null);
            if (existing is not null)
            {
                if (SharedSignature(existing) != SharedSignature(a))
                {
                    existing.Label = a.Label; existing.Email = a.Email; existing.Shops = a.Shops;
                    existing.KiotProxyKey = a.KiotProxyKey; existing.Region = a.Region; existing.ProxyType = a.ProxyType;
                    updated++;
                }
                else skipped++;
                continue;
            }
            RebaseBigSeller(a, rebaseDir);
            current.Add(a);
            added++;
        }
        // Chỉ ghi store khi THỰC SỰ đổi (thêm mới / cập nhật / chế độ thay-thế) → auto-pull định kỳ không bắn
        // sự kiện Changed làm UI dựng lại danh sách khi không có gì mới.
        if (replace || added > 0 || updated > 0) BigSellerStore.Shared.ReplaceAll(current);
        return (added, updated, skipped);
    }

    /// <summary>Chữ ký các trường DÙNG CHUNG của 1 acc BigSeller (bỏ qua cookie/workbook/lựa-chọn-UI là field
    /// cục bộ-theo-máy) → so để biết Hub có sửa nội dung (shop…) hay không.</summary>
    private static string SharedSignature(BigSellerAccount a) => JsonSerializer.Serialize(new
    {
        a.Label, a.Email, a.KiotProxyKey, a.Region, a.ProxyType, a.Shops,
    });

    /// <summary>Gộp danh sách Shopee account (kèm proxy) vào store. append = replace:false.</summary>
    public static (int added, int skipped) MergeShopee(List<ShopeeAccount> imported, bool replace)
    {
        var current = replace ? new List<ShopeeAccount>() : AccountStore.Shared.Accounts.ToList();
        var seenLogin = current.Select(LoginKey).Where(k => k.Length > 0).ToHashSet();
        var seenId = current.Select(a => a.Id).ToHashSet();
        int added = 0, skipped = 0;
        foreach (var a in imported)
        {
            var key = LoginKey(a);
            var dup = (key.Length > 0 && seenLogin.Contains(key)) || seenId.Contains(a.Id);
            if (!replace && dup) { skipped++; continue; }
            current.Add(a);
            if (key.Length > 0) seenLogin.Add(key);
            seenId.Add(a.Id);
            added++;
        }
        if (replace || added > 0) AccountStore.Shared.ReplaceAll(current);
        return (added, skipped);
    }

    private static string EmailKey(BigSellerAccount a) => (a.Email ?? "").Trim().ToLowerInvariant();
    private static string LoginKey(ShopeeAccount a) => (a.ShopeeAccountLogin ?? "").Trim().ToLowerInvariant();

    private static void RebaseBigSeller(BigSellerAccount a, string? rebaseDir)
    {
        // WorkbookPath tuyệt đối theo máy cũ → đổi sang thư mục mới (giữ tên file) nếu người dùng chỉ định.
        if (!string.IsNullOrWhiteSpace(rebaseDir) && !string.IsNullOrWhiteSpace(a.WorkbookPath))
            a.WorkbookPath = Path.Combine(rebaseDir, Path.GetFileName(a.WorkbookPath));
        // CookieFile → trỏ tới file cookie vừa giải nén vào máy này (theo tên file).
        if (!string.IsNullOrWhiteSpace(a.CookieFile))
        {
            var local = Path.Combine(CookieDir, Path.GetFileName(a.CookieFile));
            a.CookieFile = File.Exists(local) ? local : "";
        }
    }

    private static void AddFile(ZipArchive zip, string path, string entryName)
    {
        if (File.Exists(path)) zip.CreateEntryFromFile(path, entryName);
    }

    private static T? Deserialize<T>(ZipArchiveEntry entry)
    {
        try { using var s = entry.Open(); return JsonSerializer.Deserialize<T>(s); }
        catch { return default; }
    }
}
