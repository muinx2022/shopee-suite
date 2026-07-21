using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Nhận biết cookie đăng nhập Shopee: chỉ coi là đã đăng nhập khi có SPC_EC/SPC_ST/SPC_U có giá trị,
/// KHÔNG nhầm với các cookie theo dõi tiền-đăng-nhập (SPC_F, SPC_CDS...).
/// </summary>
public class ShopeeLoginCookiesTests
{
    private static string Json(params StoredCookie[] cookies) => CookieJson.Serialize(cookies);

    private static StoredCookie C(string name, string value)
        => new(name, value, ".shopee.vn", "/", 0, true, true, "Lax");

    [Fact]
    public void CoSpcEcCoGiaTri_TraTrue()
    {
        var json = Json(C("SPC_F", "x"), C("SPC_EC", "abc123"));
        Assert.True(ShopeeLoginCookies.IsLoggedIn(json));
    }

    [Fact]
    public void ChiCoCookieTheoDoi_TraFalse()
    {
        var json = Json(C("SPC_F", "x"), C("SPC_CDS", "y"), C("csrftoken", "z"));
        Assert.False(ShopeeLoginCookies.IsLoggedIn(json));
    }

    [Fact]
    public void SpcEcGiaTriRong_TraFalse()
    {
        var json = Json(C("SPC_EC", ""), C("SPC_F", "x"));
        Assert.False(ShopeeLoginCookies.IsLoggedIn(json));
    }

    [Fact]
    public void JsonRongHoacNull_TraFalse()
    {
        Assert.False(ShopeeLoginCookies.IsLoggedIn(""));
        Assert.False(ShopeeLoginCookies.IsLoggedIn((string?)null));
    }
}
