using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test hàm thuần <see cref="ShopeeDashboard.ParseToShipCount"/>: đọc số đơn từ text ô <c>item-title</c>
/// ("0"/số thường/có dấu ngăn nghìn/"99+"/rác), rỗng/null/không có chữ số → null.
/// </summary>
public class ShopeeDashboardTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("12", 12)]
    [InlineData("1.234", 1234)]   // dấu chấm ngăn nghìn bị loại
    [InlineData("1,234", 1234)]   // dấu phẩy ngăn nghìn bị loại
    [InlineData("99+", 99)]       // dấu "+" bị loại
    [InlineData("  42 ", 42)]     // khoảng trắng bị loại
    [InlineData("007", 7)]        // số 0 đứng đầu vẫn parse đúng
    public void CoChuSo_TraDungSo(string input, int expected)
    {
        Assert.Equal(expected, ShopeeDashboard.ParseToShipCount(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("+")]
    public void RongHoacKhongCoChuSo_TraNull(string input)
    {
        Assert.Null(ShopeeDashboard.ParseToShipCount(input));
    }

    [Fact]
    public void Null_TraNull()
    {
        Assert.Null(ShopeeDashboard.ParseToShipCount(null));
    }

    [Fact]
    public void SoQuaLon_TranInt_TraNull()
    {
        // Chuỗi số dài hơn phạm vi int → không parse được → null (không ném).
        Assert.Null(ShopeeDashboard.ParseToShipCount("99999999999999"));
    }
}
