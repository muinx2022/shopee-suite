using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Chốt luật <b>BỎ LƯỢT khi không chắc đang ở tab "Đơn Trả hàng Hoàn tiền"</b>
/// (<see cref="OrdersBridgeSession.QuyetDinhLuotTraHang"/>).
/// <para>
/// Sự cố: extension đặt <c>tabTraHang=true</c> mù sau cú click, C# chỉ log cảnh báo rồi VẪN chốt mốc bằng con số
/// của tab "Tất cả" (gộp Đơn Hủy / Giao không thành công) — lớn hơn hẳn số thật. Vòng sau chọn được tab, số thật
/// NHỎ hơn mốc rác ⇒ rơi nhánh <see cref="LuatSoYeuCau.Giam"/> "chỉ cập nhật mốc" ⇒ mọi yêu cầu phát sinh giữa
/// chừng KHÔNG bao giờ được đọc, không vào <c>return_codes</c>, không lên Google Sheet.
/// </para>
/// </summary>
public class TraHangBoLuotSaiTabTests
{
    private static KetQuaDocTraHang Doc(int? soYeuCau, bool tabTraHang)
        => new(soYeuCau, SortApplied: true, TabTraHang: tabTraHang, Array.Empty<DongTraHang>());

    [Fact]
    public void DungTab_CoSo_ThiXuLy()
    {
        var luot = OrdersBridgeSession.QuyetDinhLuotTraHang(Doc(12, tabTraHang: true));

        Assert.Equal(SauDocTraHang.XuLy, luot.Nhanh);
        Assert.Equal(12, luot.SoMoi);
    }

    /// <summary>Ca của bug: đọc được số nhưng KHÔNG chắc đúng tab → BỎ LƯỢT (caller return TRƯỚC
    /// <c>QuyetDinhCheck</c> + <c>saveReturnCount</c> nên mốc giữ nguyên).</summary>
    [Fact]
    public void SaiTab_DuCoSo_ThiBoLuot()
    {
        var luot = OrdersBridgeSession.QuyetDinhLuotTraHang(Doc(141, tabTraHang: false));

        Assert.Equal(SauDocTraHang.BoLuotSaiTab, luot.Nhanh);
        Assert.Equal(141, luot.SoMoi); // vẫn mang số ra để LOG cho biết đã đọc nhầm số nào
    }

    [Fact]
    public void KhongDocDuocSo_ThiBoLuot_DuBaoDungTab()
    {
        var luot = OrdersBridgeSession.QuyetDinhLuotTraHang(Doc(null, tabTraHang: true));

        Assert.Equal(SauDocTraHang.BoLuotKhongCoSo, luot.Nhanh);
        Assert.Equal(0, luot.SoMoi);
    }

    /// <summary>Extension đời CŨ không gửi <c>tabTraHang</c> → <see cref="TraHangParser.ParseKetQua"/> cho false
    /// ⇒ bỏ lượt. Phía an toàn: thà chậm một vòng còn hơn chốt mốc bằng số của tab khác.</summary>
    [Fact]
    public void ExtensionDoiCu_ThieuFieldTab_ThiBoLuot()
    {
        var doc = TraHangParser.ParseKetQua("{\"soYeuCauText\":\"141 Yêu cầu\",\"sortApplied\":true,\"list\":[]}");

        Assert.Equal(SauDocTraHang.BoLuotSaiTab, OrdersBridgeSession.QuyetDinhLuotTraHang(doc).Nhanh);
    }

    /// <summary>
    /// Vì sao bỏ lượt chứ không "cứ ghi mốc rồi tính sau": diễn lại đúng ba vòng của sự cố. Mốc 10 → vòng lỗi tab
    /// đọc 141 → vòng sau đọc đúng 12. Nếu chốt mốc 141 thì vòng cuối là GIẢM (0 dòng đọc, mã mới mất hẳn); giữ
    /// mốc 10 thì vòng cuối là TĂNG 2 — đọc đúng 2 yêu cầu mới.
    /// </summary>
    [Fact]
    public void GiuMocKhiSaiTab_VongSauVanDocDuocYeuCauMoi()
    {
        var docLoi = Doc(141, tabTraHang: false);
        Assert.Equal(SauDocTraHang.BoLuotSaiTab, OrdersBridgeSession.QuyetDinhLuotTraHang(docLoi).Nhanh);

        // Mốc GIỮ NGUYÊN 10 (lượt lỗi không gọi saveReturnCount) → vòng sau đọc 12 là TĂNG 2.
        Assert.Equal(new QuyetDinhTraHang(LuatSoYeuCau.Tang, 2), TraHangParser.QuyetDinhCheck(10, 12));

        // Đối chứng — hành vi CŨ (chốt mốc 141): vòng sau thành "Giảm" ⇒ 0 dòng đọc, mã mới bị nuốt vĩnh viễn.
        Assert.Equal(new QuyetDinhTraHang(LuatSoYeuCau.Giam, 0), TraHangParser.QuyetDinhCheck(141, 12));
    }
}
