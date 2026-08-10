using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// MỘT SHOP HỎNG KHÔNG ĐƯỢC GIẾT CẢ VÒNG — bảng quyết định
/// <see cref="OrdersBridgeSession.QuyetDinhSauShopHong"/>.
/// <para>
/// Bối cảnh (10/08/2026): cầu nối rớt giữa chặng "Chuẩn bị hàng" ở shop 6/12, ngoại lệ xuyên thẳng ra khối catch
/// cuối <c>RunAllShopsAsync</c> ⇒ 6 shop còn lại không hề được mở. Vòng duyệt 12 shop nghỉ 3–5' mỗi shop nên mất
/// 6 shop là mất gần một giờ và phải chờ vòng sau.
/// </para>
/// <para>Bảng này giữ đúng HAI đường vẫn phải dừng: chạm trần hỏng liên tiếp, và không quay lại được trang chọn
/// shop khi còn shop phía sau (shop kế chắc chắn không mở nổi).</para>
/// </summary>
public class ShopHongKhongGietCaVongTests
{
    [Fact]
    public void HongMotShop_ConShopPhiaSau_VeDuocPicker_ThiChayTiep()
    {
        // Ca chính của bản vá: shop 6 hỏng, shop 7..12 vẫn phải được chạy.
        Assert.Equal(SauShopHong.ChayTiepShopKe,
            OrdersBridgeSession.QuyetDinhSauShopHong(1, conShopPhiaSau: true, veDuocPicker: true));
    }

    [Fact]
    public void HongDuoiTran_ThiVanChayTiep()
    {
        // Ngay SÁT trần vẫn phải chạy tiếp — trần là mốc dừng, không phải mốc cảnh báo.
        Assert.Equal(SauShopHong.ChayTiepShopKe,
            OrdersBridgeSession.QuyetDinhSauShopHong(
                OrdersBridgeSession.TranShopHongLienTiep - 1, conShopPhiaSau: true, veDuocPicker: true));
    }

    [Fact]
    public void HongLienTiep_ChamTran_ThiDungVong()
    {
        Assert.Equal(SauShopHong.DungVongHongLienTiep,
            OrdersBridgeSession.QuyetDinhSauShopHong(
                OrdersBridgeSession.TranShopHongLienTiep, conShopPhiaSau: true, veDuocPicker: true));
    }

    [Fact]
    public void VuotTran_ThiVanDungVong()
    {
        Assert.Equal(SauShopHong.DungVongHongLienTiep,
            OrdersBridgeSession.QuyetDinhSauShopHong(
                OrdersBridgeSession.TranShopHongLienTiep + 5, conShopPhiaSau: true, veDuocPicker: true));
    }

    [Fact]
    public void KhongVeDuocPicker_ConShopPhiaSau_ThiDungVong()
    {
        // Luật CŨ giữ nguyên: không về được trang chọn shop thì shop kế chắc chắn chết.
        Assert.Equal(SauShopHong.DungVongKhongVePicker,
            OrdersBridgeSession.QuyetDinhSauShopHong(1, conShopPhiaSau: true, veDuocPicker: false));
    }

    [Fact]
    public void KhongVeDuocPicker_NhungLaSHOPCUOI_ThiKhongDungVong()
    {
        // Luật CŨ giữ nguyên: picker hỏng sau shop cuối không hại ai — vòng đằng nào cũng kết thúc.
        Assert.Equal(SauShopHong.ChayTiepShopKe,
            OrdersBridgeSession.QuyetDinhSauShopHong(1, conShopPhiaSau: false, veDuocPicker: false));
    }

    [Fact]
    public void ChamTranVaKhongVePicker_ThiLyDoLaHONGLIENTIEP()
    {
        // Hai lý do cùng đúng thì "cầu nối chết" mới là lý do THẬT; "không về được picker" chỉ là hệ quả.
        // Nhật ký báo sai lý do là lần sau đi sửa nhầm chỗ.
        Assert.Equal(SauShopHong.DungVongHongLienTiep,
            OrdersBridgeSession.QuyetDinhSauShopHong(
                OrdersBridgeSession.TranShopHongLienTiep, conShopPhiaSau: true, veDuocPicker: false));
    }
}
