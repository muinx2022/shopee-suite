using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test hàm THUẦN <c>LoginParsers.ParseOrdersJson</c> (gọi qua forwarder
/// <see cref="ShopeeLoginService.ParseOrdersJson"/>): chuyển JSON mảng đơn (do <c>ScanOrdersJs</c> /
/// <c>pageScanOrders</c> của extension đọc từ DOM) thành <see cref="XuLyDonShopee.Core.Models.SyncedOrder"/>.
/// Đây là đường vào dữ liệu đơn của cầu nối extension nên phải LÌ ĐÒN: một phần tử lạ KHÔNG được phá cả danh
/// sách, JSON hỏng KHÔNG được ném. Bao: map đủ trường; đơn thiếu <c>orderSn</c> bị bỏ; items rỗng/thiếu →
/// ItemsJson "[]" + ItemCount 0; chuỗi rỗng → null (để cột DB NULL); JSON hỏng/rỗng/null → danh sách rỗng.
/// </summary>
public class ParseOrdersJsonTests
{
    [Fact]
    public void ParseOrdersJson_MotDonDayDu_MapDungMoiTruong()
    {
        const string json = @"[
            {
                ""orderSn"": ""260716T6NPV58S"",
                ""shopeeOrderId"": ""123456789"",
                ""buyer"": ""nguoimua01"",
                ""items"": [ { ""name"": ""Áo thun cotton B02435"", ""amount"": ""1"" } ],
                ""totalText"": ""₫166.500"",
                ""payment"": ""COD"",
                ""status"": ""Chờ lấy hàng"",
                ""statusDesc"": ""Chuẩn bị hàng trước 18/07"",
                ""cancelReason"": ""."",
                ""channel"": ""Nhanh"",
                ""carrier"": ""SPX Express"",
                ""tracking"": ""SPXVN0123456""
            }
        ]";

        var only = Assert.Single(ShopeeLoginService.ParseOrdersJson(json));

        Assert.Equal("260716T6NPV58S", only.OrderSn);
        Assert.Equal("123456789", only.ShopeeOrderId);
        Assert.Equal("nguoimua01", only.BuyerUsername);
        Assert.Equal(1, only.ItemCount);
        Assert.Equal("Áo thun cotton B02435", only.ItemSummary);
        Assert.Equal("B02435", only.Sku);              // SKU = chuỗi ASCII chữ/số cuối tên sản phẩm
        Assert.Equal("₫166.500", only.TotalPriceText);
        Assert.Equal(166500L, only.TotalPrice);        // parse bỏ mọi ký tự không phải số
        Assert.Equal("COD", only.PaymentMethod);
        Assert.Equal("Chờ lấy hàng", only.Status);
        Assert.Equal("Chuẩn bị hàng trước 18/07", only.StatusDescription);
        Assert.Equal("Nhanh", only.Channel);
        Assert.Equal("SPX Express", only.Carrier);
        Assert.Equal("SPXVN0123456", only.TrackingNumber);
    }

    [Fact]
    public void ParseOrdersJson_DonThieuMaDon_BiBo_DonConLaiGiuNguyen()
    {
        // Không có orderSn (thiếu hẳn / rỗng / toàn khoảng trắng) → không làm khóa upsert được → BỎ.
        const string json = @"[
            { ""buyer"": ""khong-co-ma"" },
            { ""orderSn"": """",   ""buyer"": ""ma-rong"" },
            { ""orderSn"": ""   "", ""buyer"": ""ma-toan-space"" },
            { ""orderSn"": ""ABC123"", ""buyer"": ""hop-le"" }
        ]";

        var only = Assert.Single(ShopeeLoginService.ParseOrdersJson(json));
        Assert.Equal("ABC123", only.OrderSn);
        Assert.Equal("hop-le", only.BuyerUsername);
    }

    [Fact]
    public void ParseOrdersJson_KhongCoItems_ItemsJsonRong_KhongNem()
    {
        // items thiếu hẳn / không phải mảng → giữ mặc định "[]" + ItemCount 0, ItemSummary/Sku null.
        const string json = @"[
            { ""orderSn"": ""D1"" },
            { ""orderSn"": ""D2"", ""items"": ""khong-phai-mang"" },
            { ""orderSn"": ""D3"", ""items"": [] }
        ]";

        var orders = ShopeeLoginService.ParseOrdersJson(json);

        Assert.Equal(3, orders.Count);
        Assert.All(orders, o =>
        {
            Assert.Equal("[]", o.ItemsJson);
            Assert.Equal(0, o.ItemCount);
            Assert.Null(o.ItemSummary);
            Assert.Null(o.Sku);
        });
    }

    [Fact]
    public void ParseOrdersJson_NhieuItem_GiuNguyenMangJson_TomTatLayItemDau()
    {
        const string json = @"[
            {
                ""orderSn"": ""E1"",
                ""items"": [
                    { ""name"": ""Quần jean B00777"" },
                    { ""name"": ""Mũ lưỡi trai B00888"" }
                ]
            }
        ]";

        var only = Assert.Single(ShopeeLoginService.ParseOrdersJson(json));

        Assert.Equal(2, only.ItemCount);
        Assert.Equal("Quần jean B00777", only.ItemSummary); // tóm tắt = tên item ĐẦU
        Assert.Equal("B00777", only.Sku);
        Assert.Contains("B00888", only.ItemsJson);          // vẫn giữ nguyên văn cả mảng items
    }

    [Fact]
    public void ParseOrdersJson_ChuoiRong_ThanhNull_DeCotDbDeNull()
    {
        const string json = @"[
            { ""orderSn"": ""F1"", ""buyer"": """", ""totalText"": """", ""payment"": ""  "", ""tracking"": """" }
        ]";

        var only = Assert.Single(ShopeeLoginService.ParseOrdersJson(json));

        Assert.Equal("F1", only.OrderSn);
        Assert.Null(only.BuyerUsername);
        Assert.Null(only.TotalPriceText);
        Assert.Null(only.TotalPrice);      // không có số → null
        Assert.Null(only.PaymentMethod);
        Assert.Null(only.TrackingNumber);
    }

    [Fact]
    public void ParseOrdersJson_TruongSaiKieu_BoQuaTruongDo_KhongNem()
    {
        // Property đúng tên nhưng SAI KIỂU (số/bool/object thay vì chuỗi) → đọc ra rỗng → null, đơn vẫn nhận.
        const string json = @"[
            { ""orderSn"": ""G1"", ""shopeeOrderId"": 987654, ""buyer"": true, ""status"": { ""x"": 1 } }
        ]";

        var only = Assert.Single(ShopeeLoginService.ParseOrdersJson(json));

        Assert.Equal("G1", only.OrderSn);
        Assert.Null(only.ShopeeOrderId);
        Assert.Null(only.BuyerUsername);
        Assert.Null(only.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("không phải json")]
    [InlineData("[]")]
    [InlineData("{\"orderSn\":\"X1\"}")] // object đơn (không phải mảng) → bỏ, KHÔNG ném
    public void ParseOrdersJson_RongHoacHong_TraListRong_KhongNem(string? json)
    {
        Assert.Empty(ShopeeLoginService.ParseOrdersJson(json));
    }
}
