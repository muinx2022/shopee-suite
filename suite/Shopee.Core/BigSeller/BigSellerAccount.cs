namespace Shopee.Core.BigSeller;

/// <summary>
/// Tài khoản BigSeller dùng chung (Scrape + Update Product). Gồm thông tin đăng nhập, đường dẫn
/// workbook Excel, file cookie (muc_token — dùng chung cho cả scrape lẫn update), và danh sách
/// shop; mỗi shop ứng với một sheet trong workbook.
/// </summary>
public sealed class BigSellerAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "";
    public string Email { get; set; } = "";

    /// <summary>Đường dẫn file Excel chứa dữ liệu các shop (mỗi shop 1 sheet).</summary>
    public string WorkbookPath { get; set; } = "";
    /// <summary>File cookie BigSeller (muc_token) — lưu khi đăng nhập, dùng chung.</summary>
    public string CookieFile { get; set; } = "";

    public List<BigSellerShop> Shops { get; set; } = [];

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Label) ? Label
        : !string.IsNullOrWhiteSpace(Email) ? Email
        : "BigSeller " + Id[..Math.Min(8, Id.Length)];

    /// <summary>Đã có file cookie hợp lệ chưa.</summary>
    public bool HasCookie => !string.IsNullOrWhiteSpace(CookieFile) && File.Exists(CookieFile);
}
