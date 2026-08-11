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

    // Ba test về 'browser_choice' (đọc mặc định · roundtrip · còn nguyên sau khi mở lại DB) đã BỎ 11/08/2026
    // cùng với chính tính năng chọn trình duyệt: app chỉ chạy Brave — xem BrowserLocator + BraveRequirement,
    // và ChiChayBraveTests thay chỗ.

    [Fact]
    public void GetGsheetTabName_ChuaDat_TraChuoiRong()
    {
        using var temp = new TempDatabase();
        var repo = new SettingsRepository(temp.Open());

        // Trống = tự động theo tháng (caller tự resolve) → KHÔNG còn trả "tháng 4".
        Assert.Equal(string.Empty, repo.GetGsheetTabName());
    }

    [Fact]
    public void SetVaGetGsheetTabName_CoGiaTri_TraLaiDaTrim()
    {
        using var temp = new TempDatabase();
        var repo = new SettingsRepository(temp.Open());

        repo.SetGsheetTabName("  Tab Cua Toi  ");

        Assert.Equal("Tab Cua Toi", repo.GetGsheetTabName());
    }

    [Fact]
    public void SetGsheetTabName_ChuoiTrang_XoaKey_TraChuoiRong()
    {
        using var temp = new TempDatabase();
        var repo = new SettingsRepository(temp.Open());

        repo.SetGsheetTabName("Tab X");
        repo.SetGsheetTabName("   ");    // toàn khoảng trắng → xóa key

        Assert.Equal(string.Empty, repo.GetGsheetTabName());
    }

    [Fact]
    public void GetGsheetSheet2_ChuaDat_TraNull()
    {
        using var temp = new TempDatabase();
        var repo = new SettingsRepository(temp.Open());

        Assert.Null(repo.GetGsheetSheet2());   // chưa đặt = KHÔNG ghi file phụ
    }

    [Fact]
    public void SetVaGetGsheetSheet2_CoGiaTri_TraLaiDaTrim()
    {
        using var temp = new TempDatabase();
        var repo = new SettingsRepository(temp.Open());

        repo.SetGsheetSheet2("  https://docs.google.com/spreadsheets/d/ABC  ");

        Assert.Equal("https://docs.google.com/spreadsheets/d/ABC", repo.GetGsheetSheet2());
    }

    [Fact]
    public void SetGsheetSheet2_ChuoiTrang_XoaKey_TraNull()
    {
        using var temp = new TempDatabase();
        var repo = new SettingsRepository(temp.Open());

        repo.SetGsheetSheet2("ABC");
        repo.SetGsheetSheet2("   ");   // toàn khoảng trắng → xóa key (⇒ tắt ghi file phụ)

        Assert.Null(repo.GetGsheetSheet2());
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

    [Fact]
    public void NotifyWebhook_Legacy_MigrateSangDonMoiVaLoiApp()
    {
        using var temp = new TempDatabase();
        var repo = new SettingsRepository(temp.Open());
        repo.Set("notify_webhook_url", "https://hooks.slack.com/services/T/B/legacy");

        Assert.Equal("https://hooks.slack.com/services/T/B/legacy", repo.GetNotifyWebhookUrlDonMoi());
        Assert.Equal("https://hooks.slack.com/services/T/B/legacy", repo.GetNotifyWebhookUrlLoiApp());
        Assert.Null(repo.GetNotifyWebhookUrlDonTra());
    }

    [Fact]
    public void NotifyWebhook_BaODocLap_SauKhiSet()
    {
        using var temp = new TempDatabase();
        var repo = new SettingsRepository(temp.Open());
        repo.Set("notify_webhook_url", "https://hooks.slack.com/services/T/B/legacy");

        repo.SetNotifyWebhookUrls(
            "https://hooks.slack.com/services/T/B/donmoi",
            "https://hooks.slack.com/services/T/B/loi",
            "https://hooks.slack.com/services/T/B/tra");

        Assert.Equal("https://hooks.slack.com/services/T/B/donmoi", repo.GetNotifyWebhookUrlDonMoi());
        Assert.Equal("https://hooks.slack.com/services/T/B/loi", repo.GetNotifyWebhookUrlLoiApp());
        Assert.Equal("https://hooks.slack.com/services/T/B/tra", repo.GetNotifyWebhookUrlDonTra());
        // Legacy đã xóa — không còn fallback.
        Assert.Null(repo.Get("notify_webhook_url"));
    }

    [Fact]
    public void NotifyWebhook_ChiSetLoiApp_KhongFallbackLegacyChoDonMoi()
    {
        using var temp = new TempDatabase();
        var repo = new SettingsRepository(temp.Open());
        repo.Set("notify_webhook_url", "https://hooks.slack.com/services/T/B/legacy");
        repo.SetNotifyWebhookUrls(null, "https://hooks.slack.com/services/T/B/loi", null);

        Assert.Null(repo.GetNotifyWebhookUrlDonMoi());
        Assert.Equal("https://hooks.slack.com/services/T/B/loi", repo.GetNotifyWebhookUrlLoiApp());
        Assert.Null(repo.GetNotifyWebhookUrlDonTra());
    }
}
