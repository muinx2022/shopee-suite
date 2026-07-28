using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test PAYLOAD field <c>sheet2</c> (file Google Sheet PHỤ) đi qua chính thân
/// <see cref="HubOutbox.PushOrdersToGsheetAsync"/> + Web App Apps Script GIẢ trên loopback (dùng lại
/// <see cref="HubOutboxGsheetHuyTests.FakeGsheetWebApp"/>). Hợp đồng với script:
/// <list type="bullet">
/// <item>cấu hình là LINK bảng tính → client gửi ID đã BÓC (script khỏi parse lại);</item>
/// <item>cấu hình TRỐNG → <c>"sheet2":""</c> vẫn PHẢI có mặt: script cần phân biệt "người dùng tắt ghi file phụ"
/// với "client đời cũ chưa biết field này" (field vắng → script lùi về hằng dự phòng của nó).</item>
/// </list>
/// </summary>
public class HubOutboxGsheetSheet2Tests
{
    private const string IdSheet2 = "1CK-mu-rtLw0QnGDZ2cuEIkRelEnZkNWuB7Ir_ZuRLhk";

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

    private static Task<KetQuaDay> DayAsync(long accountId, AppServices services)
        => HubOutbox.PushOrdersToGsheetAsync(
            accountId, services, shopId: null, shopLogin: "alina99.store",
            nenBaoThieuGsheetUrl: () => false, imLangKhiKhongCoDonMoi: true, log: _ => { },
            ct: CancellationToken.None);

    /// <summary>Cấu hình là LINK đầy đủ (có đuôi <c>?usp=sharing</c>) → payload mang ID đã bóc, không phải URL thô.</summary>
    [Fact]
    public async Task CauHinhLaLinkDayDu_GuiIdDaBoc()
    {
        using var temp = new TempDatabase();
        using var web = new HubOutboxGsheetHuyTests.FakeGsheetWebApp();
        var (services, accId) = Dung(temp, web);
        services.Settings.SetGsheetSheet2($"https://docs.google.com/spreadsheets/d/{IdSheet2}/edit?usp=sharing");

        Assert.Equal(KetQuaDay.ThanhCong, await DayAsync(accId, services));

        var body = Assert.Single(web.Bodies);
        Assert.Contains($"\"sheet2\":\"{IdSheet2}\"", body);
        Assert.DoesNotContain("docs.google.com", body);   // gửi ID, KHÔNG gửi URL thô
    }

    /// <summary>Cấu hình là ID trần → gửi nguyên ID.</summary>
    [Fact]
    public async Task CauHinhLaIdTran_GuiNguyenId()
    {
        using var temp = new TempDatabase();
        using var web = new HubOutboxGsheetHuyTests.FakeGsheetWebApp();
        var (services, accId) = Dung(temp, web);
        services.Settings.SetGsheetSheet2(IdSheet2);

        Assert.Equal(KetQuaDay.ThanhCong, await DayAsync(accId, services));

        Assert.Contains($"\"sheet2\":\"{IdSheet2}\"", Assert.Single(web.Bodies));
    }

    /// <summary>CHƯA cấu hình → <c>"sheet2":""</c> vẫn CÓ MẶT (công tắc TẮT tường minh, không phải field vắng).</summary>
    [Fact]
    public async Task ChuaCauHinh_VanGuiSheet2Rong()
    {
        using var temp = new TempDatabase();
        using var web = new HubOutboxGsheetHuyTests.FakeGsheetWebApp();
        var (services, accId) = Dung(temp, web);
        // KHÔNG SetGsheetSheet2 — y máy chưa từng cấu hình file phụ.

        Assert.Equal(KetQuaDay.ThanhCong, await DayAsync(accId, services));

        Assert.Contains("\"sheet2\":\"\"", Assert.Single(web.Bodies));
    }
}
