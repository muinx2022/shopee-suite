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
    int BigSellerUpdated = 0, int ShopeeUpdated = 0);

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
        int bsAdded = 0, bsUpdated = 0, bsSkipped = 0, shAdded = 0, shUpdated = 0, shSkipped = 0, cookies = 0;
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
            (shAdded, shUpdated, shSkipped) = MergeShopee(imported, replace);
        }

        // 4) Cấu hình AI (luôn ghi đè — là 1 cấu hình duy nhất).
        if (opt.AiConfig && zip.GetEntry("ai.json") is { } aiEntry && Deserialize<AiConfig>(aiEntry) is { } cfg)
        {
            AiConfigStore.Shared.Save(cfg);
            aiImported = true;
        }

        return new ImportResult(bsAdded, bsSkipped, shAdded, shSkipped, aiImported, cookies, bsUpdated, shUpdated);
    }

    /// <summary>Gộp danh sách BigSeller vào store (dùng chung cho import-zip + đồng bộ Hub). append = replace:false.
    /// Acc ĐÃ CÓ (theo Id/email) mà nội dung DÙNG CHUNG khác (shop/label/email/proxy) → CẬP NHẬT xuống (Hub là
    /// nguồn sự thật: sửa shop ở Hub lan tới mọi client), GIỮ NGUYÊN field local: cookie, workbook, lựa chọn UI.</summary>
    public static (int added, int updated, int skipped) MergeBigSeller(List<BigSellerAccount> imported, bool replace, string? rebaseDir, bool mirror = false)
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
                var changed = false;
                // Đồng nhất Id theo Hub khi acc khớp bằng EMAIL nhưng Id LỆCH (client này từng tạo/đồng bộ acc
                // độc lập nên mang Id khác Hub). Hub giao việc import/update/scrape resolve acc theo
                // `BigsellerId == acct.Id` + kho SP Postgres keyed theo acct.Id → Id lệch khiến job Hub báo
                // "không thấy tài khoản" DÙ cookie + shop vẫn về (khớp theo email / graft tại chỗ). (Trước đây
                // còn lỗi kéo workbook nhầm thư mục `workbooks/{acct.Id}/` — nay workbook đã bỏ đồng bộ.) Chỉ
                // chỉnh ở chế độ MIRROR (client) — Hub là
                // nguồn sự thật của Id. An toàn khỏi trùng Id: chỉ tới được nhánh email khi KHÔNG acc nào trùng
                // Id Hub (trùng thì đã khớp bằng Id ở trên). Lưu ý: tiến độ scrape local (key theo acct.Id) dưới
                // Id cũ bị mồ côi — chấp nhận được (acc lệch Id vốn không chạy được nên không có tiến độ giá trị).
                if (mirror && existing.Id != a.Id) { existing.Id = a.Id; changed = true; }
                if (SharedSignature(existing) != SharedSignature(a))
                {
                    // Gộp shop GIỮ NGUYÊN OBJECT cũ thay vì thay nguyên danh sách bằng object mới: cấu hình
                    // chạy riêng-máy (khoảng dòng / số worker / reload) tự sống sót không cần graft, và VM
                    // đang cầm reference (SelectedShop của Update/Scrape) không bị "mồ côi" — trước đây swap
                    // object làm ô "Update worker 5" ghi vào object chết (không bao giờ được lưu xuống file),
                    // đến lúc Hub giao việc AssignmentWorker resolve object live (vẫn 1) → chạy 1 lane.
                    existing.Shops = MergeShopsKeepInstance(existing.Shops, a.Shops);
                    existing.Label = a.Label; existing.Email = a.Email; existing.Password = a.Password;
                    existing.EmailPassword = a.EmailPassword;
                    existing.KiotProxyKey = a.KiotProxyKey; existing.Region = a.Region; existing.ProxyType = a.ProxyType;
                    existing.DataSource = a.DataSource;   // field CHUNG (excel/hub): Hub là nguồn sự thật cho chế độ kho
                    changed = true;
                }
                // Nối lại CookieFile cho acc ĐÃ tồn tại: trước đây nhánh này bỏ qua cookie HOÀN TOÀN, nên acc
                // được sync TRƯỚC khi đăng nhập (CookieFile="") thì dù sau đó cookie đã về máy vẫn kẹt "" mãi
                // → client không thấy phiên, Scrape/Update báo "log in first". RelinkCookie trỏ lại theo TÊN
                // file bản Hub gửi nếu cookie đã nằm trong CookieDir local (SharedSignature cố ý bỏ qua cookie
                // nên KHÔNG bao giờ tự kích hoạt nhánh cập nhật → phải nối riêng ở đây).
                if (RelinkCookie(existing, a)) changed = true;
                if (mirror) existing.HubOwned = true;   // acc có ở Hub → đánh dấu Hub-quản-lý
                if (changed) updated++; else skipped++;
                continue;
            }
            RebaseBigSeller(a, rebaseDir);
            if (mirror) a.HubOwned = true;   // acc mới từ Hub
            current.Add(a);
            added++;
        }
        var removed = 0;
        // Mirror-xóa CHỈ acc ĐẾN TỪ HUB (HubOwned) mà Hub đã bỏ — KHÔNG đụng acc tạo/đăng nhập TẠI CHỖ.
        if (mirror && imported.Count > 0)
        {
            var keepIds = imported.Select(x => x.Id).ToHashSet();
            var keepEmails = imported.Select(EmailKey).Where(k => k.Length > 0).ToHashSet();
            removed = current.RemoveAll(x => x.HubOwned && !keepIds.Contains(x.Id) && !(EmailKey(x).Length > 0 && keepEmails.Contains(EmailKey(x))));
        }
        // Chỉ ghi store khi THỰC SỰ đổi (thêm mới / cập nhật / xóa / chế độ thay-thế) → auto-pull định kỳ không bắn
        // sự kiện Changed làm UI dựng lại danh sách khi không có gì mới.
        if (replace || added > 0 || updated > 0 || removed > 0) BigSellerStore.Shared.ReplaceAll(current);
        return (added, updated, skipped);
    }

    /// <summary>Chữ ký các trường DÙNG CHUNG của 1 acc BigSeller (bỏ qua cookie/workbook/lựa-chọn-UI là field
    /// cục bộ-theo-máy) → so để biết Hub có sửa nội dung (shop…) hay không. Shops chỉ tính phần Hub quản
    /// (tên/sheet/map cột/AI/crawl); cấu hình CHẠY (StartRow/EndRow/worker/reload) là riêng-máy — nếu tính
    /// vào chữ ký thì set worker trên client làm lệch chữ ký → pull sau đó lấy bản Hub đè ngược mất giá trị.</summary>
    private static string SharedSignature(BigSellerAccount a) => JsonSerializer.Serialize(new
    {
        a.Label, a.Email, a.Password, a.EmailPassword, a.KiotProxyKey, a.Region, a.ProxyType, a.DataSource,
        Shops = a.Shops.Select(s => new
        {
            s.Id, s.Name, s.ShopeeDataSheet, s.ColumnMap, s.BigSellerCrawlUrl, s.BigSellerImportFromClaimedTab,
            s.OpenAiModel, s.OpenAiApiKeyFile, s.OpenAiBatchSize,
        }).ToList(),
    });

    /// <summary>Gộp danh sách shop bản Hub vào danh sách cũ nhưng GIỮ NGUYÊN OBJECT shop cũ (khớp theo Id,
    /// fallback: cùng sheet): chép các field Hub-quản (đúng bộ trong SharedSignature) lên object cũ; shop
    /// mới thêm nguyên bản; shop Hub đã bỏ rơi khỏi danh sách. Field RIÊNG-MÁY (StartRow/EndRow/worker/
    /// reload) nằm sẵn trên object cũ nên tự sống sót — không cần graft như KeepLocalRunConfig trước đây,
    /// và mọi VM đang giữ reference tới shop tiếp tục nhìn thấy đúng object được persist.</summary>
    private static List<BigSellerShop> MergeShopsKeepInstance(List<BigSellerShop> old, List<BigSellerShop> incoming)
    {
        // Khớp Id CHÍNH XÁC cho TẤT CẢ trước, rồi mới fallback theo sheet trên phần còn thừa — không thì shop
        // Hub thêm-mới trùng sheet (đứng trước) có thể "cướp" object cũ mà lẽ ra thuộc shop khớp Id ở sau,
        // làm shop kia mất cấu hình chạy riêng-máy (tệ hơn cả bản KeepLocalRunConfig khớp-độc-lập trước đây).
        var used = new HashSet<BigSellerShop>();
        var match = new BigSellerShop?[incoming.Count];
        for (int i = 0; i < incoming.Count; i++)
        {
            var o = old.FirstOrDefault(x => !used.Contains(x) && x.Id == incoming[i].Id);
            if (o is null) continue;
            used.Add(o); match[i] = o;
        }
        for (int i = 0; i < incoming.Count; i++)
        {
            if (match[i] is not null || string.IsNullOrWhiteSpace(incoming[i].ShopeeDataSheet)) continue;
            var o = old.FirstOrDefault(x => !used.Contains(x) && string.Equals(x.ShopeeDataSheet, incoming[i].ShopeeDataSheet, StringComparison.OrdinalIgnoreCase));
            if (o is null) continue;
            used.Add(o); match[i] = o;
        }

        var merged = new List<BigSellerShop>(incoming.Count);
        for (int i = 0; i < incoming.Count; i++)
        {
            var s = incoming[i];
            var o = match[i];
            if (o is null) { merged.Add(s); continue; }
            o.Id = s.Id;   // khớp qua fallback sheet → đồng nhất Id theo Hub (assignment resolve shop bằng Id)
            o.Name = s.Name; o.ShopeeDataSheet = s.ShopeeDataSheet; o.ColumnMap = s.ColumnMap;
            o.BigSellerCrawlUrl = s.BigSellerCrawlUrl; o.BigSellerImportFromClaimedTab = s.BigSellerImportFromClaimedTab;
            o.OpenAiModel = s.OpenAiModel; o.OpenAiApiKeyFile = s.OpenAiApiKeyFile; o.OpenAiBatchSize = s.OpenAiBatchSize;
            merged.Add(o);
        }
        return merged;
    }

    /// <summary>Gộp danh sách Shopee account (kèm proxy) vào store. append = replace:false. Acc ĐÃ CÓ (theo
    /// Id/login) mà field DÙNG CHUNG khác (login/proxy/label) → CẬP NHẬT xuống; GIỮ NGUYÊN field local (profile,
    /// cờ login, Disabled/lỗi, LastUsedTick). <paramref name="mirror"/>=true (client sync): còn XÓA acc local
    /// KHÔNG có ở Hub (chỉ khi list Hub không rỗng) → client khớp trọn vẹn danh sách Hub.</summary>
    public static (int added, int updated, int skipped) MergeShopee(List<ShopeeAccount> imported, bool replace, bool mirror = false)
    {
        var current = replace ? new List<ShopeeAccount>() : AccountStore.Shared.Accounts.ToList();
        int added = 0, updated = 0, skipped = 0;
        foreach (var a in imported)
        {
            var key = LoginKey(a);
            var existing = current.FirstOrDefault(x => x.Id == a.Id)
                ?? (key.Length > 0 ? current.FirstOrDefault(x => LoginKey(x) == key) : null);
            if (!replace && existing is not null)
            {
                // Cập nhật field DÙNG CHUNG (Hub là nguồn sự thật); GIỮ field riêng-máy.
                if (ShopeeSharedSignature(existing) != ShopeeSharedSignature(a))
                {
                    existing.Label = a.Label; existing.ShopeeAccountLogin = a.ShopeeAccountLogin;
                    existing.KiotProxyKey = a.KiotProxyKey; existing.Region = a.Region;
                    existing.ProxyType = a.ProxyType; existing.ManualProxy = a.ManualProxy;
                    existing.RequireProxy = a.RequireProxy;
                    updated++;
                }
                else skipped++;
                if (mirror) existing.HubOwned = true;   // acc có ở Hub → đánh dấu Hub-quản-lý (để prune đúng khi Hub bỏ)
                continue;
            }
            if (mirror) { RebaseShopee(a); a.HubOwned = true; }   // acc mới từ Hub: localize profile + đánh dấu Hub-owned
            current.Add(a);
            added++;
        }
        var removed = 0;
        // Mirror-xóa CHỈ acc ĐẾN TỪ HUB (HubOwned) mà Hub đã bỏ — KHÔNG đụng acc tạo TẠI CHỖ trên client
        // (Check Account / Add tay: HubOwned=false). Chốt non-empty chống wipe khi tải hụt.
        if (mirror && imported.Count > 0)
        {
            var keepIds = imported.Select(x => x.Id).ToHashSet();
            var keepLogins = imported.Select(LoginKey).Where(k => k.Length > 0).ToHashSet();
            removed = current.RemoveAll(x => x.HubOwned && !keepIds.Contains(x.Id) && !(LoginKey(x).Length > 0 && keepLogins.Contains(LoginKey(x))));
        }
        if (replace || added > 0 || updated > 0 || removed > 0) AccountStore.Shared.ReplaceAll(current);
        return (added, updated, skipped);
    }

    /// <summary>Chữ ký các trường DÙNG CHUNG của 1 acc Shopee (bỏ field riêng-máy: profile/cờ login/lỗi/LRU)
    /// → so để biết Hub có sửa nội dung (login/proxy/label) không.</summary>
    private static string ShopeeSharedSignature(ShopeeAccount a) => JsonSerializer.Serialize(new
    {
        a.Label, a.ShopeeAccountLogin, a.KiotProxyKey, a.Region, a.ProxyType, a.ManualProxy, a.RequireProxy,
    });

    /// <summary>Acc Shopee MỚI về từ máy khác: profile trình duyệt là RIÊNG-MÁY (không sync). Path TUYỆT ĐỐI (do
    /// máy khác lưu) → bỏ để <see cref="ShopeeAccount.EnsureProfilePath"/> đặt lại profiles/{Id} local; path tương
    /// đối giữ nguyên (giống mọi máy). Acc chưa có phiên → client tự đăng nhập (OpenWithShopeeAccount).</summary>
    private static void RebaseShopee(ShopeeAccount a)
    {
        if (!string.IsNullOrWhiteSpace(a.ProfileRelativePath) && Path.IsPathRooted(a.ProfileRelativePath))
            a.ProfileRelativePath = "";
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

    /// <summary>Trỏ <see cref="BigSellerAccount.CookieFile"/> của acc ĐÃ-tồn-tại sang file cookie local nếu
    /// hiện chưa dùng được (rỗng, hoặc trỏ vào file không còn) mà cookie — theo TÊN file bản Hub gửi (nguồn
    /// sự thật), fallback theo bản local cũ — đã có trong CookieDir. Trả true nếu có đổi. Vá lỗ "acc đăng nhập
    /// SAU khi đã sync không bao giờ được nối cookie ở client".</summary>
    private static bool RelinkCookie(BigSellerAccount existing, BigSellerAccount incoming)
    {
        if (!string.IsNullOrWhiteSpace(existing.CookieFile) && File.Exists(existing.CookieFile)) return false;
        var name = !string.IsNullOrWhiteSpace(incoming.CookieFile) ? Path.GetFileName(incoming.CookieFile)
                 : !string.IsNullOrWhiteSpace(existing.CookieFile) ? Path.GetFileName(existing.CookieFile)
                 : "";
        if (name.Length == 0) return false;
        var local = Path.Combine(CookieDir, name);
        if (!File.Exists(local)) return false;
        existing.CookieFile = local;
        return true;
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
