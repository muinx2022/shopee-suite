using Microsoft.Data.Sqlite;
using Shopee.Core.Coordination;
using Shopee.Hub;
using Shopee.Hub.Web.Services;

namespace Shopee.Hub.Web.Tests;

/// <summary>
/// Số "Đơn chờ hôm nay" của HÀNG THẺ TỔNG QUAN trang chủ (H1.1) — <see cref="HomeOverview.DonChoHomNay"/>.
/// Cùng khuôn với <see cref="HubSharedOrderStatsTests"/>: dựng đơn rồi lùi <c>first_seen_at</c> về mốc muốn thử,
/// vì đây phải là ĐÚNG truy vấn "Đơn chờ hôm nay" của tab Đơn hàng (lọc <c>first_seen_at</c> theo NGÀY VIỆT NAM),
/// không phải một phép đếm mới.
/// </summary>
public sealed class HomeOverviewTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "hub-overview-test-" + Guid.NewGuid().ToString("N"));

    /// <summary>10:00 sáng 03/08/2026 giờ VN — mốc "bây giờ" cố định cho mọi ca (test không phụ thuộc đồng hồ máy).</summary>
    private static readonly DateTimeOffset BayGioVN = new(2026, 8, 3, 10, 0, 0, TimeSpan.FromHours(7));

    /// <summary>Ca chính: gộp MỌI shop, chỉ đếm đơn đang chờ, chỉ đếm đơn xuất hiện trong ngày VN hôm nay.</summary>
    [Fact]
    public void DonChoHomNay_GopMoiShop_ChiDonChoTrongNgayVietNam()
    {
        using var db = new HubDatabase(_dataDir);
        var shopA = db.GetOrCreateShopByUsername("shop-a", "Shop A");
        var shopB = db.GetOrCreateShopByUsername("shop-b", "Shop B");
        db.UpsertOrders(shopA,
        [
            new OrderPushItem { OrderSn = "A-HOM-NAY-1", Status = "Chờ lấy hàng" },
            new OrderPushItem { OrderSn = "A-HOM-NAY-2", Status = "Chờ lấy hàng" },
            new OrderPushItem { OrderSn = "A-HOM-QUA", Status = "Chờ lấy hàng" },
            new OrderPushItem { OrderSn = "A-DA-GIAO", Status = "Đã giao" },     // hôm nay nhưng KHÔNG còn chờ
        ]);
        db.UpsertOrders(shopB, [new OrderPushItem { OrderSn = "B-HOM-NAY", Status = "Chờ lấy hàng" }]);

        SetFirstSeen("A-HOM-NAY-1", "2026-08-02T17:00:00.0000000+00:00");   // 00:00:00 ngày 03/08 giờ VN (biên DƯỚI, tính)
        SetFirstSeen("A-HOM-NAY-2", "2026-08-03T05:00:00.0000000+00:00");
        SetFirstSeen("A-HOM-QUA", "2026-08-02T16:59:59.0000000+00:00");     // 23:59:59 ngày 02/08 giờ VN (loại)
        SetFirstSeen("A-DA-GIAO", "2026-08-03T05:00:00.0000000+00:00");
        SetFirstSeen("B-HOM-NAY", "2026-08-03T06:00:00.0000000+00:00");

        // 2 của shop A + 1 của shop B = 3 (thẻ tổng quan là số TOÀN HỆ THỐNG, không phải của một shop).
        Assert.Equal(3, HomeOverview.DonChoHomNay(db, BayGioVN));
    }

    /// <summary>
    /// Ngày chốt theo GIỜ VIỆT NAM, không theo ngày UTC của máy chủ. Hai đơn CỐ TÌNH nằm ở hai phía mà hai cách
    /// chia ngày cho kết quả NGƯỢC NHAU:
    /// <list type="bullet">
    /// <item><c>SAT-BIEN</c> 16:59:59Z ngày 02/08 = 23:59:59 VN ngày 02/08 → HÔM QUA theo VN, nhưng "hôm nay"
    /// nếu ai đó chia ngày theo UTC của mốc 03/08.</item>
    /// <item><c>DEM-KHUYA</c> 20:00Z ngày 02/08 = 03:00 VN ngày 03/08 → HÔM NAY theo VN, nhưng "hôm qua" theo UTC.</item>
    /// </list>
    /// Chia ngày sai kiểu nào cũng đổi cả hai con số dưới đây.
    /// </summary>
    [Fact]
    public void DonChoHomNay_BienNgayTheoGioVietNam_KhongTheoNgayUtc()
    {
        using var db = new HubDatabase(_dataDir);
        var shop = db.GetOrCreateShopByUsername("shop-a", "Shop A");
        db.UpsertOrders(shop,
        [
            new OrderPushItem { OrderSn = "SAT-BIEN", Status = "Chờ lấy hàng" },
            new OrderPushItem { OrderSn = "DEM-KHUYA", Status = "Chờ lấy hàng" },
        ]);
        SetFirstSeen("SAT-BIEN", "2026-08-02T16:59:59.0000000+00:00");
        SetFirstSeen("DEM-KHUYA", "2026-08-02T20:00:00.0000000+00:00");

        Assert.Equal(1, HomeOverview.DonChoHomNay(db, BayGioVN));              // chỉ DEM-KHUYA
        Assert.Equal(1, HomeOverview.DonChoHomNay(db, BayGioVN.AddDays(-1)));  // chỉ SAT-BIEN
    }

    /// <summary>Đồng bộ LẠI đơn cũ không được kéo nó vào hôm nay (lọc theo first_seen_at, không phải synced_at).</summary>
    [Fact]
    public void DonChoHomNay_DongBoLaiDonCu_KhongNhayVaoHomNay()
    {
        using var db = new HubDatabase(_dataDir);
        var shop = db.GetOrCreateShopByUsername("shop-a", "Shop A");
        db.UpsertOrders(shop, [new OrderPushItem { OrderSn = "CU", Status = "Chờ lấy hàng" }]);
        SetFirstSeen("CU", "2026-07-01T05:00:00.0000000+00:00");

        db.UpsertOrders(shop, [new OrderPushItem { OrderSn = "CU", Status = "Chờ lấy hàng" }]);   // đẩy lại HÔM NAY

        Assert.Equal(0, HomeOverview.DonChoHomNay(db, BayGioVN));
    }

    /// <summary>Lùi <c>first_seen_at</c> của một đơn về mốc cho trước (dựng dữ liệu "đơn cũ" cho test).</summary>
    private void SetFirstSeen(string orderSn, string isoUtc)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_dataDir, "hub.db"),
        }.ToString());
        conn.Open();
        using var c = conn.CreateCommand();
        c.CommandText = "UPDATE orders SET first_seen_at = $t WHERE order_sn = $sn";
        c.Parameters.AddWithValue("$t", isoUtc);
        c.Parameters.AddWithValue("$sn", orderSn);
        Assert.Equal(1, c.ExecuteNonQuery());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }
}
