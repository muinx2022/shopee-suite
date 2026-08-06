using Shopee.Core.BigSeller;
using Shopee.Core.Infrastructure;

namespace Shopee.Core.Tests;

/// <summary>
/// Hợp đồng đồng bộ của bộ 3 giá trị điền form Update (H3.3): tồn kho / cân nặng / kênh vận chuyển trở thành
/// cấu hình PER-SHOP do HUB sở hữu. Đây là lớp lỗi đã lặp 5 lần trong lịch sử repo ("field chung không vào
/// chữ ký" ⇄ "field riêng-máy lọt vào chữ ký"), nên khoá lại bằng test:
/// <list type="number">
/// <item>3 field NẰM TRONG <c>SharedSignature</c> ⇒ Hub sửa là client thấy lệch ⇒ có pull về;</item>
/// <item><c>MergeShopsKeepInstance</c> chép chúng TẠI CHỖ, GIỮ NGUYÊN object shop cũ ⇒ UI/VM đang cầm
/// reference thấy giá trị mới mà không cần restart (bẫy "shop mồ côi" + bẫy b13ed00);</item>
/// <item>cấu hình chạy riêng-máy trên object cũ KHÔNG bị bản Hub thổi bay.</item>
/// </list>
/// </summary>
public sealed class BigSellerShopUpdateFormSyncTests
{
    private static BigSellerAccount Acc(params BigSellerShop[] shops) =>
        new() { Id = "acc1", Label = "A", Shops = shops.ToList() };

    // ===== 1. Rỗng = dùng mặc định (đúng 3 hằng cũ trong runner) =====
    [Fact]
    public void Rong_DungMacDinh_BangDungHangCu()
    {
        Assert.Equal("30069", BigSellerShop.DefaultUpdateStock);
        Assert.Equal("500", BigSellerShop.DefaultUpdateWeight);
        Assert.Equal("Nhanh", BigSellerShop.DefaultUpdateShippingChannel);

        var s = new BigSellerShop();   // shop mới tinh: 3 field để trống
        Assert.Equal("30069", BigSellerShop.OrDefault(s.UpdateStockValue, BigSellerShop.DefaultUpdateStock));
        Assert.Equal("500", BigSellerShop.OrDefault(s.UpdateWeightValue, BigSellerShop.DefaultUpdateWeight));
        Assert.Equal("Nhanh", BigSellerShop.OrDefault(s.UpdateShippingChannel, BigSellerShop.DefaultUpdateShippingChannel));

        // Chuỗi toàn khoảng trắng cũng là "chưa đặt"; có giá trị thì trim.
        Assert.Equal("30069", BigSellerShop.OrDefault("   ", BigSellerShop.DefaultUpdateStock));
        Assert.Equal("50000", BigSellerShop.OrDefault(" 50000 ", BigSellerShop.DefaultUpdateStock));
    }

    /// <summary>bigseller.json CŨ (chưa có 3 field) → deserialize ra rỗng ⇒ vẫn chạy đúng 30069/500/Nhanh.</summary>
    [Fact]
    public void JsonCu_ThieuField_VanChay30069_500_Nhanh()
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<BigSellerShop>(
            """{"Id":"s1","Name":"Shop A","ShopeeDataSheet":"SheetA"}""")!;
        Assert.Equal("", s.UpdateStockValue);
        Assert.Equal("30069", BigSellerShop.OrDefault(s.UpdateStockValue, BigSellerShop.DefaultUpdateStock));
        Assert.Equal("500", BigSellerShop.OrDefault(s.UpdateWeightValue, BigSellerShop.DefaultUpdateWeight));
        Assert.Equal("Nhanh", BigSellerShop.OrDefault(s.UpdateShippingChannel, BigSellerShop.DefaultUpdateShippingChannel));
    }

    // ===== 2. 3 field là field CHUNG ⇒ PHẢI có trong chữ ký sync =====
    [Theory]
    [InlineData("stock")]
    [InlineData("weight")]
    [InlineData("ship")]
    public void DoiGiaTriTrenHub_LamLechChuKySync(string field)
    {
        var a = Acc(new BigSellerShop { Id = "s1", Name = "Shop A" });
        var truoc = BackupService.SharedSignature(a);

        var s = a.Shops[0];
        switch (field)
        {
            case "stock": s.UpdateStockValue = "50000"; break;
            case "weight": s.UpdateWeightValue = "1200"; break;
            case "ship": s.UpdateShippingChannel = "Tiết kiệm"; break;
        }

        Assert.NotEqual(truoc, BackupService.SharedSignature(a));
    }

    // ===== 3. Merge Hub → client: chép TẠI CHỖ, giữ nguyên object shop cũ =====
    [Fact]
    public void MergeTuHub_ChepTaiCho_GiuNguyenObjectShopCu()
    {
        var cu = new BigSellerShop
        {
            Id = "s1", Name = "Shop A", ShopeeDataSheet = "SheetA",
            // cấu hình chạy LEGACY riêng-máy còn nằm trên shop — không được bị bản Hub thổi bay
            ListingReloadSeconds = 45,
        };
        var tuHub = new BigSellerShop
        {
            Id = "s1", Name = "Shop A (đổi tên)", ShopeeDataSheet = "SheetA",
            UpdateStockValue = "50000", UpdateWeightValue = "1200", UpdateShippingChannel = "Tiết kiệm",
        };

        var merged = BackupService.MergeShopsKeepInstance([cu], [tuHub]);

        var shop = Assert.Single(merged);
        Assert.Same(cu, shop);                       // ĐÚNG object cũ → VM đang cầm reference thấy ngay, khỏi restart
        Assert.Equal("50000", shop.UpdateStockValue);
        Assert.Equal("1200", shop.UpdateWeightValue);
        Assert.Equal("Tiết kiệm", shop.UpdateShippingChannel);
        Assert.Equal("Shop A (đổi tên)", shop.Name);
        Assert.Equal(45, shop.ListingReloadSeconds);  // field riêng-máy trên object cũ vẫn còn
    }

    /// <summary>Hub XOÁ giá trị (về rỗng) cũng phải lan xuống client — nếu chỉ chép khi "bản Hub khác rỗng"
    /// thì admin không bao giờ đưa shop về mặc định được.</summary>
    [Fact]
    public void HubXoaVeRong_ClientVeMacDinh()
    {
        var cu = new BigSellerShop { Id = "s1", UpdateStockValue = "50000", UpdateShippingChannel = "Tiết kiệm" };
        var tuHub = new BigSellerShop { Id = "s1" };   // admin xoá trắng 3 ô trên Hub

        var shop = Assert.Single(BackupService.MergeShopsKeepInstance([cu], [tuHub]));
        Assert.Same(cu, shop);
        Assert.Equal("", shop.UpdateStockValue);
        Assert.Equal("", shop.UpdateShippingChannel);
        Assert.Equal("30069", BigSellerShop.OrDefault(shop.UpdateStockValue, BigSellerShop.DefaultUpdateStock));
    }

    /// <summary>Shop MỚI từ Hub (client chưa có) → nhận nguyên bản, kể cả 3 field.</summary>
    [Fact]
    public void ShopMoiTuHub_NhanNguyenBoBaField()
    {
        var tuHub = new BigSellerShop { Id = "s9", UpdateStockValue = "7", UpdateWeightValue = "8", UpdateShippingChannel = "Hoả tốc" };
        var shop = Assert.Single(BackupService.MergeShopsKeepInstance([], [tuHub]));
        Assert.Equal("7", shop.UpdateStockValue);
        Assert.Equal("8", shop.UpdateWeightValue);
        Assert.Equal("Hoả tốc", shop.UpdateShippingChannel);
    }
}
