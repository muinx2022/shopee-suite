extern alias ordersCore;

using System.Globalization;
using Microsoft.Data.Sqlite;
using Shopee.Core.Coordination;
using Shopee.Hub;
using XuLyDonShopee.Core.Services;
using LocalDatabase = ordersCore::XuLyDonShopee.Core.Data.Database;
using LocalOrdersRepository = ordersCore::XuLyDonShopee.Core.Data.OrdersRepository;
using LocalSyncedOrder = ordersCore::XuLyDonShopee.Core.Models.SyncedOrder;

namespace Shopee.Hub.Web.Tests;

/// <summary>
/// Thống kê đơn DÙNG CHUNG (<see cref="HubDatabase.GetSharedOrderStatistics"/>): đếm theo <c>first_seen_at</c> =
/// lần ĐẦU đơn xuất hiện trên hub (không phải <c>synced_at</c> — cột đó bị ghi đè mỗi lần đồng bộ lại), lọc theo
/// hai MỐC UTC client gửi lên (hub không quy đổi theo giờ máy chủ), và ra ĐÚNG con số mà máy client tự tính.
/// </summary>
public sealed class HubSharedOrderStatsTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "hub-stats-test-" + Guid.NewGuid().ToString("N"));

    // ===== 1. first_seen_at đặt lúc INSERT và KHÔNG đổi khi đồng bộ lại (đây là mốc đếm đơn) =====
    [Fact]
    public void FirstSeenAt_GiuNguyenKhiDongBoLai_ChiSyncedAtDoi()
    {
        using var db = new HubDatabase(_dataDir);
        var shopId = db.GetOrCreateShopByUsername("shop-login", "Shop Test");
        db.UpsertOrders(shopId, [new OrderPushItem { OrderSn = "SN1", Status = "Chờ lấy hàng" }]);
        var (firstSeen1, synced1) = ReadStamps("SN1");

        Thread.Sleep(5); // để mốc đồng bộ lần 2 khác hẳn lần 1
        db.UpsertOrders(shopId, [new OrderPushItem { OrderSn = "SN1", Status = "Đã giao" }]);
        var (firstSeen2, synced2) = ReadStamps("SN1");

        Assert.Equal(firstSeen1, firstSeen2);                 // mốc "lần đầu thấy đơn" BẤT BIẾN
        Assert.True(synced2 > synced1, $"synced_at phải mới hơn: {synced1} → {synced2}");
        Assert.Equal("Đã giao", db.QueryOrders(shopId, null, null, 10, 0).Single().Status); // dữ liệu VẪN được cập nhật
    }

    // ===== 2. Biên khoảng UTC: [from, to) — đơn ở đúng mốc from được đếm, ở đúng mốc to bị loại =====
    [Fact]
    public void LocTheoKhoangUtc_BienDuoiDong_BienTrenMo()
    {
        using var db = new HubDatabase(_dataDir);
        var shopId = db.GetOrCreateShopByUsername("shop-login", "Shop Test");
        db.UpsertOrders(shopId, [new OrderPushItem { OrderSn = "SN1", Status = "Chờ lấy hàng" }]);
        var moc = ReadStamps("SN1").FirstSeen.UtcDateTime;

        var tinhTuMoc = db.GetSharedOrderStatistics(moc, moc.AddMilliseconds(1), null);
        var denDungMoc = db.GetSharedOrderStatistics(moc.AddMilliseconds(-1), moc, null);

        Assert.Equal(1, tinhTuMoc.TotalOrders);   // đúng mốc from → ĐẾM
        Assert.Equal(0, denDungMoc.TotalOrders);  // đúng mốc to → LOẠI
    }

    // ===== 3. Mốc hiểu theo UTC, KHÔNG theo giờ máy chủ =====
    [Fact]
    public void KhoangLoc_HieuTheoUtc_KhongPhuThuocGioMayChu()
    {
        using var db = new HubDatabase(_dataDir);
        var shopId = db.GetOrCreateShopByUsername("shop-login", "Shop Test");
        db.UpsertOrders(shopId, [new OrderPushItem { OrderSn = "SN1", Status = "Chờ lấy hàng" }]);
        SetFirstSeen("SN1", "2026-03-15T22:30:00.0000000+00:00");

        // Khoảng ôm đúng mốc UTC → đếm được, dù máy chủ chạy múi giờ nào.
        var theoUtc = db.GetSharedOrderStatistics(
            new DateTime(2026, 3, 15, 22, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 3, 15, 23, 0, 0, DateTimeKind.Utc), null);
        // Cùng GIỜ ĐỒNG HỒ nhưng ở múi giờ VN (UTC+7) → là mốc UTC KHÁC → không được đếm. Bản cũ tự quy đổi ngày
        // theo giờ máy chủ nên hai truy vấn này đổi chỗ kết quả cho nhau tuỳ máy.
        var lechMuiGio = db.GetSharedOrderStatistics(
            new DateTime(2026, 3, 16, 5, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 3, 16, 6, 0, 0, DateTimeKind.Utc), null);

        Assert.Equal(1, theoUtc.TotalOrders);
        Assert.Equal(0, lechMuiGio.TotalOrders);
    }

    // ===== 4. CHỐT CHẶN: cùng bộ đơn + cùng khoảng → số hub KHỚP số máy client, KỂ CẢ sau khi đồng bộ lại =====
    [Fact]
    public void SoHubKhopSoLocal_KeCaSauKhiDongBoLai()
    {
        var dbPath = Path.Combine(_dataDir, "local.db");
        Directory.CreateDirectory(_dataDir);
        var mocCu = new DateTime(2026, 6, 10, 3, 0, 0, DateTimeKind.Utc);   // đơn xuất hiện lần đầu (30 ngày trước)
        var mocDongBoLai = new DateTime(2026, 7, 10, 4, 0, 0, DateTimeKind.Utc);

        var donHang = new[]
        {
            new { Sn = "SN1", Status = "Chờ lấy hàng", Huy = (string?)null },
            new { Sn = "SN2", Status = "Đã giao", Huy = (string?)null },
            new { Sn = "SN3", Status = "Đã hủy", Huy = (string?)"Người mua hủy" },
        };

        // -- Kho đơn TRÊN MÁY: created_at = mốc sync lần đầu, giữ nguyên khi sync lại (đây là mốc màn Thống kê lọc).
        var local = new LocalOrdersRepository(new LocalDatabase(dbPath));
        var donLocal = donHang
            .Select(d => new LocalSyncedOrder { OrderSn = d.Sn, Status = d.Status, CancelReason = d.Huy })
            .ToArray();
        local.UpsertMany(1, donLocal, mocCu, shopLogin: "shop-login");

        // -- Kho đơn TRÊN HUB: cùng bộ đơn; lùi first_seen_at về đúng mốc cũ (hub tự đặt = lúc nhận).
        using var hub = new HubDatabase(_dataDir);
        var shopId = hub.GetOrCreateShopByUsername("shop-login", "Shop Test");
        hub.UpsertOrders(shopId, donHang
            .Select(d => new OrderPushItem { OrderSn = d.Sn, Status = d.Status, CancelReason = d.Huy })
            .ToArray());
        foreach (var d in donHang) SetFirstSeen(d.Sn, mocCu.ToString("o", CultureInfo.InvariantCulture));

        // -- Đồng bộ LẠI cả hai kho ở thời điểm khác (đơn cũ được đẩy lại) → mốc "lần đầu" không được đổi.
        local.UpsertMany(1, donLocal, mocDongBoLai, shopLogin: "shop-login");
        hub.UpsertOrders(shopId, donHang
            .Select(d => new OrderPushItem { OrderSn = d.Sn, Status = d.Status, CancelReason = d.Huy })
            .ToArray());

        // Khoảng CHỨA mốc cũ: cả hai bên phải thấy đủ 3 đơn (2 hiệu lực, 1 hủy).
        var (fromCu, toCu) = (mocCu.AddHours(-1), mocCu.AddHours(1));
        var hubCu = hub.GetSharedOrderStatistics(fromCu, toCu, null);
        var localCu = local.Query(createdFromUtc: fromCu, createdBeforeUtc: toCu);
        Assert.Equal(3, localCu.Count);
        Assert.Equal(localCu.Count, hubCu.TotalOrders);
        Assert.Equal(localCu.Count(r => ShopeeShippingNav.LaDonHuy(r.Status, r.StatusDescription, r.CancelReason)),
            hubCu.Cancelled);

        // Khoảng CHỨA mốc đồng bộ lại: cả hai bên phải thấy 0 đơn (đơn cũ KHÔNG nhảy sang hôm nay).
        var (fromMoi, toMoi) = (mocDongBoLai.AddHours(-1), mocDongBoLai.AddHours(1));
        var hubMoi = hub.GetSharedOrderStatistics(fromMoi, toMoi, null);
        var localMoi = local.Query(createdFromUtc: fromMoi, createdBeforeUtc: toMoi);
        Assert.Empty(localMoi);
        Assert.Equal(localMoi.Count, hubMoi.TotalOrders);
    }

    // ===== 5. Migration trên DB CŨ đang có dữ liệu (hub thật trên VM): thêm cột + backfill, không mất đơn, không sập =====
    [Fact]
    public void MoDbCu_ThemCotFirstSeen_BackfillTuSyncedAt_VaVanUpsertDuoc()
    {
        Directory.CreateDirectory(_dataDir);
        using (var conn = OpenHubDb())
        using (var c = conn.CreateCommand())
        {
            // Bảng orders ĐÚNG như bản trước bản vá (chưa có first_seen_at), đã có sẵn một đơn.
            c.CommandText = @"
CREATE TABLE orders(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  shop_id INTEGER NOT NULL,
  order_sn TEXT NOT NULL,
  shopee_order_id TEXT, buyer_username TEXT,
  items_json TEXT DEFAULT '[]', item_count INTEGER DEFAULT 0,
  item_summary TEXT, sku TEXT,
  total_price INTEGER, total_price_text TEXT,
  final_amount INTEGER, final_amount_text TEXT,
  payment_method TEXT, status TEXT, status_description TEXT, cancel_reason TEXT,
  channel TEXT, carrier TEXT, tracking_number TEXT,
  synced_at TEXT, slip_at TEXT,
  prepared_at TEXT, prepared_day TEXT, return_request_code TEXT);
INSERT INTO orders(shop_id, order_sn, status, synced_at)
VALUES(1, 'SN-CU', 'Chờ lấy hàng', '2026-05-01T10:00:00.0000000+00:00');";
            c.ExecuteNonQuery();
        }

        using var db = new HubDatabase(_dataDir); // = hub khởi động lại trên DB cũ
        var cu = ReadStamps("SN-CU");
        Assert.Equal(cu.Synced, cu.FirstSeen); // backfill: đơn cũ lấy tạm synced_at làm mốc lần đầu

        var shopId = db.GetOrCreateShopByUsername("shop-login", "Shop Test");
        db.UpsertOrders(shopId, [new OrderPushItem { OrderSn = "SN-MOI", Status = "Chờ lấy hàng" }]);

        Assert.True(ReadStamps("SN-MOI").FirstSeen > cu.FirstSeen);   // đơn mới có mốc thật
        Assert.Equal(cu.FirstSeen, ReadStamps("SN-CU").FirstSeen);    // đơn cũ KHÔNG mất, mốc giữ nguyên
    }

    /// <summary>Đọc thẳng hai cột mốc của một đơn từ hub.db (HubDatabase không lộ <c>first_seen_at</c> ra ngoài).</summary>
    private (DateTimeOffset FirstSeen, DateTimeOffset Synced) ReadStamps(string orderSn)
    {
        using var conn = OpenHubDb();
        using var c = conn.CreateCommand();
        c.CommandText = "SELECT first_seen_at, synced_at FROM orders WHERE order_sn = $sn";
        c.Parameters.AddWithValue("$sn", orderSn);
        using var rd = c.ExecuteReader();
        Assert.True(rd.Read(), $"Không thấy đơn {orderSn} trên hub");
        return (Parse(rd.GetString(0)), Parse(rd.GetString(1)));

        static DateTimeOffset Parse(string v)
            => DateTimeOffset.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    /// <summary>Lùi <c>first_seen_at</c> của một đơn về mốc cho trước (dựng dữ liệu "đơn cũ" cho test).</summary>
    private void SetFirstSeen(string orderSn, string isoUtc)
    {
        using var conn = OpenHubDb();
        using var c = conn.CreateCommand();
        c.CommandText = "UPDATE orders SET first_seen_at = $t WHERE order_sn = $sn";
        c.Parameters.AddWithValue("$t", isoUtc);
        c.Parameters.AddWithValue("$sn", orderSn);
        Assert.Equal(1, c.ExecuteNonQuery());
    }

    private SqliteConnection OpenHubDb()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_dataDir, "hub.db"),
        }.ToString());
        conn.Open();
        return conn;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }
}
