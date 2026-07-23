using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test hàm THUẦN <see cref="AutoRunService.LaTrangThaiChuaXacNhan"/>: phân loại trạng thái trang cuối lượt
/// autorun thành "TK chưa xác nhận được". Verify/LoginForm/Captcha → true (còn kẹt, chưa đăng nhập được);
/// LoggedIn/Unknown → false (không kết luận bừa).
/// </summary>
public class AutoRunUnverifiedTests
{
    [Theory]
    [InlineData(ShopeePageState.Verify)]
    [InlineData(ShopeePageState.LoginForm)]
    [InlineData(ShopeePageState.Captcha)]
    public void LaTrangThaiChuaXacNhan_TrangChan_TraVeTrue(ShopeePageState state)
    {
        Assert.True(AutoRunService.LaTrangThaiChuaXacNhan(state));
    }

    [Theory]
    [InlineData(ShopeePageState.LoggedIn)]
    [InlineData(ShopeePageState.Unknown)]
    public void LaTrangThaiChuaXacNhan_DaDangNhapHoacKhongRo_TraVeFalse(ShopeePageState state)
    {
        Assert.False(AutoRunService.LaTrangThaiChuaXacNhan(state));
    }
}
