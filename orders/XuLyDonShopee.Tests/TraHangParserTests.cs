using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test các hàm THUẦN của bước "check đơn trả hàng" (bước CUỐI flow mỗi shop):
/// <list type="bullet">
/// <item><see cref="TraHangParser.QuyetDinhCheck"/> — 4 nhánh luật đếm (lần đầu = min(số, trần) / không đổi /
/// giảm / tăng k).</item>
/// <item><see cref="TraHangParser.LocTheoCuaSo"/> — chặn theo cửa sổ NGÀY YÊU CẦU (suy từ MÃ YÊU CẦU, 20 ngày),
/// LỌC chứ không dừng sớm.</item>
/// <item><see cref="TraHangParser.ParseSoYeuCau"/> — "7 Yêu cầu" → 7, text lạ → null (KHÔNG ném).</item>
/// <item><see cref="TraHangParser.TachMa"/> + <see cref="TraHangParser.GhepCap"/> — tách cặp mã từ HTML mẫu:
/// dòng CHỈ có mã đơn (đơn hủy — chỗ mã yêu cầu là <c>&lt;!----&gt;</c>) phải BỊ BỎ, dòng đủ hai mã ra đúng cặp.</item>
/// </list>
/// Hai lớp HTML mẫu:
/// <list type="bullet">
/// <item>Mẫu DỰNG TAY (các <c>Khoi*</c> ngắn) — giữ nguyên từ đợt trước, khi class khối mã yêu cầu chưa xác nhận;
/// vẫn hữu ích vì chứng minh luật vẫn chạy khi class đổi.</item>
/// <item><b>HTML THẬT</b> của một dòng trả hàng đầy đủ (<see cref="HeadThat"/>) — nay đã xác nhận khối mã yêu cầu
/// là <c>&lt;div class="id return-id"&gt;</c>, khối mã đơn là <c>&lt;div class="id order-id"&gt;</c>. Fixture này
/// giữ cả tên người mua, avatar, 2 icon copy và <c>&lt;!----&gt;</c> cuối để test đúng cảnh thật.</item>
/// </list>
/// </summary>
public class TraHangParserTests
{
    // ===================== Luật đếm: 4 nhánh =====================

    /// <summary>LẦN ĐẦU phải ĐỌC, không chỉ ghi mốc: bản trước trả 0 nên shop nào cũng chốt mốc rồi im lặng mãi,
    /// toàn bộ yêu cầu ĐANG CÓ không bao giờ được đọc (số thật lấy về từ lúc phát hành là 0).</summary>
    [Fact]
    public void QuyetDinhCheck_LanDau_CheckDungSoYeuCau_DuoiTran()
    {
        var q = TraHangParser.QuyetDinhCheck(mocCu: null, soMoi: 12);
        Assert.Equal(LuatSoYeuCau.LanDau, q.Luat);
        Assert.Equal(12, q.SoDongCanCheck);
    }

    /// <summary>Vượt trần → kẹp về <see cref="TraHangParser.TranDongMoiLuot"/> (extension cũng chỉ gửi tối đa
    /// chừng đó dòng; đọc sâu hơn cũng vô ích vì DB chỉ giữ đơn vài ngày gần đây).</summary>
    [Fact]
    public void QuyetDinhCheck_LanDau_VuotTran_KepVeTran()
    {
        var q = TraHangParser.QuyetDinhCheck(mocCu: null, soMoi: 340);
        Assert.Equal(LuatSoYeuCau.LanDau, q.Luat);
        Assert.Equal(TraHangParser.TranDongMoiLuot, q.SoDongCanCheck);
        Assert.Equal(50, q.SoDongCanCheck);
    }

    [Fact]
    public void QuyetDinhCheck_LanDau_KhongCoYeuCauNao_Check0()
    {
        var q = TraHangParser.QuyetDinhCheck(mocCu: null, soMoi: 0);
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
        // cls: "id" (BỎ token "return-id") là CỐ Ý: từ khi có tầng CLASS, để class mặc định thì tầng 1 quyết
        // xong ngay và test này không còn kiểm được luật NHÃN nữa — vẫn xanh nhưng rỗng nghĩa. Bỏ class riêng
        // đi mới ép tụt xuống tầng nhãn, đúng ý định gốc của test.
        var html = "<div class=\"return-row-item-head\">"
            + KhoiMaDon("260723E428EY8X") + KhoiMaYeuCau("R77", nhan: nhan, cls: "id") + "</div>";

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

    // ===================== HTML THẬT của một dòng trả hàng (class đã ghim) =====================

    /// <summary>Hash scoped-css của Vue trên mọi thẻ trong dòng — rác thuần, giữ để fixture giống trang thật.</summary>
    private const string DataV = "data-v-3e8f5a12";

    /// <summary>Path của icon copy (eds-icon) — HAI nút copy dùng CÙNG icon này. Giữ trong fixture vì đây chính
    /// là thứ làm phình HTML (mỗi cái ~400 ký tự) và cũng để chứng minh luật không bị SVG làm nhiễu.</summary>
    private const string PathIconCopy =
        "M13 1H4.625C4.28 1 4 1.28 4 1.625v1.75c0 .345.28.625.625.625s.625-.28.625-.625V2.25H12.5v9.5h-1.125"
        + "c-.345 0-.625.28-.625.625s.28.625.625.625H13c.345 0 .625-.28.625-.625V1.625C13.625 1.28 13.345 1 13 1z"
        + "M11.375 4.5H3c-.345 0-.625.28-.625.625v9.25c0 .345.28.625.625.625h8.375c.345 0 .625-.28.625-.625v-9.25"
        + "c0-.345-.28-.625-.625-.625zm-.625 9.25h-7.125V5.75h7.125v8z";

    /// <summary>Một khối mã theo đúng khuôn trang thật: <c>&lt;span&gt;nhãn&lt;/span&gt;</c> +
    /// <c>&lt;span class="id-content"&gt;giá trị&lt;/span&gt;</c> + nút copy (icon SVG).</summary>
    private static string KhoiIdThat(string cls, string nhan, string ma) =>
        $"<div {DataV} class=\"{cls}\"><span {DataV}>{nhan}</span>"
        + $"<span {DataV} class=\"id-content\">{ma}</span>"
        + $"<div {DataV} class=\"copy-button\"><i class=\"eds-icon icon-copy\">"
        + $"<svg viewBox=\"0 0 16 16\" width=\"1em\" height=\"1em\" fill=\"none\">"
        + $"<path d=\"{PathIconCopy}\" fill=\"currentColor\"></path></svg></i></div></div>";

    /// <summary>
    /// Khối <c>.return-row-item-head</c> của dòng trả hàng THẬT (dòng <c>/portal/sale/return/235778510235654</c>):
    /// khối người mua (avatar + <c>.username</c>) → <c>id order-id</c> → <c>id return-id</c> → <c>&lt;!----&gt;</c>.
    /// Các tham số để dựng biến thể: tên người mua (dữ liệu NGƯỜI DÙNG tự đặt — có thể chứa "return"/"request"),
    /// class hai khối (mô phỏng Shopee đổi class), đảo thứ tự khối, và khối mã yêu cầu chưa render (đơn HỦY).
    /// </summary>
    private static string HeadThat(
        string username = "ttd911",
        string classDon = "id order-id",
        string classYeuCau = "id return-id",
        bool daoThuTu = false,
        bool yeuCauChuaRender = false)
    {
        var khoiDon = KhoiIdThat(classDon, "Mã đơn hàng", "260619GSNQ36U7");
        var khoiYeuCau = yeuCauChuaRender
            ? "<!---->"
            : KhoiIdThat(classYeuCau, "Mã yêu cầu trả hàng", "2606220PN1D6X06");

        return $"<div {DataV} class=\"return-row-item-head\">"
            + $"<div {DataV} class=\"user-view-item return-row-user\">"
            + $"<img {DataV} class=\"avatar\" src=\"https://down-vn.img.susercontent.com/file/vn-11134233-7ras8_tn\">"
            + $"<div {DataV} class=\"username text-overflow\">{username}</div></div>"
            + (daoThuTu ? khoiYeuCau + khoiDon : khoiDon + khoiYeuCau)
            + "<!----></div>";
    }

    [Fact]
    public void TachMa_HtmlThatTrangTraHang_RaDungHaiMa()
    {
        var ma = TraHangParser.TachMa(HeadThat());

        Assert.Equal("260619GSNQ36U7", ma.MaDon);
        Assert.Equal("2606220PN1D6X06", ma.MaYeuCau);
    }

    /// <summary>
    /// HTML <b>NGUYÊN VĂN</b> khối <c>.return-row-item-head</c> chép từ trang thật (dòng
    /// <c>/portal/sale/return/235778510235654</c>, shop thật, 28/07/2026) — KHÔNG rút gọn, KHÔNG tái dựng:
    /// giữ nguyên mọi <c>data-v-*</c>, thuộc tính <c>account="[object Object]"</c>, hai SVG icon copy đầy đủ,
    /// và <c>&lt;!----&gt;</c> cuối khối. <see cref="HeadThat"/> ở trên là bản DỰNG ĐƯỢC THAM SỐ dùng cho các
    /// biến thể (đổi tên người mua / đổi class / đảo thứ tự); còn hằng này là mỏ neo: nếu ai đó sửa luật mà
    /// vẫn muốn test xanh thì phải xanh trên chính HTML Shopee trả về.
    /// <para>Lưu ý cấu trúc thật SÂU HƠN bản dựng: tên người mua nằm trong
    /// <c>.user-view-item &gt; .content &gt; .username</c> chứ không phải con trực tiếp.</para>
    /// </summary>
    private const string HeadNguyenVanTuTrangThat =
        """
        <div data-v-6a1b46c4="" data-v-6c0fe5b8="" class="return-row-item-head"><div data-v-2f869f9a="" data-v-6a1b46c4="" class="user-view-item linkable simple-nofollow user-header" account="[object Object]"
        ><div data-v-2f869f9a="" data-v-2f869f9a-s="" class="avatar"><img data-v-2f869f9a="" data-v-2f869f9a-s="" class="image" src="https://cf.shopee.vn/file/a5c947f6ed4c79213467e202028ba3c5" width="100%" he
        ight="100%"></div><div data-v-2f869f9a="" class="content"><div data-v-2f869f9a="" class="username text-overflow">ttd911</div></div><!----></div><div data-v-6a1b46c4="" class="id order-id"><span data-v
        -6a1b46c4="">Mã đơn hàng</span><span data-v-6a1b46c4="" class="id-content">260619GSNQ36U7</span><div data-v-5c91486a="" data-v-6a1b46c4="" class="copy-button"><i data-v-ef5019c0="" data-v-5c91486a="" 
        class="eds-icon copy-icon grey"><svg xmlns="http://www.w3.org/2000/svg"><path d="M13 1H4.625a.125.125 0 0 0-.125.125V2c0 .069.056.125.125.125h7.75v10.75c0 .069.056.125.125.125h.875a.125.125 0 0 0 .125
        -.125V1.5A.5.5 0 0 0 13 1Zm-2 2H3a.5.5 0 0 0-.5.5v8.292c0 .133.053.26.147.353l2.708 2.708a.519.519 0 0 0 .115.086v.03h.066c.055.02.112.031.172.031H11a.5.5 0 0 0 .5-.5v-11A.5.5 0 0 0 11 3ZM5.469 13.378
        l-1.346-1.347H5.47v1.347Zm4.906.497H6.469v-2.219a.625.625 0 0 0-.625-.625H3.625V4.125h6.75v9.75Z"></path></svg></i></div></div><div data-v-6a1b46c4="" class="id return-id"><span data-v-6a1b46c4="">Mã 
        yêu cầu trả hàng</span><span data-v-6a1b46c4="" class="id-content">2606220PN1D6X06</span><div data-v-5c91486a="" data-v-6a1b46c4="" class="copy-button"><i data-v-ef5019c0="" data-v-5c91486a="" class="
        eds-icon copy-icon grey"><svg xmlns="http://www.w3.org/2000/svg"><path d="M13 1H4.625a.125.125 0 0 0-.125.125V2c0 .069.056.125.125.125h7.75v10.75c0 .069.056.125.125.125h.875a.125.125 0 0 0 .125-.125V1
        .5A.5.5 0 0 0 13 1Zm-2 2H3a.5.5 0 0 0-.5.5v8.292c0 .133.053.26.147.353l2.708 2.708a.519.519 0 0 0 .115.086v.03h.066c.055.02.112.031.172.031H11a.5.5 0 0 0 .5-.5v-11A.5.5 0 0 0 11 3ZM5.469 13.378l-1.346
        -1.347H5.47v1.347Zm4.906.497H6.469v-2.219a.625.625 0 0 0-.625-.625H3.625V4.125h6.75v9.75Z"></path></svg></i></div></div><!----></div>
        """;

    [Fact]
    public void TachMa_HtmlNguyenVanTuTrangThat_RaDungHaiMa()
    {
        var ma = TraHangParser.TachMa(HeadNguyenVanTuTrangThat);

        Assert.Equal("260619GSNQ36U7", ma.MaDon);
        Assert.Equal("2606220PN1D6X06", ma.MaYeuCau);
    }

    /// <summary>Trên HTML NGUYÊN VĂN, tên người mua độc vẫn KHÔNG kéo được mã đơn sang ô mã yêu cầu — cấu trúc
    /// thật lồng sâu hơn bản dựng nên đây mới là ca chốt cho lỗi "username lọt vào nhãn".</summary>
    [Theory]
    [InlineData("returnking88")]
    [InlineData("shop_request_vn")]
    [InlineData("Shop yêu cầu 24h")]
    public void TachMa_HtmlNguyenVan_TenNguoiMuaDoc_VanRaDungHaiMa(string username)
    {
        var html = HeadNguyenVanTuTrangThat.Replace(">ttd911<", $">{username}<", StringComparison.Ordinal);

        var ma = TraHangParser.TachMa(html);

        Assert.Equal("260619GSNQ36U7", ma.MaDon);
        Assert.Equal("2606220PN1D6X06", ma.MaYeuCau);
    }

    /// <summary>Shopee bỏ token <c>order-id</c>/<c>return-id</c> → tụt xuống tầng NHÃN. Chạy trên HTML NGUYÊN
    /// VĂN mới có ý nghĩa: cấu trúc thật lồng sâu (<c>.user-view-item &gt; .content &gt; .username</c>) nên đây
    /// là chỗ dễ vỡ nhất của luật "chỉ lấy nhãn từ thẻ span gần nhất".</summary>
    [Fact]
    public void TachMa_HtmlNguyenVan_MatClassRieng_DuPhongNhanVanDung()
    {
        var html = HeadNguyenVanTuTrangThat
            .Replace("class=\"id order-id\"", "class=\"id\"", StringComparison.Ordinal)
            .Replace("class=\"id return-id\"", "class=\"id\"", StringComparison.Ordinal);

        var ma = TraHangParser.TachMa(html);

        Assert.Equal("260619GSNQ36U7", ma.MaDon);
        Assert.Equal("2606220PN1D6X06", ma.MaYeuCau);
    }

    /// <summary>Cùng lúc MẤT class riêng VÀ tên người mua độc — tầng nhãn phải tự đứng vững, không được dựa vào
    /// tầng class đã mất. Đây là ca gắt nhất: bản cũ sai ngay cả khi CÒN class.</summary>
    [Fact]
    public void TachMa_HtmlNguyenVan_MatClass_VaTenNguoiMuaDoc_VanDung()
    {
        var html = HeadNguyenVanTuTrangThat
            .Replace("class=\"id order-id\"", "class=\"id\"", StringComparison.Ordinal)
            .Replace("class=\"id return-id\"", "class=\"id\"", StringComparison.Ordinal)
            .Replace(">ttd911<", ">returnking88<", StringComparison.Ordinal);

        var ma = TraHangParser.TachMa(html);

        Assert.Equal("260619GSNQ36U7", ma.MaDon);
        Assert.Equal("2606220PN1D6X06", ma.MaYeuCau);
    }

    [Theory]
    // Tên người mua là dữ liệu NGƯỜI DÙNG TỰ ĐẶT. Luật cũ lấy nhãn = "mọi text từ khối trước tới khối này" nên
    // với khối ĐẦU nó nuốt luôn username → username chứa "return"/"request"/"yêu cầu" làm mã ĐƠN HÀNG bị gán
    // thành mã yêu cầu trả hàng (ghi mã SAI lên Google Sheet). Đây là ca hồi quy cho lỗi đó.
    [InlineData("ttd911")]
    [InlineData("returnking88")]
    [InlineData("shop_request_vn")]
    [InlineData("yeucaushop")]
    [InlineData("Shop yêu cầu 24h")]
    public void TachMa_HtmlThat_UsernameDoc_VanRaDungHaiMa(string username)
    {
        var ma = TraHangParser.TachMa(HeadThat(username));

        Assert.Equal("260619GSNQ36U7", ma.MaDon);
        Assert.Equal("2606220PN1D6X06", ma.MaYeuCau);
    }

    [Fact]
    public void TachMa_HtmlThat_DaoThuTuKhoi_ClassQuyetDinhChuKhongPhaiViTri()
    {
        var ma = TraHangParser.TachMa(HeadThat(daoThuTu: true));

        Assert.Equal("260619GSNQ36U7", ma.MaDon);
        Assert.Equal("2606220PN1D6X06", ma.MaYeuCau);
    }

    [Theory]
    // Shopee bỏ class riêng của hai khối (chỉ còn "id") → rơi xuống tầng NHÃN; nhãn nay chỉ lấy <span> gần nhất
    // nên username độc vẫn không lọt vào.
    [InlineData("ttd911")]
    [InlineData("returnking88")]
    public void TachMa_HtmlThat_MatClassRieng_DuPhongNhanVanDung(string username)
    {
        var ma = TraHangParser.TachMa(HeadThat(username, classDon: "id", classYeuCau: "id"));

        Assert.Equal("260619GSNQ36U7", ma.MaDon);
        Assert.Equal("2606220PN1D6X06", ma.MaYeuCau);
    }

    [Fact]
    public void TachMa_HtmlThat_DonHuy_KhoiReturnIdChuaRender_ThieuMaYeuCau()
    {
        var html = HeadThat(yeuCauChuaRender: true);

        var ma = TraHangParser.TachMa(html);
        Assert.Equal("260619GSNQ36U7", ma.MaDon);
        Assert.Null(ma.MaYeuCau);

        // Dòng như thế phải vào danh sách chẩn đoán, KÈM class dò được của từng khối (không chỉ nhãn).
        var thieu = Assert.Single(TraHangParser.GhepCap(new[] { Dong(html) }).ThieuMaYeuCau);
        Assert.Contains("260619GSNQ36U7", thieu);
        Assert.Contains("class='id order-id'", thieu);
        Assert.Contains("nhãn='Mã đơn hàng'", thieu);
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

    [Fact]
    public void ParseKetQua_TabTraHang_DocDuocCoTrue()
    {
        var kq = TraHangParser.ParseKetQua(
            "{\"soYeuCauText\":\"3 Yêu cầu\",\"sortApplied\":true,\"tabTraHang\":true,\"list\":[]}");

        Assert.True(kq.TabTraHang);
    }

    /// <summary>Thiếu field (bản extension CŨ) hoặc false → cờ false ⇒ caller log cảnh báo "số có thể lẫn đơn hủy".</summary>
    [Theory]
    [InlineData("{\"soYeuCauText\":\"3 Yêu cầu\",\"sortApplied\":true,\"tabTraHang\":false,\"list\":[]}")]
    [InlineData("{\"soYeuCauText\":\"3 Yêu cầu\",\"sortApplied\":true,\"list\":[]}")]
    public void ParseKetQua_TabTraHang_ThieuHoacFalse_LaFalse(string json)
    {
        Assert.False(TraHangParser.ParseKetQua(json).TabTraHang);
    }

    // ===================== Chốt chặn theo href: bỏ dòng ĐƠN HỦY =====================

    /// <summary>
    /// Dòng ĐƠN HỦY trên trang trả hàng có <c>href = /portal/sale/order/…</c> (không phải
    /// <c>/portal/sale/return/…</c>) và KHÔNG có khối mã yêu cầu. Extension gửi cờ <c>laTraHang=false</c> để C#
    /// bỏ + ĐẾM ĐƯỢC — chốt chặn thứ hai, độc lập với việc chọn được tab hay không.
    /// </summary>
    [Fact]
    public void GhepCap_DongDonHuy_TheoHref_BiBo_VaDemRieng()
    {
        var dong = new[]
        {
            new DongTraHang(null, "<div>" + KhoiMaDon("D1") + KhoiMaYeuCau("R1") + "</div>", true),
            // Dòng đơn hủy THẬT: chỉ có khối mã đơn, href /portal/sale/order/… → cờ false.
            new DongTraHang("238153025271149", "<div>" + KhoiMaDon("260713HUBU75VU") + "<!----><!----></div>", false),
        };

        var kq = TraHangParser.GhepCap(dong);

        Assert.Equal("D1", Assert.Single(kq.Cap).MaDon);
        Assert.Equal(1, kq.BoQuaDonHuy);
        // Dòng đơn hủy KHÔNG được rơi vào danh sách chẩn đoán "thiếu mã yêu cầu" — nó đâu phải dòng trả hàng.
        Assert.Empty(kq.ThieuMaYeuCau);
    }

    /// <summary>
    /// Dòng ĐƠN HỦY mà lại ĐỦ hai mã (giả định: Shopee đổi cấu trúc) vẫn bị bỏ theo href — href là chốt chặn
    /// mạnh hơn nội dung, và đơn hủy thì không bao giờ có yêu cầu trả hàng thật.
    /// </summary>
    [Fact]
    public void GhepCap_HrefDonHuy_ThangCaKhiDuHaiMa()
    {
        var dong = new[]
        {
            new DongTraHang(null, "<div>" + KhoiMaDon("D9") + KhoiMaYeuCau("R9") + "</div>", false),
        };

        var kq = TraHangParser.GhepCap(dong);

        Assert.Empty(kq.Cap);
        Assert.Equal(1, kq.BoQuaDonHuy);
    }

    /// <summary>Extension đời CŨ chưa gửi cờ (<c>null</c>) → GIỮ như trước; chốt chặn mới không được làm câm bản
    /// client chưa cập nhật.</summary>
    [Fact]
    public void GhepCap_ThieuCoLaTraHang_ClientCu_VanGiuDong()
    {
        var dong = new[] { new DongTraHang(null, "<div>" + KhoiMaDon("D1") + KhoiMaYeuCau("R1") + "</div>") };

        var kq = TraHangParser.GhepCap(dong);

        Assert.Equal("R1", Assert.Single(kq.Cap).MaYeuCau);
        Assert.Equal(0, kq.BoQuaDonHuy);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void ParseKetQua_DocDuocCoLaTraHang(string giaTri, bool mong)
    {
        var kq = TraHangParser.ParseKetQua(
            "{\"soYeuCauText\":\"1 Yêu cầu\",\"list\":[{\"laTraHang\":" + giaTri + ",\"headHtml\":\"<div>x</div>\"}]}");

        Assert.Equal(mong, Assert.Single(kq.Dong).LaTraHang);
    }

    /// <summary>Thiếu field (extension đời cũ) → <c>null</c> = "không biết", KHÁC hẳn <c>false</c> = "đơn hủy".</summary>
    [Fact]
    public void ParseKetQua_ThieuCoLaTraHang_LaNull_KhongPhaiFalse()
    {
        var kq = TraHangParser.ParseKetQua(
            "{\"soYeuCauText\":\"1 Yêu cầu\",\"list\":[{\"headHtml\":\"<div>x</div>\"}]}");

        Assert.Null(Assert.Single(kq.Dong).LaTraHang);
    }

    // ===================== Chẩn đoán khi trang không render =====================

    /// <summary>Gói <c>chanDoan</c> extension gửi khi BỎ lượt → một dòng text đủ phân biệt hết-giờ-thật /
    /// lạc-trang / sai-selector. Không có gói → null (lượt đọc bình thường).</summary>
    [Fact]
    public void ParseKetQua_CoChanDoan_DungThanhMotDongDeLog()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            soYeuCauText = "",
            list = Array.Empty<object>(),
            chanDoan = new
            {
                url = "https://banhang.shopee.vn/portal/sale/returnrefundcancel",
                title = "Trả hàng/Hoàn tiền/Hủy",
                coOTong = true,
                textOTong = "",
                soDong = 12,
                coTabWrapper = true,
            },
        });

        var cd = TraHangParser.ParseKetQua(json).ChanDoan;

        Assert.NotNull(cd);
        Assert.Contains("returnrefundcancel", cd);
        Assert.Contains("ô tổng CÓ nhưng RỖNG", cd);   // ⇒ hết giờ THẬT, không phải sai selector
        Assert.Contains("12 dòng", cd);
        Assert.Contains("CÓ .return-case-tab-wrapper", cd);
    }

    [Fact]
    public void ParseKetQua_MatOTong_ChanDoanNoiRoLaMatSelector()
    {
        var json = "{\"soYeuCauText\":\"\",\"list\":[],\"chanDoan\":"
            + "{\"url\":\"https://x/\",\"title\":\"t\",\"coOTong\":false,\"textOTong\":\"\",\"soDong\":0,\"coTabWrapper\":false}}";

        var cd = TraHangParser.ParseKetQua(json).ChanDoan;

        Assert.Contains("KHÔNG có .return-list-summary-title", cd);
        Assert.Contains("KHÔNG có .return-case-tab-wrapper", cd);
    }

    [Fact]
    public void ParseKetQua_KhongCoChanDoan_TraNull()
        => Assert.Null(TraHangParser.ParseKetQua("{\"soYeuCauText\":\"3 Yêu cầu\",\"list\":[]}").ChanDoan);

    // ===================== Lọc theo cửa sổ NGÀY YÊU CẦU =====================
    //
    // ⚠ Khối này trước đây đo trên NGÀY ĐẶT ĐƠN (mã đơn) với cửa sổ 7 ngày. Đổi trục là CHỦ ĐÍCH, không phải sửa
    // test cho xanh: Shopee cho trả hàng trong 15 ngày, nên một yêu cầu HÔM NAY thường thuộc đơn đặt từ rất lâu —
    // đo trên ngày đặt đơn là vứt đúng những mã vừa phát sinh. Từ khi có bảng `return_codes` (mã sống độc lập với
    // vòng đời đơn), việc đơn còn hay đã bị dọn không còn liên quan. Mọi ca của khối cũ được GIỮ NGUYÊN Ý ĐỊNH và
    // dựng lại trên trục mới; riêng ca "không đọc được ngày" ĐỔI CHIỀU (bỏ → GIỮ) theo đúng luật mới.

    private static readonly DateTime HomNay = new(2026, 7, 29);

    /// <summary>Cặp có NGÀY YÊU CẦU cho trước: mã yêu cầu mở đầu bằng <c>yyMMdd</c> y như mã đơn (dữ liệu thật
    /// <c>2607280TS2VYAW3</c> = yêu cầu ngày 28/07). Mã ĐƠN cố tình để một ngày RẤT CŨ (17/06) để chứng minh luật
    /// KHÔNG còn nhìn vào mã đơn nữa.</summary>
    private static YeuCauTraHang CapYeuCau(string maYeuCau) => new("260617ANE669U9", maYeuCau);

    /// <summary>
    /// BA CẶP THẬT người dùng gửi 29/07 (tab "Đơn Trả hàng Hoàn tiền") — ca chốt của cả đợt sửa. Cửa sổ 20 ngày
    /// tính từ 29/07 (mốc 09/07): yêu cầu 28/07 và 21/07 GIỮ, yêu cầu 21/06 BỎ. Hai cặp giữ lại đều thuộc đơn mà
    /// app gần như chắc chắn đã dọn (đặt 25/07 và 15/07) — luật cũ đo trên ngày ĐẶT ĐƠN sẽ vứt cặp 15/07.
    /// </summary>
    [Fact]
    public void LocTheoCuaSo_BaCapThat_CuaSo20Ngay_Giu2_Bo1()
    {
        var cap = new[]
        {
            new YeuCauTraHang("260725JTBTAJVD", "2607280TS2VYAW3"),   // đặt 25/07, yêu cầu 28/07 — HTML thật
            new YeuCauTraHang("260715QNAP2587", "2607210QK4M8T21"),   // đặt 15/07, yêu cầu 21/07
            new YeuCauTraHang("260617ANE669U9", "2606210RB7XN9C4"),   // đặt 17/06, yêu cầu 21/06 — quá hạn
        };

        var kq = TraHangParser.LocTheoCuaSo(cap, HomNay, TraHangParser.SoNgayCuaSoTraHang);

        Assert.Equal(new[] { "260725JTBTAJVD", "260715QNAP2587" }, kq.GiuLai.Select(c => c.MaDon));
        Assert.Equal(1, kq.BoQuaViCu);
        Assert.Equal(0, kq.GiuViKhongRoNgay);
    }

    /// <summary>15 ngày chính sách Shopee + biên — hằng phải là 20 và là hằng RIÊNG, không dùng chung con số 7
    /// ngày của nhánh lấy bù "Số tiền cuối cùng" (khác trục, khác ý nghĩa).</summary>
    [Fact]
    public void SoNgayCuaSoTraHang_La20_KhongDungChungVoiSoNgayBuUocTinh()
        => Assert.Equal(20, TraHangParser.SoNgayCuaSoTraHang);

    [Fact]
    public void LocTheoCuaSo_YeuCauHomNay_Giu()
    {
        var kq = TraHangParser.LocTheoCuaSo(new[] { CapYeuCau("260729US91P2N2") }, HomNay, 20);

        Assert.Equal("260729US91P2N2", Assert.Single(kq.GiuLai).MaYeuCau);
        Assert.Equal(0, kq.BoQuaViCu);
        Assert.Equal(0, kq.GiuViKhongRoNgay);
    }

    [Fact]
    public void LocTheoCuaSo_YeuCauCuHonCuaSo_Bo()
    {
        var kq = TraHangParser.LocTheoCuaSo(new[] { CapYeuCau("260701ABCDEFG") }, HomNay, 20);

        Assert.Empty(kq.GiuLai);
        Assert.Equal(1, kq.BoQuaViCu);
    }

    /// <summary>⚠ Danh sách sắp theo ngày yêu cầu mới→cũ nhưng KHÔNG đơn điệu tuyệt đối (sắp xếp có thể không áp
    /// được). Dòng quá hạn nằm GIỮA hai dòng còn hạn → phải LỌC đúng dòng đó, KHÔNG được dừng sớm.</summary>
    [Fact]
    public void LocTheoCuaSo_DongCuNamGiua_ChiBoDungDongDo_KhongDungSom()
    {
        var kq = TraHangParser.LocTheoCuaSo(
            new[] { CapYeuCau("260729AAA"), CapYeuCau("260610BBB"), CapYeuCau("260728CCC") }, HomNay, 20);

        Assert.Equal(new[] { "260729AAA", "260728CCC" }, kq.GiuLai.Select(c => c.MaYeuCau));
        Assert.Equal(1, kq.BoQuaViCu);
    }

    /// <summary>Đúng biên <c>soNgay</c> ngày → VẪN giữ (biên ĐÓNG).</summary>
    [Fact]
    public void LocTheoCuaSo_DungBien_VanGiu()
    {
        var kq = TraHangParser.LocTheoCuaSo(new[] { CapYeuCau("260709ZZZ") }, HomNay, 20);

        Assert.Single(kq.GiuLai);
        Assert.Equal(0, kq.BoQuaViCu);
    }

    /// <summary>
    /// ĐỔI CHIỀU so với luật cũ: mã yêu cầu không suy được ngày thì <b>GIỮ</b> (đếm riêng để log), không bỏ. Mã
    /// yêu cầu chính là thứ ta cần lấy — thà thừa một mã còn hơn mất nó vì một khuôn mã lạ. (Luật cũ đo trên mã
    /// ĐƠN nên bỏ được: mã đơn chỉ dùng để suy ngày, không phải dữ liệu cần.)
    /// </summary>
    [Fact]
    public void LocTheoCuaSo_MaYeuCauKhongDocDuocNgay_GIU_DemRieng_KhongNem()
    {
        var kq = TraHangParser.LocTheoCuaSo(
            new[] { CapYeuCau("RMA-KHONG-CO-NGAY"), CapYeuCau("260729AAA") }, HomNay, 20);

        Assert.Equal(new[] { "RMA-KHONG-CO-NGAY", "260729AAA" }, kq.GiuLai.Select(c => c.MaYeuCau));
        Assert.Equal(0, kq.BoQuaViCu);
        Assert.Equal(1, kq.GiuViKhongRoNgay);
    }

    /// <summary>Mã ĐƠN cũ mèm (17/06) mà yêu cầu MỚI (28/07) → GIỮ. Đây chính là ca luật cũ vứt nhầm, và cũng là
    /// lý do số mã lấy được vẫn là 0 suốt các bản trước.</summary>
    [Fact]
    public void LocTheoCuaSo_DonRatCu_NhungYeuCauMoi_VanGiu()
    {
        var kq = TraHangParser.LocTheoCuaSo(
            new[] { new YeuCauTraHang("260617ANE669U9", "2607280TS2VYAW3") }, HomNay, 20);

        Assert.Single(kq.GiuLai);
        Assert.Equal(0, kq.BoQuaViCu);
    }

    [Fact]
    public void LocTheoCuaSo_RongHoacNull_TraRong_KhongNem()
    {
        Assert.Empty(TraHangParser.LocTheoCuaSo(Array.Empty<YeuCauTraHang>(), HomNay, 20).GiuLai);
        Assert.Empty(TraHangParser.LocTheoCuaSo(null, HomNay, 20).GiuLai);
    }
}
