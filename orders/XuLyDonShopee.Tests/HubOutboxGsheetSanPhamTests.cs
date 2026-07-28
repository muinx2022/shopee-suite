using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test PAYLOAD Google Sheet hai cột "Phân loại" (K) + "Mã Sp" (M) khi <c>items_json</c> mang dữ liệu TRANG CHI
/// TIẾT: hai cột phải dựng từ CÙNG một danh sách sản phẩm nên KHỚP CẶP theo dòng. Chạy qua chính thân
/// <see cref="HubOutbox.PushOrdersToGsheetAsync"/> + Web App Apps Script GIẢ trên loopback (dùng lại
/// <see cref="HubOutboxGsheetHuyTests.FakeGsheetWebApp"/>) — quyết định nằm trong thân hàm, không tách kiến trúc
/// chỉ để test.
/// </summary>
public class HubOutboxGsheetSanPhamTests
{
    private static (AppServices Services, long AccountId) Dung(TempDatabase temp, HubOutboxGsheetHuyTests.FakeGsheetWebApp web)
    {
        var services = new AppServices(temp.Path);
        var accountId = services.Accounts.Insert(new Account { Email = "shop-test@example.com" });
        services.Settings.SetGsheetWebAppUrl(web.Url);
        return (services, accountId);
    }

    private static Task<KetQuaDay> DayAsync(long accountId, AppServices services)
        => HubOutbox.PushOrdersToGsheetAsync(
            accountId, services, shopId: null, shopLogin: "alina99.store",
            nenBaoThieuGsheetUrl: () => false, imLangKhiKhongCoDonMoi: true, log: _ => { },
            ct: CancellationToken.None);

    /// <summary>Đơn HAI sản phẩm đọc từ trang chi tiết → cột K và M CÙNG số dòng, số lượng ≥2 mới có "×N".</summary>
    [Fact]
    public async Task HaiSanPham_HaiCotKhopCapTheoDong()
    {
        using var temp = new TempDatabase();
        using var web = new HubOutboxGsheetHuyTests.FakeGsheetWebApp();
        var (services, accId) = Dung(temp, web);

        services.Orders.UpsertMany(accId, new[]
        {
            new SyncedOrder
            {
                OrderSn = "HAISP",
                Status = "Chờ lấy hàng",
                TotalPrice = 803050,
                Sku = "A141", // SKU ĐOÁN từ tên (đường cũ) — phải bị cột dựng từ sản phẩm thay thế
                ItemCount = 2,
                ItemsJson = "[{\"name\":\"Giày A\",\"variation\":\"Kem,36\",\"amount\":\"1\",\"image\":\"\","
                            + "\"phanLoai\":\"Kem,36\",\"sku\":\"A141\",\"donGia\":303050,\"thanhTien\":303050},"
                            + "{\"name\":\"Giày B\",\"variation\":\"Nâu Be,39\",\"amount\":\"2\",\"image\":\"\","
                            + "\"phanLoai\":\"Nâu Be,39\",\"sku\":\"A322\",\"donGia\":250000,\"thanhTien\":500000}]",
            },
        }, DateTime.UtcNow);

        Assert.Equal(KetQuaDay.ThanhCong, await DayAsync(accId, services));

        var body = Assert.Single(web.Bodies);
        Assert.Contains("\"phanLoai\":\"Kem,36\\nNâu Be,39 ×2\"", body);
        Assert.Contains("\"sku\":\"A141\\nA322\"", body);
    }

    /// <summary>HỒI QUY: đơn chỉ có <c>items_json</c> đời cũ (quét trang DANH SÁCH) → payload Y NHƯ TRƯỚC —
    /// phân loại qua <see cref="XuLyDonShopee.Core.Services.PhanLoaiExtractor"/> (cắt đuôi <c>[SKU SKU]</c>) và
    /// cột SKU vẫn là SKU đoán từ tên.</summary>
    [Fact]
    public async Task KhongCoDuLieuTrangChiTiet_PayloadNhuCu()
    {
        using var temp = new TempDatabase();
        using var web = new HubOutboxGsheetHuyTests.FakeGsheetWebApp();
        var (services, accId) = Dung(temp, web);

        services.Orders.UpsertMany(accId, new[]
        {
            new SyncedOrder
            {
                OrderSn = "DONCU",
                Status = "Chờ lấy hàng",
                TotalPrice = 303050,
                Sku = "A141",
                ItemCount = 1,
                ItemsJson = "[{\"name\":\"Giày A141\",\"variation\":\"Kem,36 [A141 A141]\",\"amount\":\"1\",\"image\":\"\"}]",
            },
        }, DateTime.UtcNow);

        Assert.Equal(KetQuaDay.ThanhCong, await DayAsync(accId, services));

        var body = Assert.Single(web.Bodies);
        Assert.Contains("\"phanLoai\":\"Kem,36\"", body);
        Assert.Contains("\"sku\":\"A141\"", body);
        Assert.DoesNotContain("\\n", body); // không có ô nhiều dòng nào lọt vào payload đơn cũ
    }
}
