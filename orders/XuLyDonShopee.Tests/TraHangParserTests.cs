using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test các hàm THUẦN của bước "check đơn trả hàng" (bước CUỐI flow mỗi shop):
/// <list type="bullet">
/// <item><see cref="TraHangParser.QuyetDinhCheck"/> — 4 nhánh luật đếm (lần đầu / không đổi / giảm / tăng k).</item>
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
}
