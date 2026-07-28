using System.Text.Json;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test hàm thuần <see cref="PhanLoaiExtractor.TuItemsJson"/> — cột "Phân loại" của app, hub và payload
/// Google Sheet đều suy từ đây (một nguồn sự thật). Chuỗi mẫu lấy từ DỮ LIỆU THẬT trên hub production
/// (đuôi <c>[A322 A322]</c> là SKU lặp lại do Shopee gộp 2 dòng vào một ô <c>.item-description</c>).
/// </summary>
public class PhanLoaiExtractorTests
{
    /// <summary>Dựng <c>items_json</c> đúng dạng extension quét ra (<c>{name, variation, amount, image}</c>);
    /// phần tử <c>null</c> = sản phẩm THIẾU hẳn field <c>variation</c>.</summary>
    private static string Items(params string?[] variations)
        => "[" + string.Join(",", variations.Select(v => v is null
            ? "{\"name\":\"SP\",\"amount\":\"1\"}"
            : "{\"name\":\"SP\",\"variation\":" + JsonSerializer.Serialize(v) + ",\"amount\":\"1\"}")) + "]";

    // ===== Một sản phẩm: cắt đuôi SKU, bóc tiền tố, giữ nguyên phần phân loại =====
    [Theory]
    [InlineData("Nâu Be,39 [A322 A322]", "Nâu Be,39")]                          // đơn 260728T47N5KSS
    [InlineData("Kem,36 [A141 A141]", "Kem,36")]                                // đơn 260727S2R0097C
    [InlineData("Trắng sữa,36 [B80482 B80482]", "Trắng sữa,36")]                // đơn 260727S20VWQ0K
    [InlineData("Đen 9p-form chuẩn,37 [B21318 B21318]", "Đen 9p-form chuẩn,37")]
    [InlineData("KEM,38/39", "KEM,38/39")]                                       // không có ngoặc → giữ nguyên
    [InlineData("Phân loại:\u00A0KEM,38/39", "KEM,38/39")]                       // tiền tố tiếng Việt + &nbsp;
    [InlineData("phân loại: Nâu Be,39 [A322 A322]", "Nâu Be,39")]                // tiền tố thường/hoa lẫn lộn
    [InlineData("Variation: Kem,36 [A141 A141]", "Kem,36")]                      // tiền tố tiếng Anh (UI EN)
    [InlineData("Xanh [nhạt],38 [A9 A9]", "Xanh [nhạt],38")]                     // ngoặc GIỮA chuỗi → chỉ cắt ĐUÔI
    [InlineData("  Nâu Be,39 [A322 A322]  ", "Nâu Be,39")]                       // khoảng trắng thừa 2 đầu
    [InlineData("[A322 A322]", "")]                                              // chỉ còn SKU → coi như không có
    public void TuItemsJson_MotSanPham(string variation, string expected)
        => Assert.Equal(expected, PhanLoaiExtractor.TuItemsJson(Items(variation)));

    // ===== Nhiều sản phẩm: nối " · ", bỏ item thiếu variation, bỏ trùng lặp liên tiếp =====
    [Fact]
    public void TuItemsJson_HaiSanPham_NoiBangDauCham()
        => Assert.Equal("Nâu Be,39 · Kem,36",
            PhanLoaiExtractor.TuItemsJson(Items("Nâu Be,39 [A322 A322]", "Kem,36 [A141 A141]")));

    [Fact]
    public void TuItemsJson_ItemThieuVariation_BoQua()
        => Assert.Equal("Kem,36", PhanLoaiExtractor.TuItemsJson(Items(null, "Kem,36 [A141 A141]", "")));

    [Fact]
    public void TuItemsJson_TrungLapLienTiep_ChiGiuMot()
        => Assert.Equal("Kem,36", PhanLoaiExtractor.TuItemsJson(Items("Kem,36 [A141 A141]", "Kem,36 [A141 A141]")));

    // ===== Rác từ web: KHÔNG ném, trả chuỗi rỗng =====
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[]")]
    [InlineData("{")]                       // JSON hỏng (cụt)
    [InlineData("khong phai json")]
    [InlineData("{\"variation\":\"Kem,36\"}")]  // object chứ không phải mảng
    [InlineData("[1,2,\"x\"]")]                 // phần tử không phải object
    [InlineData("[{\"variation\":123}]")]       // variation không phải chuỗi
    public void TuItemsJson_RacHoacRong_TraChuoiRong(string? itemsJson)
        => Assert.Equal(string.Empty, PhanLoaiExtractor.TuItemsJson(itemsJson));

    // ===== SkuTuItemsJson: SKU từng sản phẩm nối " · ", KHÔNG khử trùng =====
    /// <summary>Dựng items_json có khóa sku (trang CHI TIẾT); phần tử null = thiếu hẳn field sku.</summary>
    private static string ItemsCoSku(params string?[] skus)
        => "[" + string.Join(",", skus.Select(s => s is null
            ? "{\"name\":\"SP\",\"variation\":\"X\",\"amount\":\"1\"}"
            : "{\"name\":\"SP\",\"sku\":" + JsonSerializer.Serialize(s) + ",\"phanLoai\":\"X\",\"amount\":\"1\"}")) + "]";

    [Fact]
    public void SkuTuItemsJson_HaiSanPham_NoiBangDauCham()
        => Assert.Equal("A521 · A357-Đen Full LOLITA-36",
            PhanLoaiExtractor.SkuTuItemsJson(ItemsCoSku("A521", "A357-Đen Full LOLITA-36")));

    [Fact]
    public void SkuTuItemsJson_DonCuKhongCoKhoaSku_TraChuoiRong()
        => Assert.Equal(string.Empty,
            PhanLoaiExtractor.SkuTuItemsJson(Items("Nâu Be,39 [A322 A322]", "Kem,36 [A141 A141]")));

    [Fact]
    public void SkuTuItemsJson_MotSanPham_KhongCoDauNoi()
        => Assert.Equal("A521", PhanLoaiExtractor.SkuTuItemsJson(ItemsCoSku("A521")));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("khong phai json")]
    public void SkuTuItemsJson_RacHoacRong_TraChuoiRong(string? itemsJson)
        => Assert.Equal(string.Empty, PhanLoaiExtractor.SkuTuItemsJson(itemsJson));

    [Fact]
    public void SkuTuItemsJson_ThieuSkuXenGiua_ChiNoiCacMaCo()
        => Assert.Equal("A521 · A357",
            PhanLoaiExtractor.SkuTuItemsJson(ItemsCoSku("A521", null, "A357")));
}
