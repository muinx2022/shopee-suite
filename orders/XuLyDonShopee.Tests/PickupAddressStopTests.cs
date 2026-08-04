using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test hành vi "KHÔNG đặt được địa chỉ lấy hàng → BỎ QUA shop + cảnh báo ra kênh ngoài" (sự cố 28/07: app biết
/// chưa đặt được địa chỉ, vẫn in phiếu + giao đơn ⇒ shipper tới sai chỗ; 2026-08-04: chỉ bỏ qua shop lỗi, vẫn
/// chạy shop kế):
/// <list type="bullet">
/// <item><see cref="ShopFlowRunner.QuyetDinhSauDatDiaChi"/> — nhánh quyết định: <c>pickupOk=false</c> KHÔNG
/// bao giờ dẫn tới bước Chuẩn bị hàng (in phiếu); captcha giữ nguyên hành vi cũ.</item>
/// <item><see cref="OrderPersistPipeline.CoNenGuiCanhBao"/> — luật chặn spam 1 tin / tài khoản / 60 phút.</item>
/// <item><see cref="OrderNotifyService.TaoTinNhanLoiDiaChi"/> — nội dung tin cảnh báo (máy · tài khoản/shop ·
/// lỗi gì · app đã làm gì).</item>
/// </list>
/// </summary>
public class PickupAddressStopTests
{
    // ===================== Nhánh quyết định sau bước đặt địa chỉ =====================

    [Fact]
    public void QuyetDinh_DatDiaChiOk_KhongCaptcha_ThiXuDon()
    {
        Assert.Equal(SauDatDiaChi.XuDon, ShopFlowRunner.QuyetDinhSauDatDiaChi(pickupOk: true, captchaSeen: false));
    }

    [Fact]
    public void QuyetDinh_KhongDatDuocDiaChi_ThiDUNG_KhongInPhieu()
    {
        // Đây là chính lỗi production: trước đây nhánh này chạy tiếp prepareNextOrder → in phiếu sai địa chỉ.
        Assert.Equal(SauDatDiaChi.DungViDiaChi, ShopFlowRunner.QuyetDinhSauDatDiaChi(pickupOk: false, captchaSeen: false));
    }

    [Fact]
    public void QuyetDinh_Captcha_GiuNguyenHanhViCu()
    {
        // Captcha ưu tiên hơn (thông điệp phải là captcha, kẻo người trực xử nhầm nguyên nhân).
        Assert.Equal(SauDatDiaChi.DungViCaptcha, ShopFlowRunner.QuyetDinhSauDatDiaChi(pickupOk: true, captchaSeen: true));
        Assert.Equal(SauDatDiaChi.DungViCaptcha, ShopFlowRunner.QuyetDinhSauDatDiaChi(pickupOk: false, captchaSeen: true));
    }

    [Fact]
    public void QuyetDinh_KhongDatDuocDiaChi_TuyetDoiKhongRaXuDon()
    {
        // Ràng buộc AN TOÀN: pickupOk=false thì KHÔNG có tổ hợp nào cho ra "xử đơn" (in phiếu).
        foreach (var captcha in new[] { false, true })
        {
            Assert.NotEqual(SauDatDiaChi.XuDon, ShopFlowRunner.QuyetDinhSauDatDiaChi(pickupOk: false, captchaSeen: captcha));
        }
    }

    // ===================== Chặn spam cảnh báo =====================

    private static readonly TimeSpan Nguong = TimeSpan.FromMinutes(60);
    private static readonly DateTime Moc = new(2026, 7, 28, 10, 11, 0);

    [Fact]
    public void ChanSpam_LanDau_ThiGui()
    {
        Assert.True(OrderPersistPipeline.CoNenGuiCanhBao(mocGanNhat: null, bayGio: Moc, nguong: Nguong));
    }

    [Fact]
    public void ChanSpam_TrongNguong_ThiKhongGuiLai()
    {
        // Vòng chạy lặp lại sau ~30' — vẫn trong 60' nên KHÔNG gửi lại (caller vẫn phải LOG).
        Assert.False(OrderPersistPipeline.CoNenGuiCanhBao(Moc, Moc.AddMinutes(30), Nguong));
        Assert.False(OrderPersistPipeline.CoNenGuiCanhBao(Moc, Moc.AddMinutes(59), Nguong));
    }

    [Fact]
    public void ChanSpam_QuaNguong_ThiGuiLai()
    {
        Assert.True(OrderPersistPipeline.CoNenGuiCanhBao(Moc, Moc.AddMinutes(60), Nguong));   // đúng ngưỡng → gửi
        Assert.True(OrderPersistPipeline.CoNenGuiCanhBao(Moc, Moc.AddMinutes(61), Nguong));
    }

    [Fact]
    public void ChanSpam_NguongMacDinh_La60Phut()
    {
        Assert.Equal(TimeSpan.FromMinutes(60), OrderPersistPipeline.NguongCanhBaoDiaChi);
    }

    // ===================== Nội dung tin cảnh báo =====================

    [Fact]
    public void TinCanhBao_CoDuMay_TaiKhoan_Shop_Tinh_ThoiDiem()
    {
        var text = OrderNotifyService.TaoTinNhanLoiDiaChi(
            "hoangdh200392", "deilca.store", "Thanh Hóa", "muinx-nuc", new DateTime(2026, 7, 28, 10, 11, 0));

        Assert.Contains("KHÔNG ĐẶT ĐƯỢC ĐỊA CHỈ LẤY HÀNG", text);
        Assert.Contains("đã bỏ qua shop này (chưa in phiếu), vẫn chạy shop khác", text);
        Assert.DoesNotContain("đã dừng vòng", text);
        Assert.Contains("Máy: muinx-nuc", text);
        Assert.Contains("Tài khoản: hoangdh200392", text);
        Assert.Contains("Shop: deilca.store", text);
        Assert.Contains("Thanh Hóa", text);
        Assert.Contains("28/07/2026 10:11", text);
        Assert.Contains("Sửa Địa chỉ", text);        // việc cần làm
    }

    [Fact]
    public void TinCanhBao_KhacHanTinDonMoi_DeKhongXuNham()
    {
        var loi = OrderNotifyService.TaoTinNhanLoiDiaChi("acc", "shop", "Thanh Hóa", "may1", DateTime.Now);
        Assert.StartsWith("⛔", loi);
        Assert.DoesNotContain("đơn MỚI", loi);
    }

    [Fact]
    public void TinCanhBao_ThamSoRong_KhongNem_KhongInNull()
    {
        var text = OrderNotifyService.TaoTinNhanLoiDiaChi("", "  ", "", "", new DateTime(2026, 7, 28, 10, 11, 0));

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.DoesNotContain("null", text);
        Assert.Contains("Máy: ? · Tài khoản: ? · Shop: ?", text);   // chỗ trống hiện "?" cho dễ đọc
    }

    [Fact]
    public void TinCanhBao_XuongDongThuong_HopVoiCaBaKenh()
    {
        var text = OrderNotifyService.TaoTinNhanLoiDiaChi("acc", "shop", "Thanh Hóa", "may1", DateTime.Now);

        Assert.Equal(4, text.Split('\n').Length);   // 4 dòng, cắt tin theo '\n' (ChiaTinTheoGioiHan) vẫn gọn
        Assert.DoesNotContain("\r", text);
    }
}
