using System.Text.Json;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test hàm thuần <see cref="SanPhamDonParser"/> — danh sách SẢN PHẨM đọc ở TRANG CHI TIẾT đơn (extension
/// <c>pageReadOrderProducts</c>). Giá trị mẫu lấy từ HTML THẬT người dùng gửi (đơn 1 sản phẩm, SKU <c>A141</c>,
/// phân loại <c>Kem,36</c>, đơn giá/thành tiền <c>303.050</c>).
/// <para>
/// <b>Đường NHIỀU sản phẩm chưa từng chạy trên trang thật</b> (17 đơn trong DB đều 1 sản phẩm) — test ở đây là
/// chỗ dựa duy nhất, nên viết theo đúng hình dạng payload extension trả.
/// </para>
/// </summary>
public class SanPhamDonParserTests
{
    /// <summary>Một phần tử payload extension: mọi field là TEXT THÔ đúng như <c>pageReadOrderProducts</c> đẩy về.</summary>
    private static string Sp(string ten, string phanLoai, string sku, string donGia, string soLuong, string thanhTien)
        => "{\"stt\":\"1\",\"ten\":" + J(ten) + ",\"phanLoai\":" + J(phanLoai) + ",\"sku\":" + J(sku)
           + ",\"donGia\":" + J(donGia) + ",\"soLuong\":" + J(soLuong) + ",\"thanhTien\":" + J(thanhTien)
           + ",\"anh\":\"https://cf.shopee.vn/file/x\",\"metaLa\":[]}";

    private static string J(string s) => JsonSerializer.Serialize(s);

    /// <summary>Payload một sản phẩm ĐÚNG như HTML thật ở plan (đã qua <c>norm</c> của extension).</summary>
    private const string MotSanPhamThat =
        "[{\"stt\":\"1\",\"ten\":\"Giày Boots Da Nữ Cổ Ngắn - A141\",\"phanLoai\":\"Kem,36\",\"sku\":\"A141\","
        + "\"donGia\":\"303.050\",\"soLuong\":\"1\",\"thanhTien\":\"303.050\","
        + "\"anh\":\"https://cf.shopee.vn/file/abc\",\"metaLa\":[]}]";

    // ===== Một sản phẩm: đủ 6 giá trị, đúng HTML thật =====

    [Fact]
    public void Parse_MotSanPham_DuGiaTri()
    {
        var sp = Assert.Single(SanPhamDonParser.Parse(MotSanPhamThat));
        Assert.Equal("Giày Boots Da Nữ Cổ Ngắn - A141", sp.Ten);
        Assert.Equal("Kem,36", sp.PhanLoai);
        Assert.Equal("A141", sp.Sku);
        Assert.Equal(303050, sp.DonGia);
        Assert.Equal(1, sp.SoLuong);
        Assert.Equal(303050, sp.ThanhTien);
        Assert.Equal(1, sp.Stt);
        Assert.Empty(sp.MetaLa);
        Assert.False(sp.BiCat);
    }

    /// <summary>BẪY: nhãn "SKU phân loại" CHỨA chuỗi "phân loại". Extension tách nhãn ở trang, nhưng nếu luật
    /// trượt thì giá trị SKU sẽ nằm ở ô phân loại — test ghim rằng hai ô KHÔNG lẫn nhau.</summary>
    [Fact]
    public void Parse_SkuKhongChuiVaoPhanLoai()
    {
        var sp = Assert.Single(SanPhamDonParser.Parse(MotSanPhamThat));
        Assert.Equal("Kem,36", sp.PhanLoai);
        Assert.DoesNotContain("A141", sp.PhanLoai);
    }

    /// <summary><c>&amp;nbsp;</c> (U+00A0) sau dấu hai chấm → khoảng trắng thường + trim.</summary>
    [Fact]
    public void Parse_Nbsp_DuocDon()
    {
        var sp = Assert.Single(SanPhamDonParser.Parse("[" + Sp("SP", "\u00A0Kem,36", "A141", "303.050", "1", "303.050") + "]"));
        Assert.Equal("Kem,36", sp.PhanLoai);
    }

    // ===== Nhiều sản phẩm: đủ số lượng, ĐÚNG thứ tự =====

    [Fact]
    public void Parse_NhieuSanPham_DuVaDungThuTu()
    {
        var json = "[" + Sp("Giày A", "Kem,36", "A141", "303.050", "1", "303.050") + ","
                       + Sp("Giày B", "Nâu Be,39", "A322", "250.000", "2", "500.000") + ","
                       + Sp("Giày C", "Đen,37", "B80482", "199.000", "1", "199.000") + "]";
        var list = SanPhamDonParser.Parse(json);
        Assert.Equal(3, list.Count);
        Assert.Equal(new[] { "A141", "A322", "B80482" }, list.Select(x => x.Sku));
        Assert.Equal(new[] { "Kem,36", "Nâu Be,39", "Đen,37" }, list.Select(x => x.PhanLoai));
        Assert.Equal(2, list[1].SoLuong);
        Assert.Equal(500000, list[1].ThanhTien);
    }

    /// <summary>Số lượng có tiền tố <c>x</c>/<c>×</c> (một số bản giao diện) → vẫn ra số.</summary>
    [Theory]
    [InlineData("1", 1)]
    [InlineData("x2", 2)]
    [InlineData("×3", 3)]
    [InlineData("", null)]
    [InlineData("nhiều", null)]
    public void Parse_SoLuong(string soLuong, int? mong)
    {
        var sp = Assert.Single(SanPhamDonParser.Parse("[" + Sp("SP", "Kem,36", "A141", "1", soLuong, "1") + "]"));
        Assert.Equal(mong, sp.SoLuong);
    }

    /// <summary>Giá text lạ → null, KHÔNG ném (dữ liệu từ web phải chịu rác).</summary>
    [Theory]
    [InlineData("--", null)]
    [InlineData("", null)]
    [InlineData("₫303.050", 303050L)]
    public void Parse_DonGiaTextLa(string donGia, long? mong)
    {
        var sp = Assert.Single(SanPhamDonParser.Parse("[" + Sp("SP", "Kem,36", "A141", donGia, "1", "1") + "]"));
        Assert.Equal(mong, sp.DonGia);
    }

    // ===== Rác từ web: danh sách RỖNG, KHÔNG ném =====

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[]")]
    [InlineData("{")]                                   // JSON hỏng (cụt)
    [InlineData("khong phai json")]
    [InlineData("{\"sku\":\"A141\"}")]                  // object chứ không phải mảng
    [InlineData("[1,2,\"x\"]")]                         // phần tử không phải object
    [InlineData("[{}]")]                                // thiếu CẢ ten, sku, phanLoai → dòng rác
    [InlineData("[{\"donGia\":\"303.050\"}]")]          // chỉ có giá → dòng rác
    public void Parse_RacHoacRong_TraDanhSachRong(string? json)
        => Assert.Empty(SanPhamDonParser.Parse(json));

    /// <summary>Dòng rác nằm GIỮA → bỏ đúng dòng đó, các dòng còn lại vẫn ra.</summary>
    [Fact]
    public void Parse_DongRacOGiua_ChiBoDongDo()
    {
        var json = "[" + Sp("Giày A", "Kem,36", "A141", "1", "1", "1") + ",{\"donGia\":\"9\"},"
                       + Sp("Giày B", "Nâu Be,39", "A322", "1", "1", "1") + "]";
        Assert.Equal(new[] { "A141", "A322" }, SanPhamDonParser.Parse(json).Select(x => x.Sku));
    }

    /// <summary>Nhãn meta lạ + cờ cắt được giữ NGUYÊN để caller log (không nuốt im lặng).</summary>
    [Fact]
    public void Parse_MetaLaVaBiCat_DuocGiuLai()
    {
        var json = "[{\"ten\":\"SP\",\"sku\":\"A141\",\"metaLa\":[\"Bảo hành: 12 tháng\",\"\"],\"bicat\":true}]";
        var sp = Assert.Single(SanPhamDonParser.Parse(json));
        Assert.Equal(new[] { "Bảo hành: 12 tháng" }, sp.MetaLa);
        Assert.True(sp.BiCat);
    }

    // ===== items_json: tương thích ngược + đọc lại được =====

    /// <summary>Mảng ghi ra vẫn có ĐỦ 4 khóa cũ (hub + <see cref="PhanLoaiExtractor"/> đang đọc) và thêm 4 khóa mới.</summary>
    [Fact]
    public void TaoItemsJson_GiuKhoaCu_ThemKhoaMoi()
    {
        var json = SanPhamDonParser.TaoItemsJson(SanPhamDonParser.Parse(MotSanPhamThat));
        using var doc = JsonDocument.Parse(json);
        var item = Assert.Single(doc.RootElement.EnumerateArray().ToList());
        Assert.Equal("Giày Boots Da Nữ Cổ Ngắn - A141", item.GetProperty("name").GetString());
        Assert.Equal("Kem,36", item.GetProperty("variation").GetString());
        Assert.Equal("1", item.GetProperty("amount").GetString());
        Assert.Equal("https://cf.shopee.vn/file/abc", item.GetProperty("image").GetString());
        Assert.Equal("Kem,36", item.GetProperty("phanLoai").GetString());
        Assert.Equal("A141", item.GetProperty("sku").GetString());
        Assert.Equal(303050, item.GetProperty("donGia").GetInt64());
        Assert.Equal(303050, item.GetProperty("thanhTien").GetInt64());
    }

    /// <summary>HỒI QUY: <c>items_json</c> mới vẫn đọc được bằng <see cref="PhanLoaiExtractor.TuItemsJson"/> như cũ.</summary>
    [Fact]
    public void TaoItemsJson_PhanLoaiExtractor_VanDocDuoc()
    {
        var motSp = SanPhamDonParser.TaoItemsJson(SanPhamDonParser.Parse(MotSanPhamThat));
        Assert.Equal("Kem,36", PhanLoaiExtractor.TuItemsJson(motSp));

        var haiSp = SanPhamDonParser.TaoItemsJson(SanPhamDonParser.Parse(
            "[" + Sp("Giày A", "Kem,36", "A141", "1", "1", "1") + "," + Sp("Giày B", "Nâu Be,39", "A322", "1", "1", "1") + "]"));
        Assert.Equal("Kem,36 · Nâu Be,39", PhanLoaiExtractor.TuItemsJson(haiSp));
    }

    /// <summary>Đọc lại chính chuỗi mình ghi ra (items_json dùng khóa <c>name</c>/<c>amount</c>/<c>image</c>).</summary>
    [Fact]
    public void Parse_DocLaiItemsJsonDaGhi()
    {
        var json = SanPhamDonParser.TaoItemsJson(SanPhamDonParser.Parse(MotSanPhamThat));
        var sp = Assert.Single(SanPhamDonParser.Parse(json));
        Assert.Equal("Giày Boots Da Nữ Cổ Ngắn - A141", sp.Ten);
        Assert.Equal("A141", sp.Sku);
        Assert.Equal(1, sp.SoLuong);
        Assert.Equal(303050, sp.DonGia);
    }

    // ===== Hai cột Google Sheet: cùng số dòng, khớp cặp =====

    /// <summary>Đơn 1 sản phẩm → KHÔNG xuống dòng, KHÔNG có "×1" (giữ đúng định dạng ~30 dòng đã có trên sheet).</summary>
    [Fact]
    public void CotGsheet_MotSanPham_KhongXuongDong()
    {
        var cot = SanPhamDonParser.CotGsheet(SanPhamDonParser.TaoItemsJson(SanPhamDonParser.Parse(MotSanPhamThat)));
        Assert.NotNull(cot);
        Assert.Equal("A141", cot!.Sku);
        Assert.Equal("Kem,36", cot.PhanLoai);
    }

    /// <summary>Hai sản phẩm → hai cột CÙNG số dòng; số lượng ≥2 mới gắn "×N".</summary>
    [Fact]
    public void CotGsheet_HaiSanPham_KhopCap()
    {
        var json = SanPhamDonParser.TaoItemsJson(SanPhamDonParser.Parse(
            "[" + Sp("Giày A", "Kem,36", "A141", "303.050", "1", "303.050") + ","
                + Sp("Giày B", "Nâu Be,39", "A322", "250.000", "2", "500.000") + "]"));
        var cot = SanPhamDonParser.CotGsheet(json);
        Assert.NotNull(cot);
        Assert.Equal("A141\nA322", cot!.Sku);
        Assert.Equal("Kem,36\nNâu Be,39 ×2", cot.PhanLoai);
        Assert.Equal(cot.Sku.Split('\n').Length, cot.PhanLoai.Split('\n').Length);
    }

    /// <summary>Sản phẩm GIỮA thiếu SKU → cột SKU có DÒNG TRỐNG đúng chỗ, hai cột vẫn bằng số dòng.</summary>
    [Fact]
    public void CotGsheet_ThieuSkuOGiua_DeDongTrong()
    {
        var json = SanPhamDonParser.TaoItemsJson(SanPhamDonParser.Parse(
            "[" + Sp("Giày A", "Kem,36", "A141", "1", "1", "1") + ","
                + Sp("Giày B", "Nâu Be,39", "", "1", "1", "1") + ","
                + Sp("Giày C", "Đen,37", "B80482", "1", "1", "1") + "]"));
        var cot = SanPhamDonParser.CotGsheet(json);
        Assert.NotNull(cot);
        Assert.Equal("A141\n\nB80482", cot!.Sku);
        Assert.Equal("Kem,36\nNâu Be,39\nĐen,37", cot.PhanLoai);
        Assert.Equal(3, cot.Sku.Split('\n').Length);
        Assert.Equal(3, cot.PhanLoai.Split('\n').Length);
    }

    /// <summary>HỒI QUY: <c>items_json</c> đời cũ (quét trang DANH SÁCH — không có khóa sku/phanLoai) → <c>null</c>
    /// để caller giữ NGUYÊN đường cũ, KHÔNG gửi chuỗi rỗng đè ô đang có trên sheet.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("[]")]
    [InlineData("khong phai json")]
    [InlineData("[{\"name\":\"Giày\",\"variation\":\"Kem,36 [A141 A141]\",\"amount\":\"1\",\"image\":\"x\"}]")]
    public void CotGsheet_KhongCoDuLieuTrangChiTiet_TraNull(string? itemsJson)
        => Assert.Null(SanPhamDonParser.CotGsheet(itemsJson));

    /// <summary>
    /// <b>Payload NGUYÊN VĂN</b> mà <c>pageReadOrderProducts</c> đọc ra từ HTML THẬT của một đơn <b>3 sản phẩm</b>
    /// trên trang chi tiết Shopee (người dùng gửi 28/07/2026) — chạy qua jsdom trên chính thân hàm trong
    /// <c>background.js</c>, không chép tay. Đây là mỏ neo cho đường NHIỀU SẢN PHẨM: trước khi có mẫu này, đường
    /// đó chưa từng chạy trên dữ liệu thật (17 đơn production đều 1 SP).
    /// </summary>
    private const string BaSanPhamThat =
        "["
        + "{\"stt\":\"1\",\"ten\":\"Áo Khoác Dù Nữ - Áo Gió Nhiều Màu Mũ Dây Rút Chắn Gió Chống Nắng Hàn Quốc Năng Động B41608\",\"phanLoai\":\"Màu Hồng (gió nhăn),L\",\"sku\":\"B41608\","
        + "\"donGia\":\"324.000\",\"soLuong\":\"1\",\"thanhTien\":\"324.000\",\"anh\":\"https://cf.shopee.vn/file/sg-11134201-8259d-mql9lr71nt3i88_tn\",\"metaLa\":[]}"
        + ","
        + "{\"stt\":\"2\",\"ten\":\"Áo Chống Nắng Nam Nỉ - Áo Khoác Nắng Khóa 2 Vai Sọc Viền Phong Cách Trẻ Trung Mùa Hè B52246\",\"phanLoai\":\"xl\",\"sku\":\"B52246\","
        + "\"donGia\":\"313.000\",\"soLuong\":\"1\",\"thanhTien\":\"313.000\",\"anh\":\"https://cf.shopee.vn/file/sg-11134201-825a2-mqlhx6iso7in82_tn\",\"metaLa\":[]}"
        + ","
        + "{\"stt\":\"3\",\"ten\":\"Áo Khoác Gió Phối Màu - Áo Gió Kẻ Vạch Trắng Logo Ngực Form Rộng Nam Nữ Sang Chảnh B21913\",\"phanLoai\":\"xám,m\",\"sku\":\"B21913\","
        + "\"donGia\":\"345.000\",\"soLuong\":\"1\",\"thanhTien\":\"345.000\",\"anh\":\"https://cf.shopee.vn/file/sg-11134201-8257u-mqkhdzwh3dhna6_tn\",\"metaLa\":[]}"
        + "]";

    /// <summary>Đơn 3 SP thật → đọc ĐỦ 3, đúng thứ tự STT, SKU/phân loại/tiền khớp trang.</summary>
    [Fact]
    public void Parse_BaSanPhamThatTuTrangChiTiet_RaDu3()
    {
        var sp = SanPhamDonParser.Parse(BaSanPhamThat);

        Assert.Equal(3, sp.Count);
        Assert.Equal(new[] { "B41608", "B52246", "B21913" }, sp.Select(x => x.Sku).ToArray());
        // Phân loại có DẤU NGOẶC ĐƠN ở giữa — luật cắt đuôi "[SKU SKU]" tuyệt đối không được đụng vào.
        Assert.Equal("Màu Hồng (gió nhăn),L", sp[0].PhanLoai);
        Assert.Equal("xl", sp[1].PhanLoai);
        Assert.Equal("xám,m", sp[2].PhanLoai);
        Assert.Equal(new long?[] { 324000, 313000, 345000 }, sp.Select(x => x.ThanhTien).ToArray());
        Assert.All(sp, x => Assert.Equal(1, x.SoLuong));
    }

    /// <summary>Kiểm chứng ĐỘC LẬP: tổng thành tiền 3 SP phải bằng đúng "Tổng tiền sản phẩm ₫982.000" mà Shopee
    /// tự in trên trang — sai một dòng hoặc parse hụt số là con số này lệch ngay.</summary>
    [Fact]
    public void Parse_BaSanPhamThat_TongThanhTienKhopTrangShopee()
        => Assert.Equal(982_000, SanPhamDonParser.Parse(BaSanPhamThat).Sum(x => x.ThanhTien ?? 0));

    // ===== Chọn bản items_json khi upsert: bản NGHÈO không được đè bản GIÀU =====

    /// <summary>Bản GIÀU: <c>items_json</c> đọc ở TRANG CHI TIẾT — đủ 8 khóa (đơn thật lúc 08:17).</summary>
    private static readonly string ItemsGiau = SanPhamDonParser.TaoItemsJson(SanPhamDonParser.Parse(MotSanPhamThat));

    /// <summary>Bản NGHÈO: <c>items_json</c> quét ở TRANG DANH SÁCH — chỉ 4 khóa (CHÍNH đơn đó lúc 09:46, sau khi
    /// bị đè: <c>[amount, image, name, variation]</c>).</summary>
    private const string ItemsNgheo =
        "[{\"name\":\"Giày Boots Da Nữ Cổ Ngắn - A141\",\"variation\":\"Kem,36 [A141 A141]\",\"amount\":\"1\",\"image\":\"x\"}]";

    /// <summary>HỒI QUY (lỗi THẬT 28/07/2026): vòng sync sau chỉ đọc trang DANH SÁCH → bản nghèo KHÔNG được đè bản
    /// giàu, kẻo mất SKU/phân loại vĩnh viễn (đơn đã có ước tính không được mở lại trang chi tiết).</summary>
    [Fact]
    public void ChonItemsJson_CuGiau_MoiNgheo_GiuCu()
        => Assert.Equal(ItemsGiau, SanPhamDonParser.ChonItemsJson(ItemsGiau, ItemsNgheo));

    [Fact]
    public void ChonItemsJson_CuNgheo_MoiGiau_LayMoi()
        => Assert.Equal(ItemsGiau, SanPhamDonParser.ChonItemsJson(ItemsNgheo, ItemsGiau));

    /// <summary>Cả hai đều GIÀU → lấy bản MỚI (dữ liệu mới nhất luôn thắng).</summary>
    [Fact]
    public void ChonItemsJson_CaHaiGiau_LayMoi()
    {
        var moi = SanPhamDonParser.TaoItemsJson(SanPhamDonParser.Parse(BaSanPhamThat));
        Assert.Equal(moi, SanPhamDonParser.ChonItemsJson(ItemsGiau, moi));
    }

    /// <summary>Cả hai đều NGHÈO → lấy bản mới (giữ hành vi cũ: bản quét danh sách mới nhất là hiện trạng đơn).</summary>
    [Fact]
    public void ChonItemsJson_CaHaiNgheo_LayMoi()
    {
        const string moi = "[{\"name\":\"Giày\",\"variation\":\"ĐEN,37\",\"amount\":\"2\",\"image\":\"y\"}]";
        Assert.Equal(moi, SanPhamDonParser.ChonItemsJson(ItemsNgheo, moi));
    }

    /// <summary>Bản mới không có sản phẩm nào (null/rỗng/"[]"/rác) → GIỮ bản cũ, đừng xóa dữ liệu bằng rỗng.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[]")]
    [InlineData("{")]                 // JSON hỏng ở bản MỚI → chọn bản đọc được (cũ)
    [InlineData("[{}]")]              // toàn dòng rác
    public void ChonItemsJson_MoiRongHoacRac_GiuCu(string? moi)
        => Assert.Equal(ItemsGiau, SanPhamDonParser.ChonItemsJson(ItemsGiau, moi));

    /// <summary>Chưa có bản cũ (đơn mới / cột NULL) → lấy bản mới, kể cả bản nghèo.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{")]                 // JSON hỏng ở bản CŨ → chọn bản đọc được (mới)
    public void ChonItemsJson_CuRongHoacRac_LayMoi(string? cu)
        => Assert.Equal(ItemsNgheo, SanPhamDonParser.ChonItemsJson(cu, ItemsNgheo));

    /// <summary>Cả hai đều không đọc được → trả bản mới (không ném, không dựng dữ liệu từ hư không).</summary>
    [Fact]
    public void ChonItemsJson_CaHaiRac_LayMoi()
    {
        Assert.Equal("[]", SanPhamDonParser.ChonItemsJson("{", "[]"));
        Assert.Null(SanPhamDonParser.ChonItemsJson(null, null));
    }

    /// <summary>Đơn 3 SP thật → hai cột GSheet mỗi cột 3 DÒNG, khớp cặp theo dòng; SL đều bằng 1 nên KHÔNG có "×N".</summary>
    [Fact]
    public void CotGsheet_BaSanPhamThat_HaiCotBaDongKhopCap()
    {
        var items = SanPhamDonParser.TaoItemsJson(SanPhamDonParser.Parse(BaSanPhamThat));

        var cot = SanPhamDonParser.CotGsheet(items);

        Assert.NotNull(cot);
        Assert.Equal("B41608\nB52246\nB21913", cot!.Sku);
        Assert.Equal("Màu Hồng (gió nhăn),L\nxl\nxám,m", cot.PhanLoai);
        Assert.DoesNotContain("×", cot.PhanLoai, StringComparison.Ordinal); // SL = 1 → không gắn hậu tố
    }
}
