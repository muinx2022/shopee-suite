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
    /// KiotProxy key RIÊNG cho tk BigSeller này — dùng cho LUỒNG ĐĂNG NHẬP BigSeller (token lưu ra khớp IP
    /// proxy, tránh bị nghi phiên lạ). TRỐNG → đăng nhập bằng IP máy. LƯU Ý: lúc SCRAPE, BigSeller KHÔNG đi
    /// proxy riêng này nữa mà đi qua proxy của instance Shopee (mỗi instance 1 IP → phiên rải nhiều IP →
    /// chạy SONG SONG không bị "nhiều token / 1 IP"); xem <c>BraveProfileManager</c> và
    /// <c>BraveInstanceSession.ResolveBigSellerProxyServerAsync</c>.
    /// </summary>
    public string KiotProxyKey { get; set; } = "";
    /// <summary>Vùng KiotProxy khi cấp IP mới cho key trên (random/north/central/south…). Mặc định random.</summary>
    public string Region { get; set; } = "random";
    /// <summary>Loại proxy KiotProxy: "http" hoặc "socks5". Mặc định http.</summary>
    public string ProxyType { get; set; } = "http";

    /// <summary>Tk BigSeller có cấu hình proxy riêng (KiotProxy key) hay không.</summary>
    public bool HasProxy => !string.IsNullOrWhiteSpace(KiotProxyKey);

    public List<BigSellerShop> Shops { get; set; } = [];

    // Lựa chọn ở module Update/Import — LƯU để khôi phục sau khi mở lại app (trước đây chỉ là UI-state).
    /// <summary>Tk này có được TICK chọn để chạy Update/Import không.</summary>
    public bool UpdateRunSelected { get; set; }
    /// <summary>Id shop đang chọn ở module Update/Import (panel cấu hình chi tiết). Trống = chưa chọn.</summary>
    public string UpdateSelectedShopId { get; set; } = "";

    /// <summary>RIÊNG-MÁY (ngoài chữ ký dùng-chung): acc này ĐẾN TỪ HUB. Chỉ acc HubOwned mới bị mirror-xóa
    /// khi Hub bỏ acc; acc tạo/đăng nhập TẠI CHỖ = false → KHÔNG bị xóa khi client đồng bộ.</summary>
    public bool HubOwned { get; set; }

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Label) ? Label
        : !string.IsNullOrWhiteSpace(Email) ? Email
        : "BigSeller " + Id[..Math.Min(8, Id.Length)];

    /// <summary>Đã có file cookie hợp lệ chưa.</summary>
    public bool HasCookie => !string.IsNullOrWhiteSpace(CookieFile) && File.Exists(CookieFile);
}
