using Shopee.Core.Coordination;
using Shopee.Hub;

namespace Shopee.Hub.Web.Tests;

/// <summary>
/// <b>T9 (review 11/08): <c>/api/orders/slip</c> phải TÔN TRỌNG kết quả <c>SetOrderSlipAt</c>.</b>
/// <para>Đơn có thể bị xoá GIỮA lượt check <c>OrderExists</c> và lượt UPDATE — bản trước bỏ giá trị trả về nên
/// vẫn báo <c>saved</c>: client đóng cờ <c>hub_slip_synced_at</c> vĩnh viễn còn hub ôm phiếu MỒ CÔI. Endpoint
/// nay rẽ nhánh theo số dòng UPDATE (0 → missing + xoá file); hợp đồng số-dòng pin tại đây.</para>
/// </summary>
public sealed class SetOrderSlipAtContractTests : IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "hub-slipat-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void DonTonTai_Tra1_DonKhong_Tra0()
    {
        using var db = new HubDatabase(_dataDir);
        var shopId = db.GetOrCreateShopByUsername("shop-login", "Shop Test");
        db.UpsertOrders(shopId, [new OrderPushItem { OrderSn = "SN1", Status = "Chờ lấy hàng" }]);

        Assert.Equal(1, db.SetOrderSlipAt(shopId, "SN1", DateTimeOffset.UtcNow));       // đơn có
        Assert.Equal(0, db.SetOrderSlipAt(shopId, "SN-DA-XOA", DateTimeOffset.UtcNow)); // đơn không còn
        Assert.Equal(0, db.SetOrderSlipAt(shopId + 999, "SN1", DateTimeOffset.UtcNow)); // sai shop cũng không
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }
}
