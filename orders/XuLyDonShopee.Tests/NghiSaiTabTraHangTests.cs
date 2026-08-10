using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Luật CHỐT THEO DỮ LIỆU chống đọc nhầm tab ở bước check đơn trả hàng
/// (<see cref="TraHangParser.NghiSaiTabTheoDuLieu"/>).
/// <para>
/// Ca thật 10/08/2026: extension báo cờ "đang ở tab Đơn Trả hàng/Hoàn tiền" nhưng 33/33 dòng đọc được đều là
/// ĐƠN HỦY ⇒ nó đang đứng ở tab khác, mà số 33 vẫn bị ghi vào mốc ⇒ nuốt vĩnh viễn mọi yêu cầu trả hàng mới
/// của shop đó. Luật này soi CHÍNH DỮ LIỆU nên không gãy khi Shopee đổi markup tab lần nữa.
/// </para>
/// </summary>
public class NghiSaiTabTraHangTests
{
    /// <summary>Dòng mà href nói rõ là ĐƠN HỦY (<c>LaTraHang = false</c>) — <c>GhepCap</c> bỏ và đếm vào BoQuaDonHuy.</summary>
    private static DongTraHang DonHuy(string id) => new(id, "<div></div>", LaTraHang: false);

    /// <summary>Dòng trả hàng thật (<c>LaTraHang = true</c>).</summary>
    private static DongTraHang DonTraHang(string id) => new(id, "<div></div>", LaTraHang: true);

    [Fact]
    public void MoiDongDeuLaDonHuy_ThiNghiSaiTab()
    {
        var dong = new[] { DonHuy("260805AAAA"), DonHuy("260805BBBB"), DonHuy("260805CCCC") };
        Assert.True(TraHangParser.NghiSaiTabTheoDuLieu(dong));
    }

    [Fact]
    public void CoDuMotDongTraHangThat_ThiKhongNghi()
    {
        // Tab đúng vẫn có thể lẫn dòng lạ — chỉ cần MỘT dòng mang mã yêu cầu là không được bỏ lượt.
        var dong = new[] { DonHuy("260805AAAA"), DonTraHang("260805BBBB") };
        Assert.False(TraHangParser.NghiSaiTabTheoDuLieu(dong));
    }

    [Fact]
    public void DanhSachRong_ThiKhongNghi()
    {
        // Shop thật sự không có yêu cầu nào → phải cho ghi mốc 0 bình thường, KHÔNG bỏ lượt.
        Assert.False(TraHangParser.NghiSaiTabTheoDuLieu(System.Array.Empty<DongTraHang>()));
    }
}
