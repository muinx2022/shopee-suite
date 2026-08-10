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
        // MẪU QUÁ NHỎ (2 dòng, dưới SanSoDongNghiSaiTab): tab đúng vẫn lẫn dòng lạ, mà 1/2 = 50% chẳng nói lên
        // điều gì. Chỉ cần MỘT dòng mang mã yêu cầu là không được bỏ lượt.
        var dong = new[] { DonHuy("260805AAAA"), DonTraHang("260805BBBB") };
        Assert.False(TraHangParser.NghiSaiTabTheoDuLieu(dong));
    }

    [Fact]
    public void DanhSachRong_ThiKhongNghi()
    {
        // Shop thật sự không có yêu cầu nào → phải cho ghi mốc 0 bình thường, KHÔNG bỏ lượt.
        Assert.False(TraHangParser.NghiSaiTabTheoDuLieu(System.Array.Empty<DongTraHang>()));
    }

    /// <summary>Trộn <paramref name="soHuy"/> dòng đơn hủy với <paramref name="soTra"/> dòng trả hàng thật.</summary>
    private static DongTraHang[] Tron(int soHuy, int soTra)
    {
        var ds = new List<DongTraHang>(soHuy + soTra);
        for (var i = 0; i < soHuy; i++) { ds.Add(DonHuy($"2608H{i:D4}")); }
        for (var i = 0; i < soTra; i++) { ds.Add(DonTraHang($"2608T{i:D4}")); }
        return ds.ToArray();
    }

    [Fact]
    public void PhanLonLaDonHuy_ThiVanNghiSaiTab()
    {
        // CA THẬT 10/08/2026 (deilca.store): 40 dòng, 35 đơn hủy + 5 trả hàng, ô tổng báo 148 trong khi mốc thật
        // của shop là 33. Luật "100% mới nổ" LỌT ĐÚNG CA NÀY — số rác 148 sẽ được ghi vào mốc rồi nuốt vĩnh viễn
        // mọi yêu cầu trả hàng mới. 35/40 = 87,5% ≥ NguongTyLeDonHuyNghiSaiTab ⇒ phải nghi.
        Assert.True(TraHangParser.NghiSaiTabTheoDuLieu(Tron(soHuy: 35, soTra: 5)));
    }

    [Fact]
    public void ToanBoBonMuoiDongDeuLaDonHuy_ThiNghiSaiTab()
    {
        // Ca 100% với mẫu lớn — vế cũ vẫn phải giữ nguyên hiệu lực sau khi thêm vế tỷ lệ.
        Assert.True(TraHangParser.NghiSaiTabTheoDuLieu(Tron(soHuy: 40, soTra: 0)));
    }

    [Fact]
    public void DuMauNhungTyLeHuyDuoiNguong_ThiKhongNghi()
    {
        // 10 dòng, 7 hủy = 70% < 80%: mẫu đủ rộng nhưng chưa đủ gắt — không được bỏ lượt của shop lành.
        Assert.False(TraHangParser.NghiSaiTabTheoDuLieu(Tron(soHuy: 7, soTra: 3)));
    }

    [Fact]
    public void ChuaDuSanSoDong_ThiKhongApLuatTyLe()
    {
        // Ngay SÁT sàn (9 dòng, 8 hủy = 88,9%) vẫn không nổ: sàn là mốc, không phải gợi ý.
        Assert.False(TraHangParser.NghiSaiTabTheoDuLieu(Tron(soHuy: 8, soTra: 1)));
        // Đúng sàn (10 dòng, 8 hủy = 80%) thì nổ.
        Assert.True(TraHangParser.NghiSaiTabTheoDuLieu(Tron(soHuy: 8, soTra: 2)));
    }
}
