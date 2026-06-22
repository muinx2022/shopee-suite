namespace Shopee.Core.Accounts;

/// <summary>
/// Tài khoản Shopee dùng chung cho mọi module (Scrape, Search). Gồm 3 phần: thông tin đăng nhập
/// Shopee, cấu hình proxy, và thư mục profile trình duyệt. Phần cấu hình riêng của từng module
/// (Scrape: workbook/sheet/dòng; Search: từ khóa) KHÔNG nằm ở đây — module tự lưu, tham chiếu
/// account qua <see cref="Id"/>.
/// </summary>
public sealed class ShopeeAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "";

    // ── Đăng nhập Shopee ──
    /// <summary>"user|pass" hoặc "user|pass|.shopee.vn=SPC_F=value".</summary>
    public string ShopeeAccountLogin { get; set; } = "";
    public bool OpenWithShopeeAccount { get; set; } = true;

    // ── Proxy ──
    public string KiotProxyKey { get; set; } = "";
    public string Region { get; set; } = "random";     // random | bac | trung | nam
    public string ProxyType { get; set; } = "http";     // http | socks5
    public string ManualProxy { get; set; } = "";       // host:port hoặc http://… (nếu không dùng Kiot)
    public bool RequireProxy { get; set; } = true;

    // ── Profile trình duyệt ──
    /// <summary>Tương đối dưới gốc profile dùng chung; trống = "profiles/{Id}".</summary>
    public string ProfileRelativePath { get; set; } = "";

    // ── Trạng thái ──
    public bool Disabled { get; set; }
    public string? LastError { get; set; }

    /// <summary>Username (phần trước dấu '|') — chỉ để hiển thị.</summary>
    public string Username =>
        string.IsNullOrEmpty(ShopeeAccountLogin) ? "" : ShopeeAccountLogin.Split('|')[0].Trim();

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Label) ? Label
        : !string.IsNullOrWhiteSpace(Username) ? Username
        : "Account " + Id[..Math.Min(8, Id.Length)];

    /// <summary>Mô tả proxy ngắn gọn để hiển thị trong lưới.</summary>
    public string ProxySummary =>
        !string.IsNullOrWhiteSpace(KiotProxyKey) ? $"Kiot …{Tail(KiotProxyKey)} ({Region})"
        : !string.IsNullOrWhiteSpace(ManualProxy) ? ManualProxy
        : "(không proxy)";

    public void EnsureProfilePath()
    {
        if (string.IsNullOrWhiteSpace(ProfileRelativePath))
            ProfileRelativePath = "profiles/" + Id;
    }

    private static string Tail(string s) => s.Length <= 4 ? s : s[^4..];
}
