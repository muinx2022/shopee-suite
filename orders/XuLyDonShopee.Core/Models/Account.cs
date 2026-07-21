namespace XuLyDonShopee.Core.Models;

/// <summary>
/// Một tài khoản Shopee được lưu trong hệ thống.
/// </summary>
public class Account
{
    public long Id { get; set; }

    /// <summary>User đăng nhập (email hoặc tên đăng nhập tùy ý, bắt buộc, không trùng).</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Mật khẩu đăng nhập (bắt buộc). Bước đầu lưu dạng thường.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Số điện thoại (tùy chọn).</summary>
    public string? Phone { get; set; }

    /// <summary>Cookie đăng nhập Shopee (tùy chọn).</summary>
    public string? Cookie { get; set; }

    /// <summary>Ghi chú (tùy chọn).</summary>
    public string? Note { get; set; }

    /// <summary>API key KiotProxy riêng của tài khoản (tùy chọn). Có → mở trang bán hàng dùng proxy sticky theo key này.</summary>
    public string? ProxyKey { get; set; }

    /// <summary>Địa chỉ lấy hàng mặc định (tỉnh/thành, chọn từ danh sách cố định trên form). Null = chưa chọn → app coi như Thanh Hóa.</summary>
    public string? PickupAddress { get; set; }

    /// <summary>Trạng thái tài khoản.</summary>
    public AccountStatus Status { get; set; } = AccountStatus.ChuaKiemTra;

    /// <summary>Thời điểm tạo (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Thời điểm sửa gần nhất (UTC).</summary>
    public DateTime UpdatedAt { get; set; }
}
