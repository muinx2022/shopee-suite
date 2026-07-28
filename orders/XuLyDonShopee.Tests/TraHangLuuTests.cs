using Microsoft.Data.Sqlite;
using XuLyDonShopee.Core.Data;
using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test phần LƯU của bước "check đơn trả hàng":
/// <list type="bullet">
/// <item><see cref="ResultsRepository.GetReturnCount"/>/<see cref="ResultsRepository.SetReturnCount"/> — mốc số
/// yêu cầu nhớ THEO TỪNG SHOP, sống qua khởi động lại app.</item>
/// <item><see cref="OrdersRepository.SetReturnRequestCodes"/> — ghi mã vào đơn theo <c>order_sn</c>: ghi đè khi
/// KHÁC, không ghi đè bằng rỗng, đơn không có trong DB thì bỏ + đếm riêng, và mã ĐỔI thì reset cờ để lượt sau đẩy
/// lại GSheet + hub.</item>
/// <item>Migration cột mới cho DB CŨ.</item>
/// </list>
/// </summary>
public class TraHangLuuTests
{
    private static SyncedOrder Don(string sn) => new()
    {
        OrderSn = sn,
        ItemsJson = "[]",
        Status = "Chờ lấy hàng",
        TotalPrice = 100000,
    };

    /// <summary>Đọc một cột của đơn theo order_sn (null nếu NULL/không có) — soi trực tiếp cờ DB.</summary>
    private static string? DocCot(string path, string orderSn, string column)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {column} FROM orders WHERE order_sn = $sn;";
        cmd.Parameters.AddWithValue("$sn", orderSn);
        var res = cmd.ExecuteScalar();
        return res is null || res == DBNull.Value ? null : res.ToString();
    }

    // ===================== Mốc số yêu cầu theo shop =====================

    [Fact]
    public void ReturnCount_ChuaTungCheck_TraNull()
    {
        using var temp = new TempDatabase();
        var repo = new ResultsRepository(temp.Open());
        repo.UpsertShops(1, new[] { new ShopListItem("111", "Alina", "alina99.store") });

        // Shop ĐÃ có dòng nhưng chưa check trả hàng lần nào → null (lượt đầu chỉ ghi nhớ, không check dòng nào).
        Assert.Null(repo.GetReturnCount(1, "alina99.store"));
        // Shop chưa có dòng nào → cũng null.
        Assert.Null(repo.GetReturnCount(1, "chua.co"));
    }

    [Fact]
    public void ReturnCount_GhiRoiDoc_TheoTungShop()
    {
        using var temp = new TempDatabase();
        var repo = new ResultsRepository(temp.Open());
        repo.UpsertShops(1, new[]
        {
            new ShopListItem("111", "Alina", "alina99.store"),
            new ShopListItem("222", "Shop 9X", "shop9x.store"),
        });

        repo.SetReturnCount(1, "alina99.store", 7);
        repo.SetReturnCount(1, "shop9x.store", 36);

        Assert.Equal(7, repo.GetReturnCount(1, "alina99.store"));
        Assert.Equal(36, repo.GetReturnCount(1, "shop9x.store"));
        // Mốc là của SHOP, không phải của tài khoản → tài khoản khác không thấy.
        Assert.Null(repo.GetReturnCount(2, "alina99.store"));
    }

    [Fact]
    public void ReturnCount_SongQuaKhoiDongLai()
    {
        using var temp = new TempDatabase();
        new ResultsRepository(temp.Open()).SetReturnCount(1, "alina99.store", 12);

        // Mở lại DB (mô phỏng khởi động lại app) → mốc còn nguyên.
        Assert.Equal(12, new ResultsRepository(temp.Open()).GetReturnCount(1, "alina99.store"));
    }

    [Fact]
    public void ReturnCount_GhiMocKhongLamHongDanhSachShop()
    {
        using var temp = new TempDatabase();
        var repo = new ResultsRepository(temp.Open());
        repo.UpsertShops(1, new[] { new ShopListItem("111", "Alina Store1", "alina99.store") });

        repo.SetReturnCount(1, "alina99.store", 5);

        var shop = Assert.Single(repo.GetShops(1));
        Assert.Equal("alina99.store", shop.ShopLogin);
        Assert.Equal("Alina Store1", shop.ShopName); // tên hiển thị KHÔNG bị xoá bởi lượt ghi mốc
    }

    [Fact]
    public void ReturnCount_ShopChuaCoDong_VanLuuDuocMoc()
    {
        using var temp = new TempDatabase();
        var repo = new ResultsRepository(temp.Open());

        repo.SetReturnCount(1, "shop.moi", 3);

        Assert.Equal(3, repo.GetReturnCount(1, "shop.moi"));
    }

    [Fact]
    public void ReturnCount_ShopLoginRong_BoQua_KhongNem()
    {
        using var temp = new TempDatabase();
        var repo = new ResultsRepository(temp.Open());

        var ex = Record.Exception(() => repo.SetReturnCount(1, "  ", 9));

        Assert.Null(ex);
        Assert.Null(repo.GetReturnCount(1, "  "));
    }

    // ===================== Lưu mã yêu cầu vào đơn =====================

    [Fact]
    public void SetReturnRequestCodes_GhiMaVaoDungDon()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Don("D1"), Don("D2") }, DateTime.UtcNow);

        var kq = repo.SetReturnRequestCodes(1, new[] { ("D1", "R001") });

        Assert.Equal(1, kq.DaGhi);
        Assert.Equal(0, kq.KhongDoi);
        Assert.Equal(0, kq.KhongCoDon);

        var rows = repo.Query(accountId: 1);
        Assert.Equal("R001", rows.Single(r => r.OrderSn == "D1").ReturnRequestCode);
        Assert.Null(rows.Single(r => r.OrderSn == "D2").ReturnRequestCode);
    }

    [Fact]
    public void SetReturnRequestCodes_MaKhongDoi_KhongGhiLai()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Don("D1") }, DateTime.UtcNow);
        repo.SetReturnRequestCodes(1, new[] { ("D1", "R001") });
        repo.MarkHubSynced(1, new[] { "D1" }, DateTime.UtcNow);

        var kq = repo.SetReturnRequestCodes(1, new[] { ("D1", "R001") });

        Assert.Equal(0, kq.DaGhi);
        Assert.Equal(1, kq.KhongDoi);
        // Mã không đổi → KHÔNG reset cờ hub (đừng đẩy lại vô ích).
        Assert.Empty(repo.GetForHubPush(1));
    }

    [Fact]
    public void SetReturnRequestCodes_MaDoi_GhiDe_VaResetCoDeDayLai()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Don("D1") }, DateTime.UtcNow);
        repo.SetReturnRequestCodes(1, new[] { ("D1", "R001") });
        repo.MarkHubSynced(1, new[] { "D1" }, DateTime.UtcNow);
        repo.MarkGsheetSynced(1, "D1", null, daHuy: false, coVanDon: false, coUocTinh: false,
            coDonTraHang: true, tab: "Tháng 07-2026", at: DateTime.UtcNow);
        // Vừa đẩy sheet KÈM mã → cờ = 1 (đọc đúng cột, không lệch chỉ số reader).
        Assert.Equal(1, Assert.Single(repo.GetForGsheetPush(1)).GsheetDaCoDonTraHang);

        // Yêu cầu trả hàng được tạo LẠI với mã khác → ghi đè.
        var kq = repo.SetReturnRequestCodes(1, new[] { ("D1", "R002") });

        Assert.Equal(1, kq.DaGhi);
        Assert.Equal("R002", Assert.Single(repo.Query(accountId: 1)).ReturnRequestCode);
        // Cờ reset → lượt kế đẩy LẠI lên hub và lên Google Sheet.
        Assert.Equal(new[] { "D1" }, repo.GetForHubPush(1).Select(o => o.OrderSn));
        Assert.Null(Assert.Single(repo.GetForGsheetPush(1)).GsheetDaCoDonTraHang);
    }

    [Fact]
    public void SetReturnRequestCodes_MaRong_KhongGhiDe()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Don("D1") }, DateTime.UtcNow);
        repo.SetReturnRequestCodes(1, new[] { ("D1", "R001") });

        var kq = repo.SetReturnRequestCodes(1, new[] { ("D1", "   "), ("D1", "") });

        Assert.Equal(0, kq.DaGhi);
        Assert.Equal("R001", Assert.Single(repo.Query(accountId: 1)).ReturnRequestCode); // GIỮ mã cũ
    }

    [Fact]
    public void SetReturnRequestCodes_DonKhongCoTrongDb_BoQua_DemRieng_KhongTaoDonMoi()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Don("D1") }, DateTime.UtcNow);

        var kq = repo.SetReturnRequestCodes(1, new[] { ("DA-BI-DON", "R009"), ("D1", "R001") });

        Assert.Equal(1, kq.DaGhi);
        Assert.Equal(1, kq.KhongCoDon);
        Assert.Single(repo.Query(accountId: 1)); // KHÔNG đẻ thêm đơn mới
    }

    [Fact]
    public void SetReturnRequestCodes_DonCuaTaiKhoanKhac_KhongDungToi()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Don("D1") }, DateTime.UtcNow);
        repo.UpsertMany(2, new[] { Don("D1") }, DateTime.UtcNow);

        repo.SetReturnRequestCodes(1, new[] { ("D1", "R001") });

        Assert.Equal("R001", Assert.Single(repo.Query(accountId: 1)).ReturnRequestCode);
        Assert.Null(Assert.Single(repo.Query(accountId: 2)).ReturnRequestCode);
    }

    [Fact]
    public void SetReturnRequestCodes_MaTheoDenHubQuaGetForHubPush()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Don("D1") }, DateTime.UtcNow);
        repo.SetReturnRequestCodes(1, new[] { ("D1", "R001") });

        Assert.Equal("R001", Assert.Single(repo.GetForHubPush(1)).ReturnRequestCode);
    }

    [Fact]
    public void SetReturnRequestCodes_LoRong_KhongNem()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());

        var kq = repo.SetReturnRequestCodes(1, Array.Empty<(string, string)>());

        Assert.Equal(0, kq.DaGhi + kq.KhongDoi + kq.KhongCoDon);
    }

    // ===================== Migration cột mới cho DB CŨ =====================

    /// <summary>Dựng schema orders CŨ (chưa có return_request_code / gsheet_da_co_don_tra_hang) + 1 đơn sẵn.</summary>
    private static void DungSchemaCu(string path, long accountId, string orderSn)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
CREATE TABLE orders (
    id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id         INTEGER NOT NULL,
    order_sn           TEXT NOT NULL,
    shopee_order_id    TEXT,
    buyer_username     TEXT,
    items_json         TEXT,
    item_count         INTEGER,
    item_summary       TEXT,
    total_price        INTEGER,
    total_price_text   TEXT,
    payment_method     TEXT,
    status             TEXT,
    status_description TEXT,
    cancel_reason      TEXT,
    channel            TEXT,
    carrier            TEXT,
    tracking_number    TEXT,
    synced_at          TEXT,
    created_at         TEXT,
    updated_at         TEXT,
    UNIQUE(account_id, order_sn)
);
CREATE TABLE account_shops (
    account_id INTEGER NOT NULL,
    shop_login TEXT NOT NULL,
    shop_name  TEXT,
    updated_at TEXT NOT NULL,
    UNIQUE(account_id, shop_login)
);";
            cmd.ExecuteNonQuery();
        }
        using (var ins = conn.CreateCommand())
        {
            ins.CommandText = @"INSERT INTO orders (account_id, order_sn, total_price, status, synced_at, created_at, updated_at)
    VALUES ($acc, $sn, 166500, 'Chờ lấy hàng',
            '2026-07-28T00:00:00.0000000', '2026-07-28T00:00:00.0000000', '2026-07-28T00:00:00.0000000');";
            ins.Parameters.AddWithValue("$acc", accountId);
            ins.Parameters.AddWithValue("$sn", orderSn);
            ins.ExecuteNonQuery();
        }
        using (var ins = conn.CreateCommand())
        {
            ins.CommandText = @"INSERT INTO account_shops (account_id, shop_login, shop_name, updated_at)
    VALUES ($acc, 'alina99.store', 'Alina', '2026-07-28T00:00:00.0000000');";
            ins.Parameters.AddWithValue("$acc", accountId);
            ins.ExecuteNonQuery();
        }
    }

    private static bool CoCot(string path, string table, string column)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    [Fact]
    public void Migration_DbCu_ThemDu3Cot_KhongMatDuLieu()
    {
        using var temp = new TempDatabase();
        DungSchemaCu(temp.Path, accountId: 5, orderSn: "SNCU");

        Assert.False(CoCot(temp.Path, "orders", "return_request_code"));
        Assert.False(CoCot(temp.Path, "orders", "gsheet_da_co_don_tra_hang"));
        Assert.False(CoCot(temp.Path, "account_shops", "return_count_last"));

        _ = new Database(temp.Path); // Initialize() chạy migration ALTER TABLE

        Assert.True(CoCot(temp.Path, "orders", "return_request_code"));
        Assert.True(CoCot(temp.Path, "orders", "gsheet_da_co_don_tra_hang"));
        Assert.True(CoCot(temp.Path, "account_shops", "return_count_last"));

        // Dữ liệu cũ CÒN NGUYÊN, cột mới mặc định NULL (không backfill).
        var row = Assert.Single(new OrdersRepository(new Database(temp.Path)).Query(accountId: 5));
        Assert.Equal("SNCU", row.OrderSn);
        Assert.Equal(166500, row.TotalPrice);
        Assert.Null(row.ReturnRequestCode);
        Assert.Null(DocCot(temp.Path, "SNCU", "return_request_code"));

        var results = new ResultsRepository(new Database(temp.Path));
        Assert.Null(results.GetReturnCount(5, "alina99.store")); // shop cũ = chưa từng check
        Assert.Equal("Alina", Assert.Single(results.GetShops(5)).ShopName);
    }

    [Fact]
    public void Migration_DbCu_SauMigration_GhiDocBinhThuong()
    {
        using var temp = new TempDatabase();
        DungSchemaCu(temp.Path, accountId: 5, orderSn: "SNCU");

        var db = new Database(temp.Path); // migration chạy tại đây
        new OrdersRepository(db).SetReturnRequestCodes(5, new[] { ("SNCU", "R123") });
        new ResultsRepository(db).SetReturnCount(5, "alina99.store", 4);

        Assert.Equal("R123", Assert.Single(new OrdersRepository(db).Query(accountId: 5)).ReturnRequestCode);
        Assert.Equal(4, new ResultsRepository(db).GetReturnCount(5, "alina99.store"));
    }
}
