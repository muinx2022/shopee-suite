using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test cho helper thuần <see cref="BrowserChoices"/>: parse/serialize, nhãn tiếng Việt, danh sách hiển thị.
/// </summary>
public class BrowserChoiceTests
{
    [Fact]
    public void Parse_Null_TraAuto()
    {
        Assert.Equal(BrowserChoice.Auto, BrowserChoices.Parse(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("khong-biet")]
    [InlineData("firefox")]
    public void Parse_TrongHoacLa_TraAuto(string value)
    {
        Assert.Equal(BrowserChoice.Auto, BrowserChoices.Parse(value));
    }

    [Theory]
    [InlineData("chrome", BrowserChoice.Chrome)]
    [InlineData("CHROME", BrowserChoice.Chrome)]
    [InlineData(" edge ", BrowserChoice.Edge)]
    [InlineData("Brave", BrowserChoice.Brave)]
    [InlineData("chromium", BrowserChoice.BundledChromium)]
    [InlineData("auto", BrowserChoice.Auto)]
    public void Parse_KhopKhongPhanBietHoaThuong(string value, BrowserChoice expected)
    {
        Assert.Equal(expected, BrowserChoices.Parse(value));
    }

    [Theory]
    [InlineData(BrowserChoice.Auto)]
    [InlineData(BrowserChoice.Chrome)]
    [InlineData(BrowserChoice.Edge)]
    [InlineData(BrowserChoice.Brave)]
    [InlineData(BrowserChoice.BundledChromium)]
    public void ToStorage_RoiParse_Roundtrip(BrowserChoice choice)
    {
        var storage = BrowserChoices.ToStorage(choice);

        Assert.Equal(choice, BrowserChoices.Parse(storage));
    }

    [Theory]
    [InlineData(BrowserChoice.Auto)]
    [InlineData(BrowserChoice.Chrome)]
    [InlineData(BrowserChoice.Edge)]
    [InlineData(BrowserChoice.Brave)]
    [InlineData(BrowserChoice.BundledChromium)]
    public void VnLabel_KhongRong(BrowserChoice choice)
    {
        Assert.False(string.IsNullOrWhiteSpace(BrowserChoices.VnLabel(choice)));
    }

    [Fact]
    public void All_Du5Muc_DungThuTu()
    {
        Assert.Equal(
            new[]
            {
                BrowserChoice.Auto,
                BrowserChoice.Chrome,
                BrowserChoice.Edge,
                BrowserChoice.Brave,
                BrowserChoice.BundledChromium
            },
            BrowserChoices.All);
    }
}
