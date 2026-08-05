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
