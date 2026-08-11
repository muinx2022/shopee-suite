using Shopee.Core.Coordination;
using Shopee.Hub;

namespace Shopee.Hub.Web.Tests;

/// <summary>
/// <b>T4 (review 11/08): <c>UpsertOrders</c> không cho CHUỖI RỖNG ghi đè giá trị đang có.</b>
/// <para><c>COALESCE($x, cột)</c> trần chỉ chặn NULL — một client lỗi đẩy <c>""</c> là xoá mã vận đơn / mã trả
/// hàng / mốc chuẩn bị trên hub, không mốc, không notify, và đơn bị dọn phía client là hết đường sửa. Hợp đồng
/// nay khoá ở tầng SQL: <c>COALESCE(NULLIF(TRIM($x),''), cột)</c> cho 5 cột TEXT thuộc nhóm "bên rỗng không
/// được đè bên có".</para>
/// </summary>
public sealed class UpsertChuoiRongKhongDeTests : IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "hub-upsert-rong-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ChuoiRongVaKhoangTrang_KhongDeGiaTriDangCo()
    {
        using var db = new HubDatabase(_dataDir);
        var shopId = db.GetOrCreateShopByUsername("shop-login", "Shop Test");
        var homNay = "2026-08-11";
        db.UpsertOrders(shopId,
        [
            new OrderPushItem
            {
                OrderSn = "SN1", Status = "Chờ lấy hàng", TrackingNumber = "SPXVN123", ReturnRequestCode = "YC-1",
                FinalAmountText = "₫99.000", PreparedAt = "2026-08-11T03:00:00Z", PreparedDay = homNay,
            },
        ]);

        // Client lỗi đẩy lại với chuỗi RỖNG / toàn khoảng trắng ở đúng các cột đó.
        db.UpsertOrders(shopId,
        [
            new OrderPushItem
            {
                OrderSn = "SN1", Status = "Chờ lấy hàng", TrackingNumber = "", ReturnRequestCode = "  ",
                FinalAmountText = "", PreparedAt = " ", PreparedDay = "",
            },
        ]);

        var o = db.QueryOrdersPage(shopId, null, null, 10, 0).Items.Single();
        Assert.Equal("SPXVN123", o.TrackingNumber);
        Assert.Equal("YC-1", o.ReturnRequestCode);
        Assert.Equal("₫99.000", o.FinalAmountText);
        // prepared_day còn nguyên ⇒ thống kê chuẩn bị theo ngày không mất đơn.
        Assert.Equal(1, db.PrepareStatsByDay(homNay).Single(r => r.ShopUsername == "shop-login").Count);
    }

    [Fact]
    public void GiaTriMoiThatSu_VanDeDuoc()
    {
        using var db = new HubDatabase(_dataDir);
        var shopId = db.GetOrCreateShopByUsername("shop-login", "Shop Test");
        db.UpsertOrders(shopId,
            [new OrderPushItem { OrderSn = "SN1", Status = "Chờ lấy hàng", TrackingNumber = "SPXVN123" }]);

        db.UpsertOrders(shopId,
            [new OrderPushItem { OrderSn = "SN1", Status = "Chờ lấy hàng", TrackingNumber = "SPXVN999" }]);

        Assert.Equal("SPXVN999", db.QueryOrdersPage(shopId, null, null, 10, 0).Items.Single().TrackingNumber);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }
}
