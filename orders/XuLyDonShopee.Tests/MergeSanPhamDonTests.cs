using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test luật GỘP payload <c>kind="finals"</c> của extension vào DTO đơn
/// (<see cref="OrdersBridgeSession.MergeFinalAmounts"/>): "Số tiền cuối cùng" như cũ, kèm danh sách SẢN PHẨM đọc
/// được TRONG CÙNG lần mở trang chi tiết. Điểm phải ghim: sản phẩm là phần THÊM — lỗi/rỗng ở đó KHÔNG được làm
/// mất số tiền cuối cùng, cũng KHÔNG được xoá mảng <c>items_json</c> quét ở trang danh sách.
/// </summary>
public class MergeSanPhamDonTests
{
    /// <summary><c>items_json</c> đời cũ (quét trang DANH SÁCH) — mốc để kiểm "không bị xoá".</summary>
    private const string ItemsCu =
        "[{\"name\":\"Giày A141\",\"variation\":\"Kem,36 [A141 A141]\",\"amount\":\"1\",\"image\":\"x\"}]";

    private static SyncedOrder Don(string sn = "SN1")
        => new() { OrderSn = sn, Status = "Chờ lấy hàng", ItemsJson = ItemsCu, ItemCount = 1, Sku = "A141" };

    private static int Gop(SyncedOrder don, string? finalsJson)
        => OrdersBridgeSession.MergeFinalAmounts(new[] { don }, finalsJson, _ => { });

    [Fact]
    public void CoSanPham_ThayCaMang_VaCapNhatItemCount()
    {
        var don = Don();
        var got = Gop(don, "[{\"orderSn\":\"SN1\",\"finalText\":\"₫292.010\",\"sanPham\":["
            + "{\"ten\":\"Giày A\",\"phanLoai\":\"Kem,36\",\"sku\":\"A141\",\"donGia\":\"303.050\",\"soLuong\":\"1\",\"thanhTien\":\"303.050\"},"
            + "{\"ten\":\"Giày B\",\"phanLoai\":\"Nâu Be,39\",\"sku\":\"A322\",\"donGia\":\"250.000\",\"soLuong\":\"2\",\"thanhTien\":\"500.000\"}]}]");

        Assert.Equal(1, got);
        Assert.Equal(292010, don.FinalAmount);
        Assert.Equal(2, don.ItemCount);
        Assert.Equal(new[] { "A141", "A322" }, SanPhamDonParser.Parse(don.ItemsJson).Select(x => x.Sku));
        // Khóa cũ còn nguyên → PhanLoaiExtractor (app + hub) vẫn đọc được.
        Assert.Equal("Kem,36 · Nâu Be,39", PhanLoaiExtractor.TuItemsJson(don.ItemsJson));
    }

    /// <summary>Trang chi tiết KHÔNG đọc được sản phẩm nào → giữ NGUYÊN mảng cũ, vẫn lấy được số tiền cuối cùng.</summary>
    [Theory]
    [InlineData("[{\"orderSn\":\"SN1\",\"finalText\":\"₫292.010\",\"sanPham\":[]}]")]
    [InlineData("[{\"orderSn\":\"SN1\",\"finalText\":\"₫292.010\"}]")]
    [InlineData("[{\"orderSn\":\"SN1\",\"finalText\":\"₫292.010\",\"sanPham\":[{}]}]")]
    public void KhongCoSanPham_GiuNguyenMangCu(string finalsJson)
    {
        var don = Don();
        Assert.Equal(1, Gop(don, finalsJson));
        Assert.Equal(292010, don.FinalAmount);
        Assert.Equal(ItemsCu, don.ItemsJson);
        Assert.Equal(1, don.ItemCount);
    }

    /// <summary>Đọc được sản phẩm nhưng KHÔNG có số tiền cuối cùng (poll hết giờ) → vẫn lưu sản phẩm, final để null.</summary>
    [Fact]
    public void CoSanPham_KhongCoFinal_VanLuuSanPham()
    {
        var don = Don();
        var got = Gop(don, "[{\"orderSn\":\"SN1\",\"finalText\":\"\",\"sanPham\":"
            + "[{\"ten\":\"Giày A\",\"phanLoai\":\"Kem,36\",\"sku\":\"A141\",\"soLuong\":\"1\"}]}]");

        Assert.Equal(0, got);
        Assert.Null(don.FinalAmount);
        Assert.Equal("A141", Assert.Single(SanPhamDonParser.Parse(don.ItemsJson)).Sku);
    }

    /// <summary>JSON rác / đơn không khớp mã → không đụng gì tới đơn (best-effort, KHÔNG ném).</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[{\"orderSn\":\"KHAC\",\"finalText\":\"₫1\",\"sanPham\":[{\"ten\":\"X\",\"sku\":\"B1\"}]}]")]
    public void RacHoacKhongKhop_KhongDoiGi(string? finalsJson)
    {
        var don = Don();
        Assert.Equal(0, Gop(don, finalsJson));
        Assert.Null(don.FinalAmount);
        Assert.Equal(ItemsCu, don.ItemsJson);
    }

    /// <summary>Danh sách bị cắt vì vượt trần / nhãn meta lạ → phải LOG (đừng nuốt im lặng).</summary>
    [Fact]
    public void BiCatVaMetaLa_DuocLog()
    {
        var nhatKy = new List<string>();
        OrdersBridgeSession.MergeFinalAmounts(
            new[] { Don() },
            "[{\"orderSn\":\"SN1\",\"finalText\":\"\",\"sanPham\":"
            + "[{\"ten\":\"SP\",\"sku\":\"A141\",\"metaLa\":[\"Bảo hành: 12 tháng\"],\"bicat\":true}]}]",
            nhatKy.Add);

        Assert.Contains(nhatKy, d => d.Contains("VƯỢT trần", StringComparison.Ordinal));
        Assert.Contains(nhatKy, d => d.Contains("Bảo hành: 12 tháng", StringComparison.Ordinal));
    }
}
