using Shopee.Core.Coordination;
using Shopee.Hub;
using Shopee.Hub.Web.Api;

namespace Shopee.Hub.Web.Tests;

/// <summary>
/// Lọc "Có mã trả" của trang /orders (H2.3) + nguồn dữ liệu cho nút "⬇ ZIP phiếu" (H2.4). Cả hai đi qua CÙNG
/// một mệnh đề WHERE nên tổng trên lưới và số phiếu trong file zip luôn nói về cùng một tập đơn.
/// </summary>
public sealed class HubOrdersReturnFilterTests : IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "hub-ordersfilter-test-" + Guid.NewGuid().ToString("N"));

    private HubDatabase Seed(out long shopA, out long shopB)
    {
        var db = new HubDatabase(_dataDir);
        shopA = db.GetOrCreateShopByUsername("shop-a", "Shop A");
        shopB = db.GetOrCreateShopByUsername("shop-b", "Shop B");
        db.UpsertOrders(shopA,
        [
            new OrderPushItem { OrderSn = "A1", Status = "Chờ lấy hàng" },
            new OrderPushItem { OrderSn = "A2", Status = "Chờ lấy hàng", ReturnRequestCode = "YC-2" },
            new OrderPushItem { OrderSn = "A3", Status = "Đã giao", ReturnRequestCode = "YC-3" },
        ]);
        db.UpsertOrders(shopB, [new OrderPushItem { OrderSn = "B1", Status = "Chờ lấy hàng", ReturnRequestCode = "YC-B" }]);
        return db;
    }

    [Fact]
    public void KhongBatLoc_ThiRaMoiDon()
    {
        using var db = Seed(out _, out _);
        Assert.Equal(4, db.QueryOrdersPage(null, null, null, 50, 0).Total);
    }

    [Fact]
    public void BatLoc_ChiRaDonCoMaTra()
    {
        using var db = Seed(out _, out _);
        var page = db.QueryOrdersPage(null, null, null, 50, 0, coMaTra: true);

        Assert.Equal(3, page.Total);
        Assert.Equal(new[] { "A2", "A3", "B1" }, page.Items.Select(o => o.OrderSn).OrderBy(s => s).ToArray());
        Assert.All(page.Items, o => Assert.False(string.IsNullOrWhiteSpace(o.ReturnRequestCode)));
    }

    [Fact]
    public void LocMaTra_CONG_DON_VoiLocShopVaTrangThai()
    {
        using var db = Seed(out var shopA, out _);
        var page = db.QueryOrdersPage(shopA, "Chờ lấy hàng", null, 50, 0, coMaTra: true);

        Assert.Equal(1, page.Total);
        Assert.Equal("A2", page.Items.Single().OrderSn);
    }

    [Fact]
    public void MaTraRong_KhongTinhLaCoMaTra()
    {
        // Client cũ có thể gửi chuỗi rỗng/khoảng trắng thay vì bỏ field — không được coi là "có mã trả".
        using var db = new HubDatabase(_dataDir);
        var shop = db.GetOrCreateShopByUsername("shop-a", "Shop A");
        db.UpsertOrders(shop,
        [
            new OrderPushItem { OrderSn = "A1", Status = "Đã giao", ReturnRequestCode = "" },
            new OrderPushItem { OrderSn = "A2", Status = "Đã giao", ReturnRequestCode = "   " },
            new OrderPushItem { OrderSn = "A3", Status = "Đã giao", ReturnRequestCode = "YC-3" },
        ]);

        var page = db.QueryOrdersPage(null, null, null, 50, 0, coMaTra: true);
        Assert.Equal(new[] { "A3" }, page.Items.Select(o => o.OrderSn).ToArray());
    }

    // ══════════ Nguồn cho ZIP phiếu ══════════

    [Fact]
    public void OrdersWithSlip_ChiLayDonDaCoPhieuTrenHub()
    {
        using var db = Seed(out var shopA, out _);
        db.SetOrderSlipAt(shopA, "A1", DateTimeOffset.UtcNow);
        db.SetOrderSlipAt(shopA, "A2", DateTimeOffset.UtcNow);

        var rows = db.OrdersWithSlip(null, null, null, coMaTra: false, limit: 100);
        Assert.Equal(new[] { "A1", "A2" }, rows.Select(r => r.OrderSn).OrderBy(s => s).ToArray());
    }

    [Fact]
    public void OrdersWithSlip_TonTrongBoLocDangXem()
    {
        using var db = Seed(out var shopA, out var shopB);
        db.SetOrderSlipAt(shopA, "A1", DateTimeOffset.UtcNow);
        db.SetOrderSlipAt(shopA, "A2", DateTimeOffset.UtcNow);
        db.SetOrderSlipAt(shopB, "B1", DateTimeOffset.UtcNow);

        Assert.Equal(2, db.OrdersWithSlip(shopA, null, null, false, 100).Count);              // lọc shop
        Assert.Equal(2, db.OrdersWithSlip(null, null, null, coMaTra: true, 100).Count);       // lọc có mã trả (A2, B1)
        Assert.Single(db.OrdersWithSlip(shopA, null, "A2", false, 100));                      // lọc tìm kiếm
    }

    [Fact]
    public void OrdersWithSlip_TraToiDa_Limit_DeCallerBietQuaTran()
    {
        using var db = Seed(out var shopA, out var shopB);
        db.SetOrderSlipAt(shopA, "A1", DateTimeOffset.UtcNow);
        db.SetOrderSlipAt(shopA, "A2", DateTimeOffset.UtcNow);
        db.SetOrderSlipAt(shopA, "A3", DateTimeOffset.UtcNow);
        db.SetOrderSlipAt(shopB, "B1", DateTimeOffset.UtcNow);

        // Endpoint gọi với TRẦN+1: đủ 3 dòng ⇒ "hơn 2 phiếu" ⇒ trả 400 thay vì cắt bớt âm thầm.
        Assert.Equal(3, db.OrdersWithSlip(null, null, null, false, limit: 2 + 1).Count);
    }

    [Fact]
    public void TenThuMucShop_AnToanChoDuongDanTrongZip()
    {
        Assert.Equal("shop-a", OrdersZip.TenThuMucShop("shop-a", 7));
        Assert.Equal("shop_a_b", OrdersZip.TenThuMucShop("shop/a\\b", 7));   // '/' và '\' không được lọt vào entry
        Assert.Equal("shop-7", OrdersZip.TenThuMucShop("", 7));              // rỗng → vẫn có đoạn thư mục
        Assert.Equal("shop-7", OrdersZip.TenThuMucShop("///", 7));
        Assert.Equal("shop-7", OrdersZip.TenThuMucShop(null, 7));
        Assert.DoesNotContain("..", OrdersZip.TenThuMucShop("..", 7));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }
}
