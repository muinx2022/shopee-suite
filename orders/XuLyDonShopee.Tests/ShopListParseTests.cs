using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test hàm THUẦN <see cref="ShopeeLoginService.ParseShopListJson"/>: chuyển JSON mảng
/// <c>{rowKey,name,login}</c> (đọc từ bảng <c>/portal/shop</c> của Nền tảng tài khoản phụ) thành
/// <see cref="ShopListItem"/>. Bao: 3 dòng như DOM mẫu → 3 item đúng; dòng thiếu login → LoginName rỗng;
/// dòng thiếu rowKey → BỎ; mảng rỗng / JSON hỏng / null → list rỗng (KHÔNG ném); trim khoảng trắng.
/// </summary>
public class ShopListParseTests
{
    [Fact]
    public void ParseShopListJson_BaDong_TraDungBaItem()
    {
        // Mẫu theo DOM người dùng cung cấp: data-row-key = shop id, shop-name-text = tên shop, td thứ 2 = tên đăng nhập.
        const string json = @"[
            { ""rowKey"": ""1843718137"", ""name"": ""Alina Store1"", ""login"": ""alina99.store"" },
            { ""rowKey"": ""1843718200"", ""name"": ""Shop 9X"",     ""login"": ""shop9x.store"" },
            { ""rowKey"": ""1843718999"", ""name"": ""Cicily Shop"", ""login"": ""cicily.store"" }
        ]";

        var shops = ShopeeLoginService.ParseShopListJson(json);

        Assert.Equal(3, shops.Count);

        Assert.Equal("1843718137", shops[0].ShopId);
        Assert.Equal("Alina Store1", shops[0].ShopName);
        Assert.Equal("alina99.store", shops[0].LoginName);

        Assert.Equal("1843718200", shops[1].ShopId);
        Assert.Equal("Shop 9X", shops[1].ShopName);
        Assert.Equal("shop9x.store", shops[1].LoginName);

        Assert.Equal("1843718999", shops[2].ShopId);
        Assert.Equal("Cicily Shop", shops[2].ShopName);
        Assert.Equal("cicily.store", shops[2].LoginName);
    }

    [Fact]
    public void ParseShopListJson_DongThieuLogin_VanNhan_LoginRong()
    {
        const string json = @"[
            { ""rowKey"": ""111"", ""name"": ""Shop A"", ""login"": """" },
            { ""rowKey"": ""222"", ""name"": ""Shop B"" }
        ]";

        var shops = ShopeeLoginService.ParseShopListJson(json);

        Assert.Equal(2, shops.Count);
        Assert.Equal("111", shops[0].ShopId);
        Assert.Equal("", shops[0].LoginName);   // login rỗng
        Assert.Equal("222", shops[1].ShopId);
        Assert.Equal("", shops[1].LoginName);   // login thiếu → coi như rỗng (KHÔNG null)
    }

    [Fact]
    public void ParseShopListJson_DongThieuRowKey_BiBo()
    {
        // Dòng không có mã shop (rowKey rỗng / thiếu) → không định vị được để mở → BỎ; các dòng còn lại giữ.
        const string json = @"[
            { ""rowKey"": """",    ""name"": ""Không mã"", ""login"": ""x.store"" },
            { ""name"": ""Cũng không mã"", ""login"": ""y.store"" },
            { ""rowKey"": ""333"", ""name"": ""Shop C"", ""login"": ""c.store"" }
        ]";

        var shops = ShopeeLoginService.ParseShopListJson(json);

        var only = Assert.Single(shops);
        Assert.Equal("333", only.ShopId);
        Assert.Equal("c.store", only.LoginName);
    }

    [Fact]
    public void ParseShopListJson_TrimKhoangTrang()
    {
        const string json = @"[ { ""rowKey"": ""  444  "", ""name"": ""  Shop D  "", ""login"": ""  d.store  "" } ]";

        var only = Assert.Single(ShopeeLoginService.ParseShopListJson(json));
        Assert.Equal("444", only.ShopId);
        Assert.Equal("Shop D", only.ShopName);
        Assert.Equal("d.store", only.LoginName);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("không phải json")]
    [InlineData("{\"rowKey\":\"1\"}")] // object đơn (không phải mảng) → deserialize lỗi → rỗng
    public void ParseShopListJson_RongHoacHong_TraListRong_KhongNem(string? json)
    {
        var shops = ShopeeLoginService.ParseShopListJson(json);
        Assert.Empty(shops);
    }
}
