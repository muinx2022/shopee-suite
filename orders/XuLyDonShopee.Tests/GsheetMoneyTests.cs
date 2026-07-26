using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test <see cref="GsheetMoney.Chon"/>: số tiền đẩy lên cột tiền Google Sheet. Có ước tính → ước tính; chưa có
/// mà đơn THƯỜNG → null (để ô TRỐNG chờ lượt sau điền, vì script "chỉ ghi ô đang trống" không đè được số đã ghi);
/// chưa có mà đơn HỦY → tổng tiền (đơn hủy không bao giờ có ước tính nên không có gì để đè sau này).
/// </summary>
public class GsheetMoneyTests
{
    [Fact]
    public void CoUocTinh_TraUocTinh_KhongPhaiTongTien()
    {
        Assert.Equal(160000, GsheetMoney.Chon(finalAmount: 160000, totalPrice: 166500, daHuy: false));
    }

    [Fact]
    public void CoUocTinh_DonHuy_VanTraUocTinh()
    {
        // Ước tính luôn thắng, kể cả đơn hủy (hiếm — nhưng đã có số thì dùng số đúng).
        Assert.Equal(160000, GsheetMoney.Chon(finalAmount: 160000, totalPrice: 166500, daHuy: true));
    }

    [Fact]
    public void ChuaCoUocTinh_DonThuong_TraNull_DeOTrong()
    {
        // KHÔNG ghi tạm tổng tiền: ô có số rồi thì lượt đẩy lại (khi ước tính về) không đè được → kẹt số sai.
        Assert.Null(GsheetMoney.Chon(finalAmount: null, totalPrice: 166500, daHuy: false));
    }

    [Fact]
    public void ChuaCoUocTinh_DonHuy_TraTongTien()
    {
        // Đơn hủy KHÔNG BAO GIỜ có ước tính → để trống là mất số vĩnh viễn; ghi tổng tiền (giữ hành vi cũ).
        Assert.Equal(166500, GsheetMoney.Chon(finalAmount: null, totalPrice: 166500, daHuy: true));
    }

    [Fact]
    public void ChuaCoUocTinh_DonHuy_TongTienCungNull_TraNull()
    {
        Assert.Null(GsheetMoney.Chon(finalAmount: null, totalPrice: null, daHuy: true));
    }
}
