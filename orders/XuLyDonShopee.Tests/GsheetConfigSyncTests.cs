using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test luật đồng bộ cấu hình Google Sheet Hub ↔ client (<see cref="GsheetConfigSync"/>). Trọng tâm là các
/// BẤT BIẾN "làm sai là hỏng cả fleet":
/// <list type="number">
/// <item>Hub RỖNG (chưa ai điền URL) thì TUYỆT ĐỐI không đè client — URL trống là công tắc TẮT ghi sheet, đè
/// xuống là cả fleet âm thầm ngừng ghi Google Sheet.</item>
/// <item>Hub CÓ URL ⇒ áp CẢ url LẪN tab (tab rỗng lúc đó nghĩa là "tự động theo tháng", không phải "chưa đặt").</item>
/// <item>Client RỖNG thì không được đẩy đè hub.</item>
/// </list>
/// Hai chiều ĐỐI XỨNG: chiều đẩy (<see cref="GsheetConfigSync.QuyetDinhNhanBanClient"/>) cũng coi khối GSheet là
/// một đơn vị — client có URL thì hub nhận CẢ tab rỗng (= "tự động theo tháng"), nhờ vậy xoá được override từ
/// client mà không bị hub đẩy ngược bản cũ về.
/// </summary>
public class GsheetConfigSyncTests
{
    private const string UrlHub = "https://script.google.com/macros/s/HUB/exec";
    private const string UrlLocal = "https://script.google.com/macros/s/LOCAL/exec";

    // ── BẤT BIẾN #1: hub rỗng KHÔNG đè client ──

    [Fact]
    public void ApBanHub_HubTrong_ClientDaCoUrl_KhongDe()
    {
        var q = GsheetConfigSync.QuyetDinhApBanHub(null, null, UrlLocal, "Tháng 07-2026");
        Assert.False(q.Ap);
    }

    [Fact]
    public void ApBanHub_HubUrlToanKhoangTrang_KhongDe()
    {
        var q = GsheetConfigSync.QuyetDinhApBanHub("   ", "Tab hub", UrlLocal, "");
        Assert.False(q.Ap);
    }

    [Fact]
    public void ApBanHub_HubChiCoTabKhongCoUrl_KhongDe()
    {
        // Hub có tab nhưng KHÔNG có URL = chưa cấu hình → không được áp một nửa (kẻo đổi tab của cả fleet).
        var q = GsheetConfigSync.QuyetDinhApBanHub("", "Tab la", UrlLocal, "Tab cua may");
        Assert.False(q.Ap);
    }

    [Fact]
    public void ApBanHub_HubTrong_ClientCungTrong_KhongDe()
    {
        var q = GsheetConfigSync.QuyetDinhApBanHub(null, null, null, null);
        Assert.False(q.Ap);
    }

    // ── BẤT BIẾN #2: hub có URL ⇒ áp CẢ HAI field ──

    [Fact]
    public void ApBanHub_HubCoUrl_ClientChuaCau_ApCaUrlLanTab()
    {
        var q = GsheetConfigSync.QuyetDinhApBanHub(UrlHub, "Tháng 07-2026", null, "");
        Assert.True(q.Ap);
        Assert.Equal(UrlHub, q.Url);
        Assert.Equal("Tháng 07-2026", q.Tab);
    }

    [Fact]
    public void ApBanHub_HubCoUrlTabTrong_XoaTabOverrideCuaClient()
    {
        // Tab rỗng của hub (khi hub CÓ url) = "tự động theo tháng" → phải XOÁ override cũ của client.
        var q = GsheetConfigSync.QuyetDinhApBanHub(UrlHub, "", UrlHub, "Tab cu");
        Assert.True(q.Ap);
        Assert.Equal(UrlHub, q.Url);
        Assert.Equal("", q.Tab);
    }

    [Fact]
    public void ApBanHub_HubCoUrl_ClientCoUrlKhac_HubThang()
    {
        var q = GsheetConfigSync.QuyetDinhApBanHub(UrlHub, "", UrlLocal, "");
        Assert.True(q.Ap);
        Assert.Equal(UrlHub, q.Url);
    }

    [Fact]
    public void ApBanHub_TrungBanLocal_KhongGhiLai()
    {
        // Trùng → Ap=false để nhịp poll không ghi SQLite mỗi phút vô ích.
        var q = GsheetConfigSync.QuyetDinhApBanHub(UrlHub, "Tab A", UrlHub, "Tab A");
        Assert.False(q.Ap);
    }

    [Fact]
    public void ApBanHub_ChiKhacKhoangTrangThua_KhongGhiLai()
    {
        var q = GsheetConfigSync.QuyetDinhApBanHub("  " + UrlHub + " ", " Tab A ", UrlHub, "Tab A");
        Assert.False(q.Ap);
    }

    [Fact]
    public void ApBanHub_ChiKhacTab_VanAp()
    {
        var q = GsheetConfigSync.QuyetDinhApBanHub(UrlHub, "Tab moi", UrlHub, "Tab cu");
        Assert.True(q.Ap);
        Assert.Equal("Tab moi", q.Tab);
    }

    [Fact]
    public void ApBanHub_TrimTruocKhiGhi()
    {
        var q = GsheetConfigSync.QuyetDinhApBanHub("  " + UrlHub + "  ", "  Tab A  ", null, null);
        Assert.True(q.Ap);
        Assert.Equal(UrlHub, q.Url);
        Assert.Equal("Tab A", q.Tab);
    }

    // ── BẤT BIẾN #3: client rỗng KHÔNG đẩy đè hub ──

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NenDayLenHub_UrlTrong_KhongDay(string? url)
    {
        Assert.False(GsheetConfigSync.NenDayLenHub(url));
    }

    [Fact]
    public void NenDayLenHub_CoUrl_Day()
    {
        Assert.True(GsheetConfigSync.NenDayLenHub(UrlLocal));
    }

    // ── Chiều ĐẨY: hub nhận bản client (đối xứng chiều kéo — khối GSheet là MỘT đơn vị) ──

    [Fact]
    public void NhanBanClient_ClientCoUrl_TabRong_XOA_TabCuaHub()
    {
        // Ca chính của lần sửa này: user xoá ô Tab ở client để về "tự động theo tháng" → hub PHẢI xoá override,
        // nếu không hub sẽ đẩy ngược tab cũ về và người dùng không bao giờ xoá được override từ client.
        var q = GsheetConfigSync.QuyetDinhNhanBanClient(UrlLocal, "", UrlLocal, "Tab cu");
        Assert.True(q.Ap);
        Assert.Equal(UrlLocal, q.Url);
        Assert.Equal("", q.Tab);
    }

    [Fact]
    public void NhanBanClient_ClientUrlRong_TabCoGiaTri_KhongDoiGiCa()
    {
        // Máy chưa cấu hình (URL trống) thì dù có tab cũng KHÔNG được nhận một nửa khối.
        var q = GsheetConfigSync.QuyetDinhNhanBanClient("", "Tab la", UrlHub, "Tab hub");
        Assert.False(q.Ap);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NhanBanClient_ClientUrlRong_GiuNguyenBanHub(string? clientUrl)
    {
        var q = GsheetConfigSync.QuyetDinhNhanBanClient(clientUrl, null, UrlHub, "Tab hub");
        Assert.False(q.Ap);
    }

    [Fact]
    public void NhanBanClient_HubChuaCoGi_ClientCoUrl_Ghi()
    {
        var q = GsheetConfigSync.QuyetDinhNhanBanClient(UrlLocal, "Tab A", null, null);
        Assert.True(q.Ap);
        Assert.Equal(UrlLocal, q.Url);
        Assert.Equal("Tab A", q.Tab);
    }

    [Fact]
    public void NhanBanClient_TrungBanHub_KhongGhi()
    {
        // Trùng → không Save → không bump version → cả fleet khỏi re-pull vô ích.
        var q = GsheetConfigSync.QuyetDinhNhanBanClient(UrlHub, "Tab A", UrlHub, "Tab A");
        Assert.False(q.Ap);
    }

    [Fact]
    public void NhanBanClient_CaHaiCungTabRong_KhongGhi()
    {
        var q = GsheetConfigSync.QuyetDinhNhanBanClient(UrlHub, "", UrlHub, null);
        Assert.False(q.Ap);
    }

    [Fact]
    public void NhanBanClient_DoiUrl_Ghi()
    {
        var q = GsheetConfigSync.QuyetDinhNhanBanClient(UrlLocal, "", UrlHub, "");
        Assert.True(q.Ap);
        Assert.Equal(UrlLocal, q.Url);
    }

    [Fact]
    public void NhanBanClient_TrimTruocKhiGhi()
    {
        var q = GsheetConfigSync.QuyetDinhNhanBanClient("  " + UrlLocal + " ", "  Tab A  ", UrlHub, "");
        Assert.True(q.Ap);
        Assert.Equal(UrlLocal, q.Url);
        Assert.Equal("Tab A", q.Tab);
    }

    [Fact]
    public void HaiChieu_DoiXung_XoaTabOClientRoiKeoVe_KhongBiRevert()
    {
        // Kịch bản end-to-end của lỗi UX đã sửa: client xoá tab → hub nhận (tab rỗng) → client kéo về thì
        // KHÔNG còn bị đẩy ngược tab cũ.
        var nhan = GsheetConfigSync.QuyetDinhNhanBanClient(UrlHub, "", UrlHub, "Tab cu");
        Assert.True(nhan.Ap);
        Assert.Equal("", nhan.Tab);

        // Hub giờ giữ (UrlHub, ""). Client kéo về, bản local cũng đã là (UrlHub, "") → không ghi lại.
        var ap = GsheetConfigSync.QuyetDinhApBanHub(nhan.Url, nhan.Tab, UrlHub, "");
        Assert.False(ap.Ap);

        // Máy thứ 2 (còn tab cũ) kéo về → bị xoá override theo hub. Đúng ý "hub thắng".
        var ap2 = GsheetConfigSync.QuyetDinhApBanHub(nhan.Url, nhan.Tab, UrlHub, "Tab cu");
        Assert.True(ap2.Ap);
        Assert.Equal("", ap2.Tab);
    }

    // ── Validate URL (dùng CHUNG cho client + trang cấu hình trên Hub) ──

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void KiemTraUrl_Trong_HopLe(string? url)
    {
        Assert.Null(GsheetConfigSync.KiemTraUrl(url)); // trống = tắt đồng bộ, KHÔNG phải lỗi
    }

    [Fact]
    public void KiemTraUrl_DungTienTo_HopLe()
    {
        Assert.Null(GsheetConfigSync.KiemTraUrl("  " + UrlHub + "  "));
    }

    [Fact]
    public void KiemTraUrl_KhongPhanBietHoaThuong()
    {
        Assert.Null(GsheetConfigSync.KiemTraUrl("HTTPS://SCRIPT.GOOGLE.COM/macros/s/X/exec"));
    }

    [Fact]
    public void KiemTraUrl_SaiTienTo_BaoLoi()
    {
        var loi = GsheetConfigSync.KiemTraUrl("https://docs.google.com/spreadsheets/d/X/edit");
        Assert.NotNull(loi);
        Assert.Contains("script.google.com", loi);
    }

    // ── Ô "Google Sheet thứ hai": validate RIÊNG + bóc ID (KHÔNG dùng chung validator Web App) ──

    private const string IdSheet2 = "1CK-mu-rtLw0QnGDZ2cuEIkRelEnZkNWuB7Ir_ZuRLhk";
    private const string UrlSheet2 = "https://docs.google.com/spreadsheets/d/" + IdSheet2 + "/edit";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void KiemTraSheet2_Trong_HopLe(string? s)
    {
        Assert.Null(GsheetConfigSync.KiemTraSheet2(s)); // trống = TẮT ghi file phụ, KHÔNG phải lỗi
    }

    [Theory]
    [InlineData(UrlSheet2)]
    [InlineData(UrlSheet2 + "?usp=sharing")]
    [InlineData(UrlSheet2 + "#gid=0")]
    [InlineData("  " + UrlSheet2 + "  ")]
    [InlineData(IdSheet2)]                       // ID trần
    public void KiemTraSheet2_LinkBangTinhHoacIdTran_HopLe(string s)
    {
        Assert.Null(GsheetConfigSync.KiemTraSheet2(s));
    }

    [Theory]
    [InlineData("rác rưởi")]
    [InlineData("ABC")]                          // ID quá ngắn (gõ nhầm vài ký tự)
    [InlineData("https://example.com/gì-đó")]
    public void KiemTraSheet2_LinkRac_BaoLoi(string s)
    {
        Assert.NotNull(GsheetConfigSync.KiemTraSheet2(s));
    }

    [Fact]
    public void KiemTraSheet2_UrlWebApp_BaoLoi()
    {
        // Dán nhầm URL Web App vào ô file phụ → phải báo lỗi (hai ô KHÁC loại giá trị).
        Assert.NotNull(GsheetConfigSync.KiemTraSheet2(UrlHub));
    }

    [Fact]
    public void KiemTraSheet2_KHONG_DungChungValidatorWebApp()
    {
        // BẪY đã ghi trong plan: link bảng tính là HỢP LỆ ở ô này, nhưng KiemTraUrl (ép tiền tố script.google.com)
        // sẽ báo lỗi oan → hai ô phải dùng hai validator khác nhau.
        Assert.Null(GsheetConfigSync.KiemTraSheet2(UrlSheet2));
        Assert.NotNull(GsheetConfigSync.KiemTraUrl(UrlSheet2));
    }

    [Theory]
    [InlineData(UrlSheet2, IdSheet2)]
    [InlineData(UrlSheet2 + "?usp=sharing", IdSheet2)]
    [InlineData(UrlSheet2 + "#gid=0", IdSheet2)]
    [InlineData("https://docs.google.com/spreadsheets/d/" + IdSheet2, IdSheet2)] // không có đuôi /edit
    [InlineData("  " + IdSheet2 + " ", IdSheet2)]                                // ID trần (trim)
    [InlineData("rác rưởi", "")]
    [InlineData("https://example.com/x.html", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void BocIdSheet_DungLuatKhopAppsScript(string? s, string mongDoi)
    {
        Assert.Equal(mongDoi, GsheetConfigSync.BocIdSheet(s));
    }

    // ── Field sheet2 nằm TRONG khối GSheet (không có luật đồng bộ riêng) ──

    [Fact]
    public void ApBanHub_HubTrong_KhongDe_KeCaKhiHubCoSheet2()
    {
        // BẤT BIẾN #1 vẫn là URL Web App: hub chưa có URL = chưa cấu hình → không áp gì, sheet2 local GIỮ NGUYÊN.
        var q = GsheetConfigSync.QuyetDinhApBanHub("", "", IdSheet2, UrlLocal, "", "id-cu-cua-may");
        Assert.False(q.Ap);
    }

    [Fact]
    public void ApBanHub_HubCoUrl_Sheet2HubTRONG_VanAp_XoaSheet2CuaClient()
    {
        // HÀNH VI CỐ Ý (khoá lại bằng test): hub CÓ URL ⇒ khối GSheet đã cấu hình ⇒ áp CẢ sheet2 rỗng — rỗng ở
        // đây nghĩa là "TẮT ghi file phụ", y như tab rỗng nghĩa là "tự động theo tháng".
        var q = GsheetConfigSync.QuyetDinhApBanHub(UrlHub, "", "", UrlHub, "", IdSheet2);
        Assert.True(q.Ap);
        Assert.Equal("", q.Sheet2);
    }

    [Fact]
    public void ApBanHub_ChiKhacSheet2_VanAp()
    {
        var q = GsheetConfigSync.QuyetDinhApBanHub(UrlHub, "Tab A", IdSheet2, UrlHub, "Tab A", null);
        Assert.True(q.Ap);
        Assert.Equal(IdSheet2, q.Sheet2);
    }

    [Fact]
    public void ApBanHub_BaFieldYHetLocal_KhongGhiLai()
    {
        var q = GsheetConfigSync.QuyetDinhApBanHub(UrlHub, "Tab A", IdSheet2, UrlHub, "Tab A", IdSheet2);
        Assert.False(q.Ap);
    }

    [Fact]
    public void ApBanHub_Sheet2ChiKhacKhoangTrangThua_KhongGhiLai()
    {
        var q = GsheetConfigSync.QuyetDinhApBanHub(UrlHub, "Tab A", "  " + IdSheet2 + " ", UrlHub, "Tab A", IdSheet2);
        Assert.False(q.Ap);
    }

    [Fact]
    public void ApBanHub_KhuonCu2Field_Sheet2Rong()
    {
        // Overload cũ (call site/test viết trước khi có ô này) → coi như sheet2 rỗng cả hai bên.
        var q = GsheetConfigSync.QuyetDinhApBanHub(UrlHub, "Tab A", UrlLocal, "Tab A");
        Assert.True(q.Ap);
        Assert.Equal("", q.Sheet2);
    }

    [Fact]
    public void NhanBanClient_ClientCoUrl_Sheet2Rong_XOA_Sheet2CuaHub()
    {
        // ĐỐI XỨNG chiều kéo: xoá ô ở client phải xoá được trên hub, không thì hub đẩy ngược bản cũ về.
        var q = GsheetConfigSync.QuyetDinhNhanBanClient(UrlLocal, "", "", UrlLocal, "", IdSheet2);
        Assert.True(q.Ap);
        Assert.Equal("", q.Sheet2);
    }

    [Fact]
    public void NhanBanClient_ChiKhacSheet2_Ghi()
    {
        var q = GsheetConfigSync.QuyetDinhNhanBanClient(UrlHub, "Tab A", IdSheet2, UrlHub, "Tab A", null);
        Assert.True(q.Ap);
        Assert.Equal(IdSheet2, q.Sheet2);
    }

    [Fact]
    public void HaiChieu_DoiXung_Sheet2_SuaOClientRoiKeoVe_KhongBiRevert()
    {
        // End-to-end: client điền sheet2 → hub nhận → chính client kéo về thì KHÔNG bị hub đẩy ngược bản rỗng cũ.
        var nhan = GsheetConfigSync.QuyetDinhNhanBanClient(UrlHub, "", IdSheet2, UrlHub, "", null);
        Assert.True(nhan.Ap);
        Assert.Equal(IdSheet2, nhan.Sheet2);

        var ap = GsheetConfigSync.QuyetDinhApBanHub(nhan.Url, nhan.Tab, nhan.Sheet2, UrlHub, "", IdSheet2);
        Assert.False(ap.Ap);

        // Máy thứ 2 (chưa có sheet2) kéo về → nhận theo hub. Đúng ý "hub thắng".
        var ap2 = GsheetConfigSync.QuyetDinhApBanHub(nhan.Url, nhan.Tab, nhan.Sheet2, UrlHub, "", null);
        Assert.True(ap2.Ap);
        Assert.Equal(IdSheet2, ap2.Sheet2);
    }
}
