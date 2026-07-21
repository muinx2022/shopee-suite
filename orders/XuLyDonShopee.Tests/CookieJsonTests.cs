using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test round-trip serialize/deserialize cookie và xử lý đầu vào hỏng của <see cref="CookieJson"/>.
/// </summary>
public class CookieJsonTests
{
    [Fact]
    public void RoundTrip_GiuNguyenGiaTri()
    {
        var cookies = new List<StoredCookie>
        {
            new("SPC_EC", "abc123", ".shopee.vn", "/", 1893456000, true, true, "Lax"),
            new("csrftoken", "xyz789", "banhang.shopee.vn", "/", 0, false, false, "None"),
            new("SPC_U", "999", ".shopee.vn", "/seller", 1800000000.5, true, false, null)
        };

        var json = CookieJson.Serialize(cookies);
        var back = CookieJson.Deserialize(json);

        Assert.Equal(cookies.Count, back.Count);
        for (var i = 0; i < cookies.Count; i++)
        {
            // StoredCookie là record → so sánh bằng giá trị toàn bộ các trường.
            Assert.Equal(cookies[i], back[i]);
        }
    }

    [Fact]
    public void Serialize_CoThutLe_DeDoc()
    {
        var json = CookieJson.Serialize(new[]
        {
            new StoredCookie("a", "b", ".shopee.vn", "/", 0, false, false, "Lax")
        });

        // Có thụt lề (WriteIndented) → chứa xuống dòng.
        Assert.Contains("\n", json);
        Assert.Contains("\"Name\"", json);
    }

    [Fact]
    public void Deserialize_Null_TraVeRong()
    {
        Assert.Empty(CookieJson.Deserialize(null));
    }

    [Fact]
    public void Deserialize_ChuoiRong_TraVeRong()
    {
        Assert.Empty(CookieJson.Deserialize(""));
        Assert.Empty(CookieJson.Deserialize("   "));
    }

    [Fact]
    public void Deserialize_ChuoiRac_TraVeRong_KhongNem()
    {
        Assert.Empty(CookieJson.Deserialize("{ this is not valid json ["));
        Assert.Empty(CookieJson.Deserialize("not json at all"));
    }
}
