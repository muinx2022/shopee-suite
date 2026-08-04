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
    /// phần tử <c>null</c> = sản phẩm THIẾU hẳn field <c>variation</c>. Mặc định <c>amount=1</c>.</summary>
    private static string Items(params string?[] variations)
        => "[" + string.Join(",", variations.Select(v => v is null
            ? "{\"name\":\"SP\",\"amount\":\"1\"}"
            : "{\"name\":\"SP\",\"variation\":" + JsonSerializer.Serialize(v) + ",\"amount\":\"1\"}")) + "]";

    private static string Item(string variation, string? amount)
        => amount is null
            ? "{\"name\":\"SP\",\"variation\":" + JsonSerializer.Serialize(variation) + "}"
            : "{\"name\":\"SP\",\"variation\":" + JsonSerializer.Serialize(variation)
              + ",\"amount\":" + JsonSerializer.Serialize(amount) + "}";

    // ===== Một sản phẩm: cắt đuôi SKU, bóc tiền tố, gắn . SL: 1 (Items luôn có amount=1) =====
    [Theory]
    [InlineData("Nâu Be,39 [A322 A322]", "Nâu Be,39. SL: 1")]
    [InlineData("Kem,36 [A141 A141]", "Kem,36. SL: 1")]
    [InlineData("Trắng sữa,36 [B80482 B80482]", "Trắng sữa,36. SL: 1")]
    [InlineData("Đen 9p-form chuẩn,37 [B21318 B21318]", "Đen 9p-form chuẩn,37. SL: 1")]
    [InlineData("KEM,38/39", "KEM,38/39. SL: 1")]
    [InlineData("Phân loại:\u00A0KEM,38/39", "KEM,38/39. SL: 1")]
    [InlineData("phân loại: Nâu Be,39 [A322 A322]", "Nâu Be,39. SL: 1")]
    [InlineData("Variation: Kem,36 [A141 A141]", "Kem,36. SL: 1")]
    [InlineData("Xanh [nhạt],38 [A9 A9]", "Xanh [nhạt],38. SL: 1")]
    [InlineData("  Nâu Be,39 [A322 A322]  ", "Nâu Be,39. SL: 1")]
    [InlineData("[A322 A322]", "")] // chỉ còn SKU → coi như không có (không gắn SL)
    public void TuItemsJson_MotSanPham(string variation, string expected)
        => Assert.Equal(expected, PhanLoaiExtractor.TuItemsJson(Items(variation)));

    [Fact]
    public void TuItemsJson_HaiSanPham_NoiBangDauCham()
        => Assert.Equal("Nâu Be,39. SL: 1 · Kem,36. SL: 1",
            PhanLoaiExtractor.TuItemsJson(Items("Nâu Be,39 [A322 A322]", "Kem,36 [A141 A141]")));

    [Fact]
    public void TuItemsJson_ItemThieuVariation_BoQua()
        => Assert.Equal("Kem,36. SL: 1", PhanLoaiExtractor.TuItemsJson(Items(null, "Kem,36 [A141 A141]", "")));

    [Fact]
    public void TuItemsJson_HaiDongGiongNhau_KhongKhuTrung_GiuSL()
        => Assert.Equal("Kem,36. SL: 1 · Kem,36. SL: 1",
            PhanLoaiExtractor.TuItemsJson(Items("Kem,36 [A141 A141]", "Kem,36 [A141 A141]")));

    [Fact]
    public void TuItemsJson_Amount2_GanSL2()
        => Assert.Equal("Nâu Be,39. SL: 2",
            PhanLoaiExtractor.TuItemsJson("[" + Item("Nâu Be,39 [A322 A322]", "2") + "]"));

    [Fact]
    public void TuItemsJson_ThieuAmount_KhongGanSL()
        => Assert.Equal("Nâu Be,39",
            PhanLoaiExtractor.TuItemsJson("[" + Item("Nâu Be,39 [A322 A322]", null) + "]"));

    [Fact]
    public void GanSoLuong_N1VaN2()
    {
        Assert.Equal("Kem,36. SL: 1", PhanLoaiExtractor.GanSoLuong("Kem,36", 1));
        Assert.Equal("Kem,36. SL: 2", PhanLoaiExtractor.GanSoLuong("Kem,36", 2));
        Assert.Equal("Kem,36", PhanLoaiExtractor.GanSoLuong("Kem,36", null));
        Assert.Equal("SL: 1", PhanLoaiExtractor.GanSoLuong("", 1));
    }

    // ===== Rác từ web: KHÔNG ném, trả chuỗi rỗng =====
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("khong phai json")]
    [InlineData("{\"variation\":\"Kem,36\"}")]
    [InlineData("[1,2,\"x\"]")]
    [InlineData("[{\"variation\":123}]")]
    public void TuItemsJson_RacHoacRong_TraChuoiRong(string? itemsJson)
        => Assert.Equal(string.Empty, PhanLoaiExtractor.TuItemsJson(itemsJson));

    // ===== SkuTuItemsJson =====
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
