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

    /// <summary>Key ảnh Update dùng chung trên Hub — PHẢI khớp FileStoreConfigService.DefaultUpdateImageFile (server).
    /// Chữ thường cố định vì VM Linux phân biệt hoa-thường.</summary>
    public const string DefaultUpdateImageRemote = "images/default-update.jpg";

    // Chống đua ghi khi nhiều đích Update kéo cùng 1 file asset về cùng đường cache (hash-skip bên trong → chỉ
    // đứa đầu tải, các đứa sau bỏ qua). Rẻ vì asset nhỏ + hiếm.
    private static readonly SemaphoreSlim _assetGate = new(1, 1);

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
        // AI config giờ CHỈ sửa trên Hub (trang Cấu hình AI) — client KHÔNG đẩy ai.json nữa để bản cache cũ
        // không đè bản Hub mới (client chỉ pull về làm cache/fallback offline).
        if (await PushIfChangedAsync(manifest, SharedFile("scrape-targets.json"), "config/scrape-targets.json", ct)) n++;
        if (await PushIfChangedAsync(manifest, SharedFile("kiot-proxies.json"), "config/kiot-proxies.json", ct)) n++;

        // Cookie: chỉ đè kho khi token local MỚI HƠN (so iat JWT) — kho Hub luôn giữ token mới nhất mỗi acc,
        // tránh push sau scrape (client còn giữ bản seed cũ) đè chết token máy khác vừa đăng nhập.
        if (Directory.Exists(CookieDir))
            foreach (var f in Directory.GetFiles(CookieDir, "*.json"))
                if (await PushCookieIfNewerAsync(manifest, f, "cookies/" + Path.GetFileName(f), ct)) n++;

        // Workbook Excel (dữ liệu sản phẩm) — đẩy theo từng tk BigSeller để máy khác kéo về chạy được.
        foreach (var acct in BigSellerStore.Shared.Accounts)
        {
            if (acct.UsesHubData) continue;   // acc hub-mode: dữ liệu ở kho Postgres, KHÔNG còn workbook để đẩy
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
        int bsA = 0, bsU = 0, bsS = 0, shA = 0, shU = 0, shS = 0, cookies = 0;
        var ai = false;
        // MIRROR chỉ trên CLIENT: khớp trọn vẹn danh sách acc theo Hub (cập nhật + thêm + XÓA acc thừa). Máy Hub
        // KHÔNG mirror (là nguồn sự thật; nút "Đồng bộ acc"/handoff-pull trên Hub chỉ gộp, không tự xóa/revert).
        var mirror = !HubServerConfigStore.Shared.Current.Enabled;

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
                var localPath = Path.Combine(CookieDir, Path.GetFileName(m.Name));
                // TOKEN MỚI HƠN THẮNG (so iat JWT — xem LocalCookieShouldWin): giữ local khi máy này tự
                // login/refresh gần hơn; nhận bản Hub khi local thiếu / hết hạn / CŨ HƠN (BigSeller xoay
                // token → bản cũ chết dần; giữ khư khư bản cũ = kẹt "login first" phải đăng nhập tay mãi).
                if (LocalCookieShouldWin(localPath, bytes)) continue;
                await File.WriteAllBytesAsync(localPath, bytes, ct);
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
                (bsA, bsU, bsS) = BackupService.MergeBigSeller(list, replace: false, rebaseDir: null, mirror: mirror);
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
                (shA, shU, shS) = BackupService.MergeShopee(list, replace: false, mirror: mirror);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }

        // 4) AI (ghi đè cache — nguồn sự thật ở Hub, client chỉ giữ bản cache/fallback).
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

        // 4b) Kho KiotProxy dùng chung (ghi đè — Hub là nguồn sự thật; client tự nhận proxy mới).
        // CHỈ client mới ghi đè: máy HUB KHÔNG tự ghi đè kho của chính nó bằng bản blob (có thể cũ hơn bản
        // admin vừa sửa tay chưa kịp push) — tránh mất chỉnh sửa. (Auto-pull đã bỏ qua Hub; đây chặn cả nút
        // "Đồng bộ acc" tay + handoff-pull chạy trên máy Hub.)
        if (!HubServerConfigStore.Shared.Current.Enabled)
        {
            try
            {
                var pxBytes = await _client.DownloadAsync("config/kiot-proxies.json", ct);
                if (pxBytes is not null)
                {
                    var keys = JsonSerializer.Deserialize<List<string>>(NoBom(pxBytes)) ?? [];
                    Shopee.Core.Proxy.KiotProxyPoolStore.Shared.ReplaceAll(keys);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }

        // 5) Workbook Excel — tải về hub-cache và TRỎ WorkbookPath sang bản local (để máy này scrape được).
        try
        {
            var accts = BigSellerStore.Shared.Accounts.ToList();
            var changed = false;
            foreach (var acct in accts)
            {
                // acc hub-mode: dữ liệu ở kho Postgres → KHÔNG tải workbook và KHÔNG rebase WorkbookPath (giữ
                // nguyên đường cũ để nếu user chuyển acc về excel-mode thì bản workbook cũ vẫn còn dùng được).
                if (acct.UsesHubData) continue;
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

        return new ImportResult(bsA, bsS, shA, shS, ai, cookies, bsU, shU);
    }

    /// <summary>Bỏ BOM UTF-8 (EF BB BF) ở đầu nếu có. Các store ghi config bằng Encoding.UTF8 (CÓ BOM); deserialize
    /// THẲNG byte[] sẽ ném "'0xEF' is an invalid start of a value" → đây là lý do BigSeller/Shopee/AI = 0 khi kéo
    /// (Hub đọc bằng ReadAllText nên tự bỏ BOM → không lộ lỗi). Bỏ BOM ở client cho khớp.</summary>
    private static ReadOnlySpan<byte> NoBom(byte[] b) =>
        b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? b.AsSpan(3) : b.AsSpan();

    /// <summary>Kéo 1 file "dùng chung" (vd ảnh Update mặc định) từ Hub về <paramref name="localPath"/>. Sao y khối
    /// kéo workbook: manifest → bỏ qua nếu bản local đã khớp hash → tải qua _bulkHttp (5') → ghi BYTE THÔ (KHÔNG NoBom,
    /// đây là ảnh nhị phân). Trả về localPath nếu có sẵn/tải được; null nếu Hub CHƯA CÓ (404) / offline / lỗi →
    /// caller tự fallback về đường ảnh local mặc định. BEST-EFFORT: mọi lỗi nuốt hết, không bao giờ ném vào đường chạy.</summary>
    public async Task<string?> PullSharedAssetAsync(string remoteName, string localPath, CancellationToken ct = default)
    {
        await _assetGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<FileManifestEntry> manifest;
            try { manifest = await _client.ManifestAsync(ct); }
            catch { return File.Exists(localPath) ? localPath : null; }   // offline → dùng bản cache cũ nếu còn

            var entry = manifest.FirstOrDefault(m => string.Equals(m.Name, remoteName, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return File.Exists(localPath) ? localPath : null;   // Hub chưa upload ảnh → fallback

            // Bản local đã khớp hash manifest → khỏi tải lại (giữ guard Hash không rỗng như khối workbook).
            if (File.Exists(localPath) && !string.IsNullOrEmpty(entry.Hash)
                && string.Equals(LocalSha256(localPath), entry.Hash, StringComparison.OrdinalIgnoreCase))
                return localPath;

            var bytes = await _client.DownloadAsync(entry.Name, ct);
            if (bytes is null) return File.Exists(localPath) ? localPath : null;   // 404/offline → cache cũ nếu còn

            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            await File.WriteAllBytesAsync(localPath, bytes, ct);
            return localPath;
        }
        catch { return File.Exists(localPath) ? localPath : null; }
        finally { _assetGate.Release(); }
    }

    /// <summary>SHA-256 (hex hoa) của 1 file local — so với manifest.Hash để bỏ qua tải workbook không đổi.</summary>
    private static string LocalSha256(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    /// <summary>True = GIỮ file cookie LOCAL, KHÔNG đè bằng bản kéo từ Hub. Nguyên tắc: TOKEN MỚI HƠN THẮNG
    /// (so iat của JWT muc_token) — BigSeller XOAY token định kỳ, bản cũ có thể đã bị vô hiệu. Trước đây
    /// "local còn hạn &amp; khác Hub = giữ" làm client cầm bản seed CŨ không bao giờ nhận token tươi từ Hub
    /// (kẹt token chết → phải login tay lại mãi). Local thiếu / hết hạn / trùng / CŨ HƠN → nhận bản Hub.</summary>
    private static bool LocalCookieShouldWin(string localPath, byte[] incoming)
    {
        if (BigSellerCookieEngine.GetFileAuthTokenInfo(localPath) is not { } lt) return false; // local không có token → seed
        if (lt.Expires is { } exp && exp <= DateTimeOffset.Now) return false;                  // local hết hạn → refresh
        var hubToken = ReadMucTokenValue(incoming);
        if (hubToken is null) return true;                                                      // bản Hub không có token → giữ local
        if (string.Equals(hubToken, lt.Value, StringComparison.Ordinal)) return false;          // trùng → đè vô hại
        if (BigSellerCookieEngine.GetJwtIssuedAt(lt.Value) is { } localIat
            && BigSellerCookieEngine.GetJwtIssuedAt(hubToken) is { } hubIat)
            return localIat >= hubIat;   // giữ local CHỈ khi token local không cũ hơn (máy này login/refresh gần hơn)
        return true;                     // không so được iat (token lạ) → giữ local như hành vi cũ
    }

    /// <summary>Đẩy CHỈ các file cookie local có token MỚI HƠN bản kho Hub — để token máy này TỰ đăng nhập
    /// lan sang Hub + các máy khác (trước đây client không bao giờ đẩy cookie → mỗi máy 1 phiên, login máy
    /// này xong máy kia "mất cookie" và ngược lại). Trả về số file đã đẩy.</summary>
    public async Task<int> PushCookiesIfNewerAsync(CancellationToken ct = default)
    {
        Dictionary<string, FileManifestEntry> manifest;
        try { manifest = (await _client.ManifestAsync(ct)).ToDictionary(m => m.Name, m => m, StringComparer.OrdinalIgnoreCase); }
        catch { return 0; }
        var n = 0;
        if (Directory.Exists(CookieDir))
            foreach (var f in Directory.GetFiles(CookieDir, "*.json"))
            {
                try { if (await PushCookieIfNewerAsync(manifest, f, "cookies/" + Path.GetFileName(f), ct)) n++; }
                catch (Exception ex) when (ex is not OperationCanceledException) { }
            }
        return n;
    }

    /// <summary>Kéo CHỈ cookie từ kho Hub về máy này khi bản kho MỚI HƠN — cho máy HUB nhận token client vừa
    /// tự đăng nhập (máy Hub không chạy <see cref="PullAccountsAsync"/> nên trước đây không bao giờ nhận).
    /// An toàn nhờ <see cref="LocalCookieShouldWin"/> mới-hơn-thắng. Trả về số file đã nhận.</summary>
    public async Task<int> PullCookiesIfNewerAsync(CancellationToken ct = default)
    {
        List<FileManifestEntry> manifest;
        try { manifest = await _client.ManifestAsync(ct); }
        catch { return 0; }
        Directory.CreateDirectory(CookieDir);
        var n = 0;
        foreach (var m in manifest.Where(m => m.Name.StartsWith("cookies/", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var localPath = Path.Combine(CookieDir, Path.GetFileName(m.Name));
                if (File.Exists(localPath) && !string.IsNullOrEmpty(m.Hash)
                    && string.Equals(LocalSha256(localPath), m.Hash, StringComparison.OrdinalIgnoreCase))
                    continue;   // y hệt bản kho → khỏi tải
                var bytes = await _client.DownloadAsync(m.Name, ct);
                if (bytes is null || LocalCookieShouldWin(localPath, bytes)) continue;
                await File.WriteAllBytesAsync(localPath, bytes, ct);
                n++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        return n;
    }

    /// <summary>Upload 1 file cookie CHỈ khi token local MỚI HƠN bản kho (so iat JWT) — chặn đẩy bản seed CŨ
    /// đè token tươi máy khác vừa đăng nhập (kho Hub luôn giữ token mới nhất của mỗi acc).</summary>
    private async Task<bool> PushCookieIfNewerAsync(Dictionary<string, FileManifestEntry> manifest, string localPath, string remoteName, CancellationToken ct)
    {
        if (BigSellerCookieEngine.GetFileAuthTokenInfo(localPath) is not { } lt) return false;   // local không có token → đừng đẩy rác
        if (!manifest.TryGetValue(remoteName, out var m))
        {
            await _client.UploadAsync(remoteName, await File.ReadAllBytesAsync(localPath, ct), null, ct);
            return true;
        }
        if (m.Size == new FileInfo(localPath).Length
            && string.Equals(LocalSha256(localPath), m.Hash, StringComparison.OrdinalIgnoreCase))
            return false;   // không đổi → bỏ qua
        // Khác nội dung → chỉ đè khi token local KHÔNG CŨ HƠN bản kho (file cookie nhỏ, tải về so là rẻ).
        try
        {
            var remote = await _client.DownloadAsync(remoteName, ct);
            if (remote is not null
                && BigSellerCookieEngine.GetJwtIssuedAt(lt.Value) is { } localIat
                && BigSellerCookieEngine.GetJwtIssuedAt(ReadMucTokenValue(remote)) is { } remoteIat
                && localIat < remoteIat)
                return false;   // kho đang giữ token mới hơn → giữ nguyên
        }
        catch (OperationCanceledException) { throw; }
        catch { /* không đọc được bản kho → cứ đẩy như hành vi cũ */ }
        await _client.UploadAsync(remoteName, await File.ReadAllBytesAsync(localPath, ct), null, ct);
        return true;
    }

    /// <summary>Giá trị muc_token (cookie giữ phiên BigSeller) trong JSON cookie dạng bytes (đã bỏ BOM). null nếu thiếu.</summary>
    private static string? ReadMucTokenValue(byte[] bytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(NoBom(bytes).ToArray());
            var cookiesEl = doc.RootElement.TryGetProperty("cookies", out var cp) ? cp : doc.RootElement;
            if (cookiesEl.ValueKind != JsonValueKind.Array) return null;
            foreach (var ck in cookiesEl.EnumerateArray())
            {
                if (ck.ValueKind != JsonValueKind.Object) continue;
                if (!ck.TryGetProperty("name", out var np) ||
                    !string.Equals(np.GetString(), BigSellerCookieEngine.AuthCookieName, StringComparison.OrdinalIgnoreCase))
                    continue;
                var domain = ck.TryGetProperty("domain", out var dp) ? (dp.GetString() ?? "") : "";
                if (!domain.Contains("bigseller", StringComparison.OrdinalIgnoreCase)) continue;
                return ck.TryGetProperty("value", out var vp) ? vp.GetString() : null;
            }
            return null;
        }
        catch { return null; }
    }
}
