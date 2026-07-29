using Shopee.Core.Coordination;
using Shopee.Hub;

namespace Shopee.Hub.Web.Tests;

public sealed class HubOrdersReadTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "hub-orders-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void QueryOrdersPage_ReturnsConsistentCountAndItems()
    {
        using var db = new HubDatabase(_dataDir);
        var shopId = db.GetOrCreateShopByUsername("shop-login", "Shop Test");
        db.UpsertOrders(shopId,
        [
            new OrderPushItem { OrderSn = "ORDER-1", Status = "Chờ lấy hàng", Sku = "SKU-ONE" },
            new OrderPushItem { OrderSn = "ORDER-2", Status = "Hoàn thành", Sku = "SKU-TWO" },
        ]);

        var page = db.QueryOrdersPage(null, "Chờ lấy hàng", "SKU-ONE", 25, 0);

        Assert.Equal(1, page.Total);
        Assert.Collection(page.Items, order => Assert.Equal("ORDER-1", order.OrderSn));
        Assert.Equal(2, db.OrderCountsByShop()[shopId]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }
}
