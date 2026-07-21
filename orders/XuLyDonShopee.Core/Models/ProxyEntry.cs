namespace XuLyDonShopee.Core.Models;

/// <summary>
/// Một proxy trong danh sách xoay vòng.
/// </summary>
public class ProxyEntry
{
    public long Id { get; set; }

    /// <summary>Địa chỉ host/IP.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Cổng.</summary>
    public int Port { get; set; }

    /// <summary>Tên đăng nhập proxy (tùy chọn).</summary>
    public string? Username { get; set; }

    /// <summary>Mật khẩu proxy (tùy chọn).</summary>
    public string? Password { get; set; }

    /// <summary>Loại proxy (mặc định Http).</summary>
    public ProxyType Type { get; set; } = ProxyType.Http;

    /// <summary>Trạng thái sống/chết.</summary>
    public ProxyStatus Status { get; set; } = ProxyStatus.ChuaKiemTra;

    /// <summary>Thời điểm thêm (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Chuỗi hiển thị host:port dùng cho log/UI.</summary>
    public override string ToString()
        => Username is { Length: > 0 }
            ? $"{Host}:{Port}:{Username}:{Password}"
            : $"{Host}:{Port}";
}
