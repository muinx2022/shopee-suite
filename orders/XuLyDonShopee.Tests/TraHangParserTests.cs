using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test các hàm THUẦN của bước "check đơn trả hàng" (bước CUỐI flow mỗi shop):
/// <list type="bullet">
/// <item><see cref="TraHangParser.QuyetDinhCheck"/> — 4 nhánh luật đếm (lần đầu / không đổi / giảm / tăng k).</item>
/// <item><see cref="TraHangParser.ParseSoYeuCau"/> — "7 Yêu cầu" → 7, text lạ → null (KHÔNG ném).</item>
/// <item><see cref="TraHangParser.TachMa"/> + <see cref="TraHangParser.GhepCap"/> — tách cặp mã theo NHÃN từ HTML
/// mẫu: dòng CHỈ có mã đơn (đơn hủy — chỗ mã yêu cầu là <c>&lt;!----&gt;</c>) phải BỊ BỎ, dòng đủ hai mã ra đúng cặp.</item>
/// </list>
/// HTML mẫu dựng theo đúng khuôn người dùng gửi (khối <c>&lt;div class="id order-id"&gt;</c> +
/// <c>&lt;span class="id-content"&gt;</c>); class khối mã YÊU CẦU chưa xác nhận nên test dùng vài biến thể class
/// khác nhau để chứng minh luật KHÔNG phụ thuộc class.
/// </summary>
public class TraHangParserTests
{
    // ===================== Luật đếm: 4 nhánh =====================

    [Fact]
    public void QuyetDinhCheck_LanDau_ChiGhiNho_KhongCheckDong()
    {
        var q = TraHangParser.QuyetDinhCheck(mocCu: null, soMoi: 36);
        Assert.Equal(LuatSoYeuCau.LanDau, q.Luat);
        Assert.Equal(0, q.SoDongCanCheck);
    }

    [Fact]
    public void QuyetDinhCheck_KhongDoi_BoQua()
    {
        var q = TraHangParser.QuyetDinhCheck(mocCu: 7, soMoi: 7);
        Assert.Equal(LuatSoYeuCau.KhongDoi, q.Luat);
        Assert.Equal(0, q.SoDongCanCheck);
    }

    [Fact]
    public void QuyetDinhCheck_Giam_ChiCapNhatMoc()
    {
        var q = TraHangParser.QuyetDinhCheck(mocCu: 7, soMoi: 3);
        Assert.Equal(LuatSoYeuCau.Giam, q.Luat);
        Assert.Equal(0, q.SoDongCanCheck);
    }

    [Theory]
    [InlineData(7, 8, 1)]
    [InlineData(7, 12, 5)]
    [InlineData(0, 3, 3)]
    public void QuyetDinhCheck_Tang_CheckDungK(int mocCu, int soMoi, int k)
    {
        var q = TraHangParser.QuyetDinhCheck(mocCu, soMoi);
        Assert.Equal(LuatSoYeuCau.Tang, q.Luat);
        Assert.Equal(k, q.SoDongCanCheck);
    }

    [Fact]
    public void QuyetDinhCheck_SoAmLaRac_KepVe0_CoiNhuGiam()
    {
        var q = TraHangParser.QuyetDinhCheck(mocCu: 5, soMoi: -3);
        Assert.Equal(LuatSoYeuCau.Giam, q.Luat);
        Assert.Equal(0, q.SoDongCanCheck);
    }

    // ===================== Parse số yêu cầu =====================

    [Theory]
    [InlineData("7 Yêu cầu", 7)]
    [InlineData("42 Yêu cầu", 42)]
    [InlineData("36 Yêu cầu", 36)]
    [InlineData("1.234 Yêu cầu", 1234)]   // dấu ngăn nghìn GIỮA hai nhóm số
    [InlineData("  0 Yêu cầu  ", 0)]
    [InlineData("7 Requests", 7)]         // giao diện tiếng Anh
    public void ParseSoYeuCau_DocDuocSo(string title, int expected)
        => Assert.Equal(expected, TraHangParser.ParseSoYeuCau(title));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Yêu cầu")]               // không có chữ số
    [InlineData("—")]
    public void ParseSoYeuCau_TextLa_TraNull_KhongNem(string? title)
        => Assert.Null(TraHangParser.ParseSoYeuCau(title));

    // ===================== Tách mã theo NHÃN =====================

    /// <summary>Khối "Mã đơn hàng" đúng khuôn HTML thật người dùng gửi.</summary>
    private static string KhoiMaDon(string ma) =>
        "<div class=\"id order-id\"><span>Mã đơn hàng</span>"
        + $"<span class=\"id-content\">{ma}</span><div class=\"copy-button\"></div></div>";

    /// <summary>Khối "mã yêu cầu trả hàng": class CHƯA xác nhận nên cho truyền vào để test nhiều biến thể.</summary>
    private static string KhoiMaYeuCau(string ma, string nhan = "Mã yêu cầu trả hàng", string cls = "id return-id") =>
        $"<div class=\"{cls}\"><span>{nhan}</span>"
        + $"<span class=\"id-content\">{ma}</span><div class=\"copy-button\"></div></div>";

    [Fact]
    public void TachMa_DuHaiMa_RaDungCap()
    {
        var html = "<div class=\"return-row-item-head\">"
            + KhoiMaDon("260723E428EY8X") + KhoiMaYeuCau("R2607230001") + "</div>";

        var ma = TraHangParser.TachMa(html);

        Assert.Equal("260723E428EY8X", ma.MaDon);
        Assert.Equal("R2607230001", ma.MaYeuCau);
    }

    [Fact]
    public void TachMa_ChiCoMaDon_KhoiMaYeuCauChuaRender_KhongCoMaYeuCau()
    {
        // Đúng cảnh trong HTML người dùng gửi: dòng ĐƠN HỦY nên khối mã yêu cầu còn là <!---->.
        var html = "<div class=\"return-row-item-head\">" + KhoiMaDon("260722BNQRM2GM") + "<!----><!----></div>";

        var ma = TraHangParser.TachMa(html);

        Assert.Equal("260722BNQRM2GM", ma.MaDon);
        Assert.Null(ma.MaYeuCau);
    }

    [Theory]
    // Class khối mã yêu cầu CHƯA xác nhận → luật đi theo NHÃN, đổi class không ảnh hưởng.
    [InlineData("id return-id")]
    [InlineData("id request-id")]
    [InlineData("id refund-id")]
    [InlineData("id")]
    public void TachMa_ClassKhoiMaYeuCauKhacNhau_VanTachDung(string cls)
    {
        var html = "<div class=\"return-row-item-head\">"
            + KhoiMaDon("260721A41HFT22") + KhoiMaYeuCau("R99", cls: cls) + "</div>";

        var ma = TraHangParser.TachMa(html);

        Assert.Equal("260721A41HFT22", ma.MaDon);
        Assert.Equal("R99", ma.MaYeuCau);
    }

    [Theory]
    [InlineData("Mã yêu cầu trả hàng")]
    [InlineData("MÃ YÊU CẦU")]                 // hoa toàn phần
    [InlineData("Ma yeu cau tra hang")]        // không dấu
    [InlineData("Return ID")]                  // giao diện tiếng Anh
    [InlineData("Request ID")]
    public void TachMa_NhanKhacNhau_VanNhanDienDuocMaYeuCau(string nhan)
    {
        var html = "<div class=\"return-row-item-head\">"
            + KhoiMaDon("260723E428EY8X") + KhoiMaYeuCau("R77", nhan: nhan) + "</div>";

        Assert.Equal("R77", TraHangParser.TachMa(html).MaYeuCau);
    }

    [Fact]
    public void TachMa_ThuTuNguoc_VanDungNhan()
    {
        // Khối mã yêu cầu đứng TRƯỚC mã đơn → phân loại theo nhãn nên vẫn đúng (không theo vị trí).
        var html = "<div class=\"return-row-item-head\">"
            + KhoiMaYeuCau("R11") + KhoiMaDon("260723E428EY8X") + "</div>";

        var ma = TraHangParser.TachMa(html);

        Assert.Equal("260723E428EY8X", ma.MaDon);
        Assert.Equal("R11", ma.MaYeuCau);
    }

    [Fact]
    public void TachMa_HaiKhoiNhanLa_DuPhongTheoViTri()
    {
        // Dự phòng CUỐI: đúng 2 khối .id-content mà KHÔNG nhãn nào khớp → khối 1 = mã đơn, khối 2 = mã yêu cầu.
        var html = "<div class=\"return-row-item-head\">"
            + "<div class=\"id a\"><span>Alpha</span><span class=\"id-content\">AAA111</span></div>"
            + "<div class=\"id b\"><span>Beta</span><span class=\"id-content\">BBB222</span></div>"
            + "</div>";

        var ma = TraHangParser.TachMa(html);

        Assert.Equal("AAA111", ma.MaDon);
        Assert.Equal("BBB222", ma.MaYeuCau);
    }

    [Fact]
    public void TachMa_MaDonKhopNhungKhoiKiaNhanLa_KHONGDoanBua()
    {
        // Khối thứ hai có nhãn RÕ RÀNG không phải yêu cầu ("Mã vận đơn") → thà bỏ trống còn hơn ghi mã SAI lên sheet.
        var html = "<div class=\"return-row-item-head\">"
            + KhoiMaDon("260723E428EY8X")
            + "<div class=\"id tn\"><span>Mã vận đơn</span><span class=\"id-content\">SPXVN123</span></div>"
            + "</div>";

        var ma = TraHangParser.TachMa(html);

        Assert.Equal("260723E428EY8X", ma.MaDon);
        Assert.Null(ma.MaYeuCau);
    }

    [Fact]
    public void TachMa_KhoiIdContentRONG_KhongLamNhanDinhSangKhoiSau()
    {
        // Khối mã yêu cầu có .id-content nhưng RỖNG (Vue render nửa vời) + khối sau là "Mã vận đơn":
        // nhãn khối rỗng KHÔNG được dính sang khối sau, kẻo lấy nhầm mã vận đơn làm mã yêu cầu.
        var html = "<div class=\"return-row-item-head\">"
            + KhoiMaDon("260723E428EY8X")
            + "<div class=\"id r\"><span>Mã yêu cầu trả hàng</span><span class=\"id-content\"></span></div>"
            + "<div class=\"id tn\"><span>Mã vận đơn</span><span class=\"id-content\">SPXVN123</span></div>"
            + "</div>";

        var ma = TraHangParser.TachMa(html);

        Assert.Equal("260723E428EY8X", ma.MaDon);
        Assert.Null(ma.MaYeuCau);
    }

    [Fact]
    public void TachMa_NbspTrongNhan_VanKhop()
    {
        var html = "<div class=\"return-row-item-head\">"
            + KhoiMaDon("260723E428EY8X")
            + "<div class=\"id x\"><span>Mã&nbsp;yêu&nbsp;cầu</span><span class=\"id-content\">R55</span></div>"
            + "</div>";

        Assert.Equal("R55", TraHangParser.TachMa(html).MaYeuCau);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<div class=\"return-row-item-head\"><!----><!----></div>")]  // chưa render gì
    [InlineData("khong phai html")]
    public void TachMa_RacHoacRong_KhongNem(string? html)
    {
        var ma = TraHangParser.TachMa(html);
        Assert.Null(ma.MaDon);
        Assert.Null(ma.MaYeuCau);
    }

    // ===================== Ghép cặp cả lô =====================

    private static DongTraHang Dong(string html, string? soi = null) => new(soi, html);

    [Fact]
    public void GhepCap_ChiLayDongDuHaiMa_DongThieuVaoDanhSachChanDoan()
    {
        var dong = new[]
        {
            Dong("<div class=\"return-row-item-head\">" + KhoiMaDon("D1") + KhoiMaYeuCau("R1") + "</div>", "111"),
            Dong("<div class=\"return-row-item-head\">" + KhoiMaDon("D2") + "<!----><!----></div>", "222"),
            Dong("<div class=\"return-row-item-head\">" + KhoiMaDon("D3") + KhoiMaYeuCau("R3") + "</div>", "333"),
        };

        var kq = TraHangParser.GhepCap(dong);

        Assert.Equal(2, kq.Cap.Count);
        Assert.Equal(new[] { "D1", "D3" }, kq.Cap.Select(c => c.MaDon));
        Assert.Equal(new[] { "R1", "R3" }, kq.Cap.Select(c => c.MaYeuCau));

        // Dòng thiếu mã yêu cầu PHẢI để lại dấu vết đủ để soi (mã đơn + HTML thô) — luật nhãn có thể trượt.
        var thieu = Assert.Single(kq.ThieuMaYeuCau);
        Assert.Contains("D2", thieu);
        Assert.Contains("return-row-item-head", thieu);
    }

    [Fact]
    public void GhepCap_DongKhongCoMaDon_BoImLang()
    {
        var kq = TraHangParser.GhepCap(new[] { Dong("<div class=\"return-row-item-head\"><!----></div>") });

        Assert.Empty(kq.Cap);
        Assert.Empty(kq.ThieuMaYeuCau);
    }

    [Fact]
    public void GhepCap_MaDonTrung_ChiGiuCapDau()
    {
        var dong = new[]
        {
            Dong("<div class=\"h\">" + KhoiMaDon("D1") + KhoiMaYeuCau("R-MOI") + "</div>"),
            Dong("<div class=\"h\">" + KhoiMaDon("D1") + KhoiMaYeuCau("R-CU") + "</div>"),
        };

        var cap = Assert.Single(TraHangParser.GhepCap(dong).Cap);
        Assert.Equal("R-MOI", cap.MaYeuCau); // danh sách sắp mới→cũ nên cặp ĐẦU là mới nhất
    }

    [Fact]
    public void GhepCap_RongHoacNull_KhongNem()
    {
        Assert.Empty(TraHangParser.GhepCap(Array.Empty<DongTraHang>()).Cap);
        Assert.Empty(TraHangParser.GhepCap(null!).Cap);
    }

    // ===================== Parse cả gói JSON extension gửi =====================

    [Fact]
    public void ParseKetQua_JsonChuan_DocDuSoVaDong()
    {
        var html = "<div class=\"return-row-item-head\">" + KhoiMaDon("D1") + KhoiMaYeuCau("R1") + "</div>";
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            soYeuCauText = "7 Yêu cầu",
            sortApplied = true,
            list = new[] { new { shopeeOrderId = "12345", headHtml = html } },
        });

        var kq = TraHangParser.ParseKetQua(json);

        Assert.Equal(7, kq.SoYeuCau);
        Assert.True(kq.SortApplied);
        var d = Assert.Single(kq.Dong);
        Assert.Equal("12345", d.ShopeeOrderId);
        Assert.Equal("R1", Assert.Single(TraHangParser.GhepCap(kq.Dong).Cap).MaYeuCau);
    }

    [Fact]
    public void ParseKetQua_SortKhongApDung_CoCoCanhBao()
    {
        var kq = TraHangParser.ParseKetQua("{\"soYeuCauText\":\"3 Yêu cầu\",\"sortApplied\":false,\"list\":[]}");

        Assert.Equal(3, kq.SoYeuCau);
        Assert.False(kq.SortApplied);
        Assert.Empty(kq.Dong);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{")]                                   // JSON cụt
    [InlineData("[1,2,3]")]                             // không phải object
    [InlineData("{\"soYeuCauText\":\"khong co so\"}")]  // text lạ → SoYeuCau null
    public void ParseKetQua_RacHoacRong_TraKetQuaRong_KhongNem(string? json)
    {
        var kq = TraHangParser.ParseKetQua(json);
        Assert.Null(kq.SoYeuCau);
        Assert.False(kq.SortApplied);
        Assert.Empty(kq.Dong);
    }

    [Fact]
    public void ParseKetQua_DongThieuHeadHtml_BoQua()
    {
        var kq = TraHangParser.ParseKetQua(
            "{\"soYeuCauText\":\"2 Yêu cầu\",\"sortApplied\":true,\"list\":[{\"shopeeOrderId\":\"1\"},{\"headHtml\":\"\"}]}");

        Assert.Equal(2, kq.SoYeuCau);
        Assert.Empty(kq.Dong);
    }
}
