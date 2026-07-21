using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

public class ProxyParserTests
{
    [Fact]
    public void Parse_HostPort_TraVeProxyHopLe()
    {
        var result = ProxyParser.Parse("1.2.3.4:8080");

        Assert.Single(result.Valid);
        Assert.Empty(result.Errors);
        var p = result.Valid[0];
        Assert.Equal("1.2.3.4", p.Host);
        Assert.Equal(8080, p.Port);
        Assert.Null(p.Username);
        Assert.Null(p.Password);
        Assert.Equal(ProxyType.Http, p.Type);
    }

    [Fact]
    public void Parse_HostPortUserPass_TraVeDayDu()
    {
        var result = ProxyParser.Parse("1.2.3.4:8080:myuser:mypass");

        Assert.Single(result.Valid);
        var p = result.Valid[0];
        Assert.Equal("1.2.3.4", p.Host);
        Assert.Equal(8080, p.Port);
        Assert.Equal("myuser", p.Username);
        Assert.Equal("mypass", p.Password);
    }

    [Fact]
    public void Parse_BoDongTrong_VaTrimKhoangTrang()
    {
        var text = "  1.2.3.4:8080  \n\n   \n5.6.7.8:3128\n";
        var result = ProxyParser.Parse(text);

        Assert.Equal(2, result.Valid.Count);
        Assert.Empty(result.Errors);
        Assert.Equal("1.2.3.4", result.Valid[0].Host);
        Assert.Equal("5.6.7.8", result.Valid[1].Host);
    }

    [Fact]
    public void Parse_PortKhongPhaiSo_BaoLoi()
    {
        var result = ProxyParser.Parse("1.2.3.4:abc");

        Assert.Empty(result.Valid);
        Assert.Single(result.Errors);
        Assert.Equal(1, result.Errors[0].LineNumber);
    }

    [Fact]
    public void Parse_ThieuPhan_BaoLoi()
    {
        // 1 phần (không có dấu :) và 3 phần đều sai định dạng.
        var result = ProxyParser.Parse("justhost\n1.2.3.4:8080:useronly");

        Assert.Empty(result.Valid);
        Assert.Equal(2, result.Errors.Count);
        Assert.Equal(1, result.Errors[0].LineNumber);
        Assert.Equal(2, result.Errors[1].LineNumber);
    }

    [Fact]
    public void Parse_HonHop_DungSoLuongVaSoDongLoi()
    {
        var text =
            "1.2.3.4:8080\n" +      // dòng 1: hợp lệ
            "badline\n" +           // dòng 2: lỗi
            "5.6.7.8:3128:u:p\n" +  // dòng 3: hợp lệ
            "9.9.9.9:99999\n";      // dòng 4: lỗi (port ngoài khoảng)

        var result = ProxyParser.Parse(text);

        Assert.Equal(2, result.Valid.Count);
        Assert.Equal(2, result.Errors.Count);
        Assert.Equal(2, result.Errors[0].LineNumber);
        Assert.Equal(4, result.Errors[1].LineNumber);
    }

    [Fact]
    public void Parse_ChuoiRong_TraVeRong()
    {
        var result = ProxyParser.Parse("   ");
        Assert.Empty(result.Valid);
        Assert.Empty(result.Errors);
    }
}
