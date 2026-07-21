using XuLyDonShopee.Core.Data;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Tests;

public class SettingsRepositoryTests
{
    [Fact]
    public void SetVaGetKiotProxyKeys_ChuanHoa_BoTrongVaTrung()
    {
        using var temp = new TempDatabase();
        var repo = new SettingsRepository(temp.Open());

        repo.SetKiotProxyKeys(new[] { " k1 ", "k1", "", "k2" });

        Assert.Equal(new[] { "k1", "k2" }, repo.GetKiotProxyKeys());
    }

    [Fact]
    public void GetKiotProxyKeys_ChuaLuu_TraVeRong()
    {
        using var temp = new TempDatabase();
        var repo = new SettingsRepository(temp.Open());

        Assert.Empty(repo.GetKiotProxyKeys());
    }

    [Fact]
    public void KiotProxyKeys_ConNguyen_SauKhiMoLaiDatabase()
    {
        using var temp = new TempDatabase();

        // Phiên 1: ghi.
        {
            var repo1 = new SettingsRepository(temp.Open());
            repo1.SetKiotProxyKeys(new[] { "k1", "k2" });
        }

        // Phiên 2: mở lại, dữ liệu còn nguyên.
        {
            var repo2 = new SettingsRepository(temp.Open());
            Assert.Equal(new[] { "k1", "k2" }, repo2.GetKiotProxyKeys());
        }
    }

    [Fact]
    public void GetBrowserChoice_ChuaDat_TraAuto()
    {
        using var temp = new TempDatabase();
        var repo = new SettingsRepository(temp.Open());

        Assert.Equal(BrowserChoice.Auto, repo.GetBrowserChoice());
    }

    [Theory]
    [InlineData(BrowserChoice.Auto)]
    [InlineData(BrowserChoice.Chrome)]
    [InlineData(BrowserChoice.Edge)]
    [InlineData(BrowserChoice.Brave)]
    [InlineData(BrowserChoice.BundledChromium)]
    public void SetVaGetBrowserChoice_Roundtrip(BrowserChoice choice)
    {
        using var temp = new TempDatabase();
        var repo = new SettingsRepository(temp.Open());

        repo.SetBrowserChoice(choice);

        Assert.Equal(choice, repo.GetBrowserChoice());
    }

    [Fact]
    public void BrowserChoice_ConNguyen_SauKhiMoLaiDatabase()
    {
        using var temp = new TempDatabase();

        // Phiên 1: ghi.
        {
            var repo1 = new SettingsRepository(temp.Open());
            repo1.SetBrowserChoice(BrowserChoice.Edge);
        }

        // Phiên 2: mở lại, dữ liệu còn nguyên.
        {
            var repo2 = new SettingsRepository(temp.Open());
            Assert.Equal(BrowserChoice.Edge, repo2.GetBrowserChoice());
        }
    }

    [Fact]
    public void GetSyncFreshProfile_ChuaDat_TraFalse()
    {
        using var temp = new TempDatabase();
        var repo = new SettingsRepository(temp.Open());

        Assert.False(repo.GetSyncFreshProfile());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SetVaGetSyncFreshProfile_Roundtrip(bool value)
    {
        using var temp = new TempDatabase();
        var repo = new SettingsRepository(temp.Open());

        repo.SetSyncFreshProfile(value);

        Assert.Equal(value, repo.GetSyncFreshProfile());
    }

    [Fact]
    public void SyncFreshProfile_ConNguyen_SauKhiMoLaiDatabase()
    {
        using var temp = new TempDatabase();

        // Phiên 1: bật cờ.
        {
            var repo1 = new SettingsRepository(temp.Open());
            repo1.SetSyncFreshProfile(true);
        }

        // Phiên 2: mở lại, cờ còn nguyên.
        {
            var repo2 = new SettingsRepository(temp.Open());
            Assert.True(repo2.GetSyncFreshProfile());
        }
    }
}
