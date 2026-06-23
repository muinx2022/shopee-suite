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

    /// <summary>
    /// KiotProxy key RIÊNG cho tk BigSeller này. Có key → mọi traffic bigseller.com (cả login lẫn scrape)
    /// đi qua IP của proxy này → mỗi tk BigSeller 1 IP khác nhau → chạy SONG SONG nhiều tk không bị server
    /// đá phiên (vì không còn "nhiều token / 1 IP máy"). TRỐNG → đi IP máy như cũ (khi đó các tk chạy
    /// LẦN LƯỢT để chỉ 1 token trên IP máy 1 lúc). Xem <c>ScrapeViewModel.BigSellerGate</c>.
    /// </summary>
    public string KiotProxyKey { get; set; } = "";
    /// <summary>Vùng KiotProxy khi cấp IP mới cho key trên (random/north/central/south…). Mặc định random.</summary>
    public string Region { get; set; } = "random";
    /// <summary>Loại proxy KiotProxy: "http" hoặc "socks5". Mặc định http.</summary>
    public string ProxyType { get; set; } = "http";

    /// <summary>Tk BigSeller có cấu hình proxy riêng (KiotProxy key) hay không.</summary>
    public bool HasProxy => !string.IsNullOrWhiteSpace(KiotProxyKey);

    public List<BigSellerShop> Shops { get; set; } = [];

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Label) ? Label
        : !string.IsNullOrWhiteSpace(Email) ? Email
        : "BigSeller " + Id[..Math.Min(8, Id.Length)];

    /// <summary>Đã có file cookie hợp lệ chưa.</summary>
    public bool HasCookie => !string.IsNullOrWhiteSpace(CookieFile) && File.Exists(CookieFile);
}
