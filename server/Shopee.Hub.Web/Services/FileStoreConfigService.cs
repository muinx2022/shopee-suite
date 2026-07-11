using Shopee.Core.Accounts;
using Shopee.Core.Ai;
using Shopee.Core.BigSeller;
using Shopee.Core.Coordination;
using Shopee.Hub;

namespace Shopee.Hub.Web.Services;

/// <summary>
/// Đọc/ghi các file cấu hình dùng chung QUA kho file của Hub (bảng files + blob) — web-hub là NGUỒN SỰ THẬT.
/// Client kéo về qua /manifest + /files như cũ; mỗi lần lưu bump version → client tự pull trong ≤3 phút.
///
/// QUAN TRỌNG về JSON: các store WPF ghi bằng <c>JsonSerializer.Serialize(list, {WriteIndented=true})</c> =
/// PascalCase (tên property C# nguyên bản). Phải GIỮ ĐÚNG kiểu đó (khác camelCase của API /fleet) để client
/// đọc lại được. Đọc thì case-insensitive + strip BOM (store WPF ghi UTF-8 CÓ BOM).
/// </summary>
public sealed class FileStoreConfigService
{
    public const string BigSellerFile = "config/bigseller.json";
    public const string AccountsFile = "config/accounts.json";
    public const string AiFile = "config/ai.json";
    public const string KiotProxiesFile = "config/kiot-proxies.json";

    /// <summary>Ảnh mặc định DÙNG CHUNG cho luồng Update — 1 file cho cả hệ, client tự kéo về (không phải chọn tay).
    /// Tiền tố <c>images/</c> (KHÔNG phải config/) để né chặn AllowClientConfigPush + ngữ nghĩa JSON. Chữ THƯỜNG
    /// cố định vì VM Linux phân biệt hoa-thường. Client dùng đúng literal này (xem HubConfigSync.DefaultUpdateImageRemote).</summary>
    public const string DefaultUpdateImageFile = "images/default-update.jpg";

    // Khớp store WPF: PascalCase + indented. KHÔNG dùng web defaults (camelCase) ở đây.
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions ReadOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly HubDatabase _db;
    public FileStoreConfigService(HubDatabase db) => _db = db;

    /// <summary>Version manifest hiện tại của 1 file (0 nếu chưa có) — dùng làm If-Match khi lưu.</summary>
    public int VersionOf(string name) =>
        _db.ListFiles().FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal))?.Version ?? 0;

    private T ReadList<T>(string name) where T : new()
    {
        var bytes = _db.ReadFile(name);
        if (bytes is null || bytes.Length == 0) return new T();
        try { return JsonSerializer.Deserialize<T>(NoBom(bytes), ReadOpts) ?? new T(); }
        catch { return new T(); }
    }

    /// <summary>Ghi 1 object cấu hình với optimistic concurrency. Trả (ok, version, conflict). conflict != null
    /// (thường "version-conflict") = có client vừa đẩy chen giữa → UI báo tải lại.</summary>
    public FilePutResponse Save(string name, object value, int ifMatch)
    {
        var json = JsonSerializer.Serialize(value, value.GetType(), WriteOpts);
        var bytes = new UTF8Encoding(false).GetBytes(json);   // KHÔNG BOM
        return _db.PutFile(name, bytes, ifMatch, "hub-web");
    }

    // ── Đọc từng loại (bản sao — sửa xong gọi Save để ghi lại) ──
    public List<BigSellerAccount> BigSellerAccounts() => ReadList<List<BigSellerAccount>>(BigSellerFile);
    public List<ShopeeAccount> ShopeeAccounts() => ReadList<List<ShopeeAccount>>(AccountsFile);
    public List<string> KiotProxies() => ReadList<List<string>>(KiotProxiesFile);

    public AiConfig Ai()
    {
        var bytes = _db.ReadFile(AiFile);
        if (bytes is null || bytes.Length == 0) return new AiConfig();
        try { return JsonSerializer.Deserialize<AiConfig>(NoBom(bytes), ReadOpts) ?? new AiConfig(); }
        catch { return new AiConfig(); }
    }

    // ── Gộp acc Shopee từ client (endpoint /accounts/append & /accounts/remove) ──
    /// <summary>Gộp danh sách acc Shopee client gửi lên vào config/accounts.json: KHỚP theo Id rồi login, có
    /// thì cập nhật field dùng-chung, không có thì THÊM (đánh HubOwned=true). KHÔNG BAO GIỜ xoá. Bump version →
    /// client khác pull về. Không có gì đổi thật → KHÔNG lưu (khỏi bump version làm cả fleet re-pull vô ích).
    /// Thử lại tối đa 3 lần nếu dính version-conflict (client khác đẩy chen). Trả số acc mới.</summary>
    public int AppendShopeeAccounts(IReadOnlyList<ShopeeAccount> incoming)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var list = ShopeeAccounts();
            var added = 0;
            var changed = false;
            foreach (var a in incoming)
            {
                if (a is null) continue;
                var login = a.ShopeeAccountLogin?.Trim() ?? "";
                var match = list.FirstOrDefault(x => x.Id == a.Id)
                            ?? (login.Length > 0 ? list.FirstOrDefault(x => string.Equals(x.ShopeeAccountLogin?.Trim(), login, StringComparison.OrdinalIgnoreCase)) : null);
                if (match is null)
                {
                    a.HubOwned = true;
                    list.Add(a);
                    added++;
                    changed = true;
                }
                else
                {
                    // Cập nhật field dùng-chung (giữ Id + trạng thái riêng-máy của bản Hub). Chỉ gán khi KHÁC
                    // giá trị cũ → nếu incoming trùng khớp bản đang có thì không đánh dấu đổi (khỏi bump version).
                    if (!string.IsNullOrWhiteSpace(a.Label) && match.Label != a.Label) { match.Label = a.Label; changed = true; }
                    if (login.Length > 0 && match.ShopeeAccountLogin != login) { match.ShopeeAccountLogin = login; changed = true; }
                    if (match.KiotProxyKey != a.KiotProxyKey) { match.KiotProxyKey = a.KiotProxyKey; changed = true; }
                    if (match.Region != a.Region) { match.Region = a.Region; changed = true; }
                    if (match.ProxyType != a.ProxyType) { match.ProxyType = a.ProxyType; changed = true; }
                    if (match.ManualProxy != a.ManualProxy) { match.ManualProxy = a.ManualProxy; changed = true; }
                    if (match.RequireProxy != a.RequireProxy) { match.RequireProxy = a.RequireProxy; changed = true; }
                }
            }
            if (!changed) return added;   // KHÔNG có gì đổi thật → không Save, không bump version (khỏi cả fleet re-pull).
            var res = Save(AccountsFile, list, VersionOf(AccountsFile));
            if (res.Ok) return added;
            // version-conflict → vòng lặp đọc lại bản mới rồi gộp lại.
        }
        return 0;
    }

    /// <summary>Xoá 1 acc Shopee khỏi config/accounts.json (trang Config trên web / nút xoá acc lỗi). Thử lại
    /// nếu dính version-conflict. Trả true nếu đã xoá.</summary>
    public bool RemoveShopeeAccount(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var list = ShopeeAccounts();
            var n = list.RemoveAll(x => x.Id == id);
            if (n == 0) return false;
            var res = Save(AccountsFile, list, VersionOf(AccountsFile));
            if (res.Ok) return true;
        }
        return false;
    }

    /// <summary>Set CookieFile của 1 acc BigSeller = TÊN FILE THUẦN (không path) để client RelinkCookie nối được
    /// cookie đa nền tảng (client Linux dùng Path.GetFileName KHÔNG tách được path Windows). Gọi sau khi hub
    /// login lưu cookie. No-op nếu đã đúng. Thử lại nếu dính version-conflict.</summary>
    public void SetBigSellerCookieFile(string acctId, string cookieFileName)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var list = BigSellerAccounts();
            var acc = list.FirstOrDefault(a => a.Id == acctId);
            if (acc is null) return;
            if (string.Equals(acc.CookieFile, cookieFileName, StringComparison.Ordinal)) return;   // đã đúng
            acc.CookieFile = cookieFileName;
            if (Save(BigSellerFile, list, VersionOf(BigSellerFile)).Ok) return;
        }
    }

    /// <summary>Đặt DataSource ("excel" | "hub") của 1 acc BigSeller khi cutover kho dữ liệu ở trang Fleet. Field
    /// DÙNG CHUNG → bump version → client tự đổi hành vi khi sync về (≤3'). Đọc bản MỚI NHẤT ngay trước khi ghi +
    /// thử lại nếu dính version-conflict (import/export đã chạy xong nên KHÔNG được để mất cú lật cờ này). No-op nếu
    /// đã đúng giá trị.</summary>
    public void SetBigSellerDataSource(string acctId, string dataSource)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var list = BigSellerAccounts();
            var acc = list.FirstOrDefault(a => a.Id == acctId);
            if (acc is null) return;
            if (string.Equals(acc.DataSource, dataSource, StringComparison.Ordinal)) return;   // đã đúng
            acc.DataSource = dataSource;
            if (Save(BigSellerFile, list, VersionOf(BigSellerFile)).Ok) return;
        }
    }

    /// <summary>Bỏ BOM UTF-8 đầu file (store WPF ghi CÓ BOM; System.Text.Json ném lỗi nếu còn BOM).</summary>
    private static string NoBom(byte[] bytes)
    {
        var s = Encoding.UTF8.GetString(bytes);
        return s.Length > 0 && s[0] == '﻿' ? s[1..] : s;
    }
}
