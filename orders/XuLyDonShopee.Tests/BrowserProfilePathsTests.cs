using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

public class BrowserProfilePathsTests
{
    [Fact]
    public void ForAccount_KetThucBangProfilesIdVaKind()
    {
        var path = BrowserProfilePaths.ForAccount("C:\\data", 7, "chrome");
        var sep = System.IO.Path.DirectorySeparatorChar;

        Assert.EndsWith($"profiles{sep}7-chrome", path);
    }

    [Fact]
    public void ForAccount_HaiIdKhacNhau_DuongDanKhacNhau()
    {
        var a = BrowserProfilePaths.ForAccount("C:\\data", 1, "chrome");
        var b = BrowserProfilePaths.ForAccount("C:\\data", 2, "chrome");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ForAccount_ThemKindVaoTenThuMuc()
    {
        var path = BrowserProfilePaths.ForAccount("C:\\data", 12, "chrome");
        var sep = System.IO.Path.DirectorySeparatorChar;

        Assert.EndsWith($"profiles{sep}12-chrome", path);
    }

    [Fact]
    public void ForAccount_CungIdHaiKindKhacNhau_DuongDanKhacNhau()
    {
        var chrome = BrowserProfilePaths.ForAccount("C:\\data", 12, "chrome");
        var brave = BrowserProfilePaths.ForAccount("C:\\data", 12, "brave");

        Assert.NotEqual(chrome, brave);
        Assert.EndsWith("12-chrome", chrome);
        Assert.EndsWith("12-brave", brave);
    }

    [Fact]
    public void ForAccount_KindVietHoaVaSpace_ChuanHoaLowercase()
    {
        var path = BrowserProfilePaths.ForAccount("C:\\data", 5, "  BRAVE  ");
        var sep = System.IO.Path.DirectorySeparatorChar;

        Assert.EndsWith($"profiles{sep}5-brave", path);
    }
}
