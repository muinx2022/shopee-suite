using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

public class KiotProxyKeyParserTests
{
    [Fact]
    public void Parse_Null_TraVeRong()
    {
        Assert.Empty(KiotProxyKeyParser.Parse(null));
    }

    [Fact]
    public void Parse_ChuoiRong_TraVeRong()
    {
        Assert.Empty(KiotProxyKeyParser.Parse(""));
    }

    [Fact]
    public void Parse_Trim_BoDongTrong()
    {
        var keys = KiotProxyKeyParser.Parse(" k1 \n\n k2 ");
        Assert.Equal(new[] { "k1", "k2" }, keys);
    }

    [Fact]
    public void Parse_LoaiTrung_GiuThuTu()
    {
        var keys = KiotProxyKeyParser.Parse("k1\nk2\nk1");
        Assert.Equal(new[] { "k1", "k2" }, keys);
    }

    [Fact]
    public void Parse_Crlf_TachDung()
    {
        var keys = KiotProxyKeyParser.Parse("k1\r\nk2");
        Assert.Equal(new[] { "k1", "k2" }, keys);
    }

    [Fact]
    public void Join_GhepMoiDongMotKey()
    {
        Assert.Equal("k1\nk2", KiotProxyKeyParser.Join(new[] { "k1", "k2" }));
    }
}
