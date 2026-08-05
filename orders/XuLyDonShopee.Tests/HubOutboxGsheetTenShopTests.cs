using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Cột F "Shop" phải là TÊN ĐĂNG NHẬP SHOP CỦA TỪNG ĐƠN, không phải một tên chung cho cả lô.
/// <para>
/// Ca hỏng thật: <c>HubOutboxWorker</c> đẩy BÙ gọi <see cref="HubOutbox.PushOrdersToGsheetAsync"/> với
/// <c>shopId: null, shopLogin: null</c> — tức gom MỌI shop của tài khoản vào MỘT lượt. Trước khi sửa, tên shop
/// được tính MỘT lần cho cả lượt và rơi về <c>Accounts.GetById(accountId).Email</c> (email subaccount) rồi gán
/// cho mọi dòng ⇒ mọi đơn của mọi shop lên sheet với cột Shop = email đăng nhập subaccount.
/// </para>
/// Chạy qua chính thân hàm + Web App Apps Script GIẢ trên loopback (dùng lại
/// <see cref="HubOutboxGsheetHuyTests.FakeGsheetWebApp"/>).
/// </summary>
public class HubOutboxGsheetTenShopTests
{
    private const string EmailSubaccount = "subacc-chung@example.com";

    private static (AppServices Services, long AccountId) Dung(TempDatabase temp, HubOutboxGsheetHuyTests.FakeGsheetWebApp web)
    {
        var services = new AppServices(temp.Path);
        var accountId = services.Accounts.Insert(new Account { Email = EmailSubaccount });
        services.Settings.SetGsheetWebAppUrl(web.Url);
        return (services, accountId);
    }

    private static SyncedOrder Don(string sn)
        => new() { OrderSn = sn, Status = "Chờ lấy hàng", TotalPrice = 100_000 };

    /// <summary>Đường WORKER (shopId/shopLogin = null): hai đơn của HAI shop khác nhau trong CÙNG tài khoản →
    /// mỗi dòng mang đúng <c>shop_login</c> của đơn đó, KHÔNG dòng nào mang email subaccount.</summary>
    [Fact]
    public async Task DuongWorker_MoiDonLayTenShopCuaChinhNo_KhongPhaiEmail()
    {
        using var temp = new TempDatabase();
        using var web = new HubOutboxGsheetHuyTests.FakeGsheetWebApp();
        var (services, accId) = Dung(temp, web);

        // Hai shop khác nhau, cùng một tài khoản subaccount (mô hình lặp nhiều shop).
        services.Orders.UpsertMany(accId, new[] { Don("SN_A") }, DateTime.UtcNow,
            shopId: "SHOP_A", shopLogin: "alina99.store");
        services.Orders.UpsertMany(accId, new[] { Don("SN_B") }, DateTime.UtcNow,
            shopId: "SHOP_B", shopLogin: "mexico1.vn");

        // ĐÚNG cách HubOutboxWorker gọi: không biết shop nào, gom cả tài khoản.
        var kq = await HubOutbox.PushOrdersToGsheetAsync(
            accId, services, shopId: null, shopLogin: null,
            nenBaoThieuGsheetUrl: () => false, imLangKhiKhongCoDonMoi: true, log: _ => { },
            ct: CancellationToken.None);
        Assert.Equal(KetQuaDay.ThanhCong, kq);

        var body = Assert.Single(web.Bodies);
        Assert.Contains("\"maDon\":\"SN_A\"", body);
        Assert.Contains("\"maDon\":\"SN_B\"", body);
        Assert.Contains("\"tenShop\":\"alina99.store\"", body);
        Assert.Contains("\"tenShop\":\"mexico1.vn\"", body);
        // Bẫy chính: email subaccount KHÔNG được xuất hiện ở bất kỳ đâu trong payload.
        Assert.DoesNotContain(EmailSubaccount, body);
    }

    /// <summary>Đơn CŨ chưa gắn shop (<c>shop_login</c> NULL) trên đường worker → vẫn giữ fallback cũ
    /// (email tài khoản), không ném và không để trống cột Shop.</summary>
    [Fact]
    public async Task DuongWorker_DonCuChuaGanShop_VanFallbackEmail()
    {
        using var temp = new TempDatabase();
        using var web = new HubOutboxGsheetHuyTests.FakeGsheetWebApp();
        var (services, accId) = Dung(temp, web);

        services.Orders.UpsertMany(accId, new[] { Don("SN_CU") }, DateTime.UtcNow); // shop_login NULL

        var kq = await HubOutbox.PushOrdersToGsheetAsync(
            accId, services, shopId: null, shopLogin: null,
            nenBaoThieuGsheetUrl: () => false, imLangKhiKhongCoDonMoi: true, log: _ => { },
            ct: CancellationToken.None);
        Assert.Equal(KetQuaDay.ThanhCong, kq);

        var body = Assert.Single(web.Bodies);
        Assert.Contains("\"tenShop\":\"" + EmailSubaccount + "\"", body);
    }

    /// <summary>HỒI QUY đường đẩy theo SHOP (vòng lặp shop truyền shopId + shopLogin): hành vi KHÔNG đổi —
    /// dòng mang đúng tên shop của vòng lặp, đơn shop khác không lọt vào lô.</summary>
    [Fact]
    public async Task DuongTheoShop_GiuNguyenHanhVi()
    {
        using var temp = new TempDatabase();
        using var web = new HubOutboxGsheetHuyTests.FakeGsheetWebApp();
        var (services, accId) = Dung(temp, web);

        services.Orders.UpsertMany(accId, new[] { Don("SN_A") }, DateTime.UtcNow,
            shopId: "SHOP_A", shopLogin: "alina99.store");
        services.Orders.UpsertMany(accId, new[] { Don("SN_B") }, DateTime.UtcNow,
            shopId: "SHOP_B", shopLogin: "mexico1.vn");

        var kq = await HubOutbox.PushOrdersToGsheetAsync(
            accId, services, shopId: "SHOP_A", shopLogin: "alina99.store",
            nenBaoThieuGsheetUrl: () => false, imLangKhiKhongCoDonMoi: true, log: _ => { },
            ct: CancellationToken.None);
        Assert.Equal(KetQuaDay.ThanhCong, kq);

        var body = Assert.Single(web.Bodies);
        Assert.Contains("\"maDon\":\"SN_A\"", body);
        Assert.DoesNotContain("\"maDon\":\"SN_B\"", body);
        Assert.Contains("\"tenShop\":\"alina99.store\"", body);
        Assert.DoesNotContain("mexico1.vn", body);
    }
}
