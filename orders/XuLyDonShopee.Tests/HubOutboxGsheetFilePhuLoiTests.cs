using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test client hiện CẢNH BÁO khi Apps Script trả <c>filePhu.loi</c> — lỗi ghi file phụ KHÔNG làm hỏng
/// đường file chính (lượt đẩy vẫn <see cref="KetQuaDay.ThanhCong"/>).
/// </summary>
public class HubOutboxGsheetFilePhuLoiTests
{
    private static (AppServices Services, long AccountId) Dung(TempDatabase temp, HubOutboxGsheetHuyTests.FakeGsheetWebApp web)
    {
        var services = new AppServices(temp.Path);
        var accountId = services.Accounts.Insert(new Account { Email = "shop-test@example.com" });
        services.Settings.SetGsheetWebAppUrl(web.Url);
        services.Orders.UpsertMany(accountId, new[]
        {
            new SyncedOrder { OrderSn = "DON1", Status = "Chờ lấy hàng", TotalPrice = 166500 },
        }, DateTime.UtcNow);
        return (services, accountId);
    }

    private static Task<KetQuaDay> DayAsync(long accountId, AppServices services, Action<string> log)
        => HubOutbox.PushOrdersToGsheetAsync(
            accountId, services, shopId: null, shopLogin: "alina99.store",
            nenBaoThieuGsheetUrl: () => false, imLangKhiKhongCoDonMoi: true, log: log,
            ct: CancellationToken.None);

    [Fact]
    public async Task FilePhuLoi_LogCanhBao_VanThanhCong()
    {
        using var temp = new TempDatabase();
        using var web = new HubOutboxGsheetHuyTests.FakeGsheetWebApp();
        web.FilePhuLoi = "Exception: You do not have permission to access the requested document.";
        var (services, accId) = Dung(temp, web);
        var logs = new List<string>();

        Assert.Equal(KetQuaDay.ThanhCong, await DayAsync(accId, services, logs.Add));

        Assert.Contains(logs, m => m.Contains("file phụ", StringComparison.OrdinalIgnoreCase)
            && m.Contains("permission", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logs, m => m.StartsWith("GSheet: thêm", StringComparison.Ordinal)); // file chính vẫn ok
    }

    [Fact]
    public async Task KhongCoFilePhuLoi_KhongLogCanhBaoFilePhu()
    {
        using var temp = new TempDatabase();
        using var web = new HubOutboxGsheetHuyTests.FakeGsheetWebApp();
        // FilePhuLoi = null (mặc định) → phản hồi không có filePhu
        var (services, accId) = Dung(temp, web);
        var logs = new List<string>();

        Assert.Equal(KetQuaDay.ThanhCong, await DayAsync(accId, services, logs.Add));

        Assert.DoesNotContain(logs, m => m.Contains("file phụ", StringComparison.OrdinalIgnoreCase));
    }

    // ===== DocFilePhuLoi (hàm thuần) =====
    [Fact]
    public void DocFilePhuLoi_CoLoi_TraChuoi()
        => Assert.Equal("openById failed",
            GoogleSheetSyncService.DocFilePhuLoi(
                "{\"results\":[],\"filePhu\":{\"ghi\":0,\"them\":0,\"loi\":\"openById failed\"}}"));

    [Theory]
    [InlineData("{\"results\":[]}")]
    [InlineData("{\"results\":[],\"filePhu\":{\"ghi\":1,\"them\":1,\"loi\":null}}")]
    [InlineData("{\"results\":[],\"filePhu\":{\"ghi\":0,\"them\":0,\"loi\":\"\"}}")]
    [InlineData("not json")]
    public void DocFilePhuLoi_KhongLoi_TraNull(string json)
        => Assert.Null(GoogleSheetSyncService.DocFilePhuLoi(json));
}
