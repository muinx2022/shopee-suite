using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

public class BrowserProfilePathsTests
{
    [Fact]
    public void ForAccount_KetThucBangProfilesVaId()
    {
        var path = BrowserProfilePaths.ForAccount("C:\\data", 7);
        var sep = System.IO.Path.DirectorySeparatorChar;

        Assert.EndsWith($"profiles{sep}7", path);
    }

    [Fact]
    public void ForAccount_HaiIdKhacNhau_DuongDanKhacNhau()
    {
        var a = BrowserProfilePaths.ForAccount("C:\\data", 1);
        var b = BrowserProfilePaths.ForAccount("C:\\data", 2);

        Assert.NotEqual(a, b);
    }
}
