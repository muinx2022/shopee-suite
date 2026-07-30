using System.Globalization;
using Microsoft.Data.Sqlite;
using Shopee.Core.Coordination;
using Shopee.Hub;

namespace Shopee.Hub.Web.Tests;

/// <summary>
/// Ba thứ đi cùng đường <c>POST /api/orders/push</c>:
/// <list type="bullet">
/// <item><b>Khoá shop KHÔNG phân biệt hoa/thường</b> — migration gộp hàng <c>shops</c> trùng + index
/// <c>COLLATE NOCASE</c>. Hai hàng shop cho CÙNG một shop vật lý làm khoá chống trùng đơn
/// <c>(shop_id, order_sn)</c> mất tác dụng → cùng một đơn bị đếm 2 lần trong thống kê dùng chung.</item>
/// <item><b>Notify "đơn trả"</b> chỉ tính mã ĐỔI trên đơn hub ĐÃ CÓ (không bắn loạt tin cũ khi dựng lại hub).</item>
/// <item><b>first_seen_at</b> lấy <c>CreatedAt</c> client gửi kèm; client cũ không gửi → lùi về giờ hub nhận.</item>
/// </list>
/// </summary>
public sealed class HubShopMergeAndPushTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "hub-merge-test-" + Guid.NewGuid().ToString("N"));

    // ===== 1. Migration gộp shop trùng hoa/thường: đơn KHÔNG còn bị đếm 2 lần =====
    [Fact]
    public void GopShopTrungHoaThuong_DonKhongConDemHaiLan()
    {
        var moc = new DateTime(2026, 6, 10, 3, 0, 0, DateTimeKind.Utc);
        DungDbCuHaiShopTrung(moc);

        using var db = new HubDatabase(_dataDir);   // = hub khởi động lại trên DB cũ → migration chạy

        // Chỉ còn MỘT hàng shop, giữ hàng CŨ NHẤT (id 1, chữ hoa/thường bản đầu).
        var shop = Assert.Single(db.ListShops());
        Assert.Equal(1, shop.Id);
        Assert.Equal("ShopA", shop.Username);

        // SN1 (có ở CẢ hai shop) chỉ còn MỘT dòng → 2 đơn chứ không phải 3.
        var stats = db.GetSharedOrderStatistics(moc.AddHours(-1), moc.AddHours(1), null);
        Assert.Equal(2, stats.TotalOrders);

        // Bản GIỮ LẠI của SN1 là bản synced_at MỚI NHẤT (trạng thái mới, không phải bản cũ).
        Assert.Equal("Đã hủy", DocCot("SN1", "status"));

        // Phiếu PDF của shop bị gộp đã dời sang thư mục shop giữ lại.
        Assert.True(File.Exists(Path.Combine(_dataDir, "slips", "1", "SN1.pdf")));
        Assert.False(Directory.Exists(Path.Combine(_dataDir, "slips", "2")));
    }

    // ===== 2. Lọc theo shop KHÔNG phân biệt hoa/thường (client gõ lệch hoa/thường vẫn ra số) =====
    [Fact]
    public void LocTheoShop_KhongPhanBietHoaThuong()
    {
        var moc = new DateTime(2026, 6, 10, 3, 0, 0, DateTimeKind.Utc);
        DungDbCuHaiShopTrung(moc);
        using var db = new HubDatabase(_dataDir);

        var (from, to) = (moc.AddHours(-1), moc.AddHours(1));
        Assert.Equal(2, db.GetSharedOrderStatistics(from, to, "ShopA").TotalOrders);
        Assert.Equal(2, db.GetSharedOrderStatistics(from, to, "shopa").TotalOrders);
        Assert.Equal(2, db.GetSharedOrderStatistics(from, to, "  SHOPA  ").TotalOrders);
    }

    // ===== 3. Migration IDEMPOTENT: mở lại lần 2 không lỗi, không đổi thêm gì =====
    [Fact]
    public void MigrationGopShop_ChayLaiLanHai_KhongDoiGi()
    {
        var moc = new DateTime(2026, 6, 10, 3, 0, 0, DateTimeKind.Utc);
        DungDbCuHaiShopTrung(moc);

        int soShop, soDon;
        using (var db1 = new HubDatabase(_dataDir))
        {
            soShop = db1.ListShops().Count;
            soDon = db1.GetSharedOrderStatistics(moc.AddHours(-1), moc.AddHours(1), null).TotalOrders;
        }

        using var db2 = new HubDatabase(_dataDir);  // lần mở thứ 2
        Assert.Equal(soShop, db2.ListShops().Count);
        Assert.Equal(soDon, db2.GetSharedOrderStatistics(moc.AddHours(-1), moc.AddHours(1), null).TotalOrders);
    }

    // ===== 4. Sau migration, index NOCASE chặn đẻ shop trùng: push lệch hoa/thường về ĐÚNG một shop_id =====
    [Fact]
    public void SauMigration_PushLechHoaThuong_VeCungMotShopId()
    {
        using var db = new HubDatabase(_dataDir);
        var id1 = db.GetOrCreateShopByUsername("ShopB", "Shop B");
        var id2 = db.GetOrCreateShopByUsername("shopb", null);
        var id3 = db.GetOrCreateShopByUsername("  SHOPB  ", null);   // có khoảng trắng thừa

        Assert.Equal(id1, id2);
        Assert.Equal(id1, id3);
        Assert.Single(db.ListShops());
    }

    // ===== 5. Notify "đơn trả": đơn INSERT lần đầu mang sẵn mã KHÔNG phải tin mới =====
    [Fact]
    public void MaTraHang_DonInsertLanDau_KhongVaoReturnCodeChanged()
    {
        using var db = new HubDatabase(_dataDir);
        var shopId = db.GetOrCreateShopByUsername("shop-login", "Shop Test");

        // Lần đầu thấy đơn, đã mang sẵn mã (hub mới dựng / client đẩy bù cả kho) → KHÔNG bắn tin.
        var lanDau = db.UpsertOrders(shopId, [new OrderPushItem { OrderSn = "SN1", ReturnRequestCode = "R001" }]);
        Assert.Equal(1, lanDau.Added);
        Assert.Empty(lanDau.ReturnCodeChangedItems);

        // Đẩy lại ĐÚNG mã đó → không đổi, vẫn không bắn.
        Assert.Empty(db.UpsertOrders(shopId, [new OrderPushItem { OrderSn = "SN1", ReturnRequestCode = "R001" }])
            .ReturnCodeChangedItems);

        // Mã ĐỔI trên đơn đã có → ĐÂY mới là tin mới.
        var doiMa = db.UpsertOrders(shopId, [new OrderPushItem { OrderSn = "SN1", ReturnRequestCode = "R002" }]);
        Assert.Equal("R002", Assert.Single(doiMa.ReturnCodeChangedItems).ReturnRequestCode);
    }

    // ===== 6. first_seen_at = CreatedAt client gửi kèm; thiếu → lùi về giờ hub nhận =====
    [Fact]
    public void FirstSeenAt_LayCreatedAtCuaClient_ThieuThiLuiVeGioNhan()
    {
        using var db = new HubDatabase(_dataDir);
        var shopId = db.GetOrCreateShopByUsername("shop-login", "Shop Test");
        var mocClient = new DateTimeOffset(2026, 6, 10, 3, 0, 0, TimeSpan.Zero);

        db.UpsertOrders(shopId,
        [
            // Client MỚI: gửi created_at dạng DateTime.ToString("o") của mốc UTC (hậu tố "Z").
            new OrderPushItem { OrderSn = "SN-CO", CreatedAt = mocClient.UtcDateTime.ToString("o", CultureInfo.InvariantCulture) },
            // Client CŨ: không gửi field này (hợp đồng cho phép) → hub lùi về giờ nhận.
            new OrderPushItem { OrderSn = "SN-KHONG" },
        ]);

        Assert.Equal(mocClient, DocMoc("SN-CO", "first_seen_at"));
        Assert.Equal(DocMoc("SN-KHONG", "synced_at"), DocMoc("SN-KHONG", "first_seen_at"));

        // Chuỗi lưu phải CÙNG định dạng hub tự ghi ("+00:00", không phải "Z") — lọc theo khoảng là so CHUỖI.
        Assert.EndsWith("+00:00", DocCot("SN-CO", "first_seen_at"));

        // Đơn CÓ created_at rơi đúng khoảng ngày của mốc ĐÓ, không phải hôm nay.
        Assert.Equal(1, db.GetSharedOrderStatistics(
            mocClient.UtcDateTime.AddHours(-1), mocClient.UtcDateTime.AddHours(1), null).TotalOrders);
    }

    // ===== 7. Đẩy LẠI đơn có created_at KHÔNG dời first_seen_at (nhánh DO UPDATE không đụng cột) =====
    [Fact]
    public void DayLai_KhongDoiFirstSeenAt()
    {
        using var db = new HubDatabase(_dataDir);
        var shopId = db.GetOrCreateShopByUsername("shop-login", "Shop Test");
        var moc = new DateTimeOffset(2026, 6, 10, 3, 0, 0, TimeSpan.Zero);

        db.UpsertOrders(shopId, [new OrderPushItem { OrderSn = "SN1", CreatedAt = moc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture) }]);
        db.UpsertOrders(shopId, [new OrderPushItem { OrderSn = "SN1", CreatedAt = moc.AddDays(30).UtcDateTime.ToString("o", CultureInfo.InvariantCulture) }]);

        Assert.Equal(moc, DocMoc("SN1", "first_seen_at"));
    }

    /// <summary>
    /// Dựng hub.db kiểu BẢN CŨ: index username PHÂN BIỆT hoa/thường nên có 2 hàng shop cho cùng một shop vật lý,
    /// và đơn <c>SN1</c> nằm ở CẢ HAI (bản của shop 2 mới hơn). Kèm 1 file phiếu của shop 2 để kiểm việc dời.
    /// </summary>
    private void DungDbCuHaiShopTrung(DateTime firstSeen)
    {
        Directory.CreateDirectory(_dataDir);
        var fs = new DateTimeOffset(firstSeen, TimeSpan.Zero).ToString("o");
        using (var conn = OpenHubDb())
        using (var c = conn.CreateCommand())
        {
            c.CommandText = @"
CREATE TABLE shops(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  name TEXT NOT NULL DEFAULT '', username TEXT, password TEXT, cookie TEXT,
  proxy_key TEXT, note TEXT, created_at TEXT, updated_at TEXT);
CREATE UNIQUE INDEX ux_shops_username ON shops(username);
INSERT INTO shops(id,name,username) VALUES(1,'Shop A','ShopA'),(2,'shop a','shopa');
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
  prepared_at TEXT, prepared_day TEXT, return_request_code TEXT, first_seen_at TEXT);
CREATE UNIQUE INDEX ux_orders_shop_sn ON orders(shop_id, order_sn);
INSERT INTO orders(shop_id,order_sn,status,synced_at,first_seen_at) VALUES
  (1,'SN1','Chờ lấy hàng','2026-06-10T04:00:00.0000000+00:00',$fs),
  (2,'SN1','Đã hủy',      '2026-06-11T04:00:00.0000000+00:00',$fs),
  (2,'SN2','Chờ lấy hàng','2026-06-11T04:00:00.0000000+00:00',$fs);";
            c.Parameters.AddWithValue("$fs", fs);
            c.ExecuteNonQuery();
        }

        var slipDir = Path.Combine(_dataDir, "slips", "2");
        Directory.CreateDirectory(slipDir);
        File.WriteAllText(Path.Combine(slipDir, "SN1.pdf"), "%PDF-1.4");
    }

    private string DocCot(string orderSn, string cot)
    {
        using var conn = OpenHubDb();
        using var c = conn.CreateCommand();
        c.CommandText = $"SELECT {cot} FROM orders WHERE order_sn = $sn";   // tên cột là hằng trong test
        c.Parameters.AddWithValue("$sn", orderSn);
        var v = c.ExecuteScalar();
        Assert.NotNull(v);
        return (string)v!;
    }

    private DateTimeOffset DocMoc(string orderSn, string cot)
        => DateTimeOffset.Parse(DocCot(orderSn, cot), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

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
