namespace XuLyDonShopee.Core.Models;

/// <summary>Trạng thái của một tài khoản Shopee.</summary>
public enum AccountStatus
{
    /// <summary>Chưa kiểm tra.</summary>
    ChuaKiemTra = 0,

    /// <summary>Đang hoạt động bình thường.</summary>
    HoatDong = 1,

    /// <summary>Bị khóa.</summary>
    BiKhoa = 2
}

/// <summary>Loại proxy.</summary>
public enum ProxyType
{
    Http = 0,
    Socks5 = 1
}

/// <summary>Trạng thái sống/chết của proxy.</summary>
public enum ProxyStatus
{
    /// <summary>Chưa kiểm tra.</summary>
    ChuaKiemTra = 0,

    /// <summary>Proxy còn sống.</summary>
    Song = 1,

    /// <summary>Proxy đã chết.</summary>
    Chet = 2
}
