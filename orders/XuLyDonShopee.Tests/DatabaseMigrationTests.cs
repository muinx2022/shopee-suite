using Microsoft.Data.Sqlite;
using XuLyDonShopee.Core.Data;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test migration cột <c>ProxyKey</c> cho bảng <c>accounts</c>: DB CŨ (chưa có cột) phải được thêm cột
/// bằng ALTER TABLE ADD COLUMN mà KHÔNG mất dữ liệu; chạy nhiều lần idempotent.
/// </summary>
public class DatabaseMigrationTests
{
    /// <summary>Dựng schema CŨ (bảng accounts KHÔNG có cột ProxyKey) rồi ghi 1 dòng dữ liệu sẵn.</summary>
    private static void CreateOldSchemaWithRow(string path, string email)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var conn = new SqliteConnection(cs);
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
CREATE TABLE accounts (
    Id         INTEGER PRIMARY KEY AUTOINCREMENT,
    Email      TEXT NOT NULL,
    Password   TEXT NOT NULL,
    Phone      TEXT,
    Cookie     TEXT,
    Note       TEXT,
    Status     TEXT NOT NULL,
    CreatedAt  TEXT NOT NULL,
    UpdatedAt  TEXT NOT NULL
);";
            cmd.ExecuteNonQuery();
        }

        using (var ins = conn.CreateCommand())
        {
            ins.CommandText = @"INSERT INTO accounts (Email, Password, Phone, Cookie, Note, Status, CreatedAt, UpdatedAt)
                                VALUES ($e, 'matkhau', '0900', 'cookie-cu', 'ghi chu cu', 'HoatDong',
                                        '2020-01-01T00:00:00.0000000', '2020-01-01T00:00:00.0000000');";
            ins.Parameters.AddWithValue("$e", email);
            ins.ExecuteNonQuery();
        }
    }

    /// <summary>Dựng schema orders CŨ (thiếu cột final_amount / final_amount_text) rồi ghi 1 đơn dữ liệu sẵn.</summary>
    private static void CreateOldOrdersSchemaWithRow(string path, long accountId, string orderSn)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var conn = new SqliteConnection(cs);
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            // Bản schema orders TRƯỚC khi thêm 2 cột final_* (đủ các cột cũ, KHÔNG có final_amount/final_amount_text).
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
);";
            cmd.ExecuteNonQuery();
        }

        using (var ins = conn.CreateCommand())
        {
            ins.CommandText = @"INSERT INTO orders
    (account_id, order_sn, total_price, total_price_text, status, synced_at, created_at, updated_at)
    VALUES ($acc, $sn, 166500, '₫166.500', 'Chờ lấy hàng',
            '2026-07-16T00:00:00.0000000', '2026-07-16T00:00:00.0000000', '2026-07-16T00:00:00.0000000');";
            ins.Parameters.AddWithValue("$acc", accountId);
            ins.Parameters.AddWithValue("$sn", orderSn);
            ins.ExecuteNonQuery();
        }
    }

    /// <summary>Kiểm tra bảng có cột hay không qua PRAGMA table_info (cột name ở chỉ số 1).</summary>
    private static bool HasColumn(string path, string table, string column)
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
    public void KhoiTao_DbCu_ThieuProxyKey_DuocThemCot_KhongMatDuLieu()
    {
        using var temp = new TempDatabase();
        CreateOldSchemaWithRow(temp.Path, "old@x.com");

        // Trước migration: schema cũ chưa có cột ProxyKey.
        Assert.False(HasColumn(temp.Path, "accounts", "ProxyKey"));

        // Khởi tạo Database mới trỏ cùng file → Initialize() chạy migration.
        _ = new Database(temp.Path);

        // Sau migration: đã có cột ProxyKey.
        Assert.True(HasColumn(temp.Path, "accounts", "ProxyKey"));

        // Dữ liệu cũ CÒN NGUYÊN; ProxyKey mặc định null.
        var repo = new AccountRepository(new Database(temp.Path));
        var all = repo.GetAll();
        Assert.Single(all);
        var acc = all[0];
        Assert.Equal("old@x.com", acc.Email);
        Assert.Equal("cookie-cu", acc.Cookie);
        Assert.Equal("ghi chu cu", acc.Note);
        Assert.Equal(AccountStatus.HoatDong, acc.Status);
        Assert.Null(acc.ProxyKey);
    }

    [Fact]
    public void KhoiTao_DbCu_SauMigration_GhiDocProxyKeyBinhThuong()
    {
        using var temp = new TempDatabase();
        CreateOldSchemaWithRow(temp.Path, "old@x.com");

        var repo = new AccountRepository(new Database(temp.Path)); // migration chạy tại đây
        var acc = repo.GetAll()[0];

        acc.ProxyKey = "KEY-SAU-MIGRATION";
        repo.Update(acc);

        Assert.Equal("KEY-SAU-MIGRATION", repo.GetById(acc.Id)!.ProxyKey);
    }

    [Fact]
    public void KhoiTao_NhieuLan_Idempotent_KhongNem()
    {
        using var temp = new TempDatabase();

        var ex = Record.Exception(() =>
        {
            _ = new Database(temp.Path); // tạo mới (đã có ProxyKey trong CREATE TABLE)
            _ = new Database(temp.Path); // chạy lại migration lần 2 — không được ném
            _ = new Database(temp.Path); // lần 3
        });

        Assert.Null(ex);
        Assert.True(HasColumn(temp.Path, "accounts", "ProxyKey"));
        Assert.True(HasColumn(temp.Path, "accounts", "PickupAddress"));
    }

    [Fact]
    public void KhoiTao_DbCu_ThieuPickupAddress_DuocThemCot_KhongMatDuLieu()
    {
        using var temp = new TempDatabase();
        CreateOldSchemaWithRow(temp.Path, "old@x.com");

        // Trước migration: schema cũ chưa có cột PickupAddress.
        Assert.False(HasColumn(temp.Path, "accounts", "PickupAddress"));

        // Khởi tạo Database mới trỏ cùng file → Initialize() chạy migration.
        _ = new Database(temp.Path);

        // Sau migration: đã có cột PickupAddress.
        Assert.True(HasColumn(temp.Path, "accounts", "PickupAddress"));

        // Dữ liệu cũ CÒN NGUYÊN; PickupAddress mặc định null.
        var repo = new AccountRepository(new Database(temp.Path));
        var all = repo.GetAll();
        Assert.Single(all);
        var acc = all[0];
        Assert.Equal("old@x.com", acc.Email);
        Assert.Equal("cookie-cu", acc.Cookie);
        Assert.Equal("ghi chu cu", acc.Note);
        Assert.Equal(AccountStatus.HoatDong, acc.Status);
        Assert.Null(acc.PickupAddress);
    }

    [Fact]
    public void KhoiTao_DbCu_SauMigration_GhiDocPickupAddressBinhThuong()
    {
        using var temp = new TempDatabase();
        CreateOldSchemaWithRow(temp.Path, "old@x.com");

        var repo = new AccountRepository(new Database(temp.Path)); // migration chạy tại đây
        var acc = repo.GetAll()[0];

        acc.PickupAddress = "Hà Nội";
        repo.Update(acc);

        Assert.Equal("Hà Nội", repo.GetById(acc.Id)!.PickupAddress);
    }

    // ===================== Migration cột final_amount cho bảng orders =====================

    [Fact]
    public void KhoiTao_DbCu_Orders_ThieuFinalAmount_DuocThemCot_KhongMatDuLieu()
    {
        using var temp = new TempDatabase();
        CreateOldOrdersSchemaWithRow(temp.Path, accountId: 5, orderSn: "SNOLD");

        // Trước migration: schema orders cũ chưa có cột final_amount / final_amount_text.
        Assert.False(HasColumn(temp.Path, "orders", "final_amount"));
        Assert.False(HasColumn(temp.Path, "orders", "final_amount_text"));

        // Khởi tạo Database mới trỏ cùng file → Initialize() chạy migration ALTER TABLE.
        _ = new Database(temp.Path);

        // Sau migration: đã có 2 cột.
        Assert.True(HasColumn(temp.Path, "orders", "final_amount"));
        Assert.True(HasColumn(temp.Path, "orders", "final_amount_text"));

        // Dữ liệu đơn cũ CÒN NGUYÊN; final_amount mặc định null.
        var repo = new OrdersRepository(new Database(temp.Path));
        var row = Assert.Single(repo.Query(accountId: 5));
        Assert.Equal("SNOLD", row.OrderSn);
        Assert.Equal(166500, row.TotalPrice);
        Assert.Null(row.FinalAmount);
        Assert.Null(row.FinalAmountText);
    }

    [Fact]
    public void KhoiTao_DbCu_Orders_SauMigration_UpsertFinalAmountBinhThuong()
    {
        using var temp = new TempDatabase();
        CreateOldOrdersSchemaWithRow(temp.Path, accountId: 5, orderSn: "SNOLD");

        var repo = new OrdersRepository(new Database(temp.Path)); // migration chạy tại đây
        repo.UpsertMany(5, new[]
        {
            new SyncedOrder { OrderSn = "SNOLD", FinalAmount = 292010, FinalAmountText = "₫292.010" }
        }, DateTime.UtcNow);

        var row = Assert.Single(repo.Query(accountId: 5));
        Assert.Equal(292010, row.FinalAmount);
        Assert.Equal("₫292.010", row.FinalAmountText);
    }

    // ==== Migration 4 cột gsheet_synced_at / gsheet_file_url / gsheet_da_huy / gsheet_da_co_van_don cho bảng orders ====

    [Fact]
    public void KhoiTao_DbCu_Orders_ThieuCotGsheet_DuocThemCot_KhongMatDuLieu()
    {
        using var temp = new TempDatabase();
        CreateOldOrdersSchemaWithRow(temp.Path, accountId: 9, orderSn: "SNGS");

        // Trước migration: schema orders cũ chưa có 4 cột cờ đẩy Google Sheet.
        Assert.False(HasColumn(temp.Path, "orders", "gsheet_synced_at"));
        Assert.False(HasColumn(temp.Path, "orders", "gsheet_file_url"));
        Assert.False(HasColumn(temp.Path, "orders", "gsheet_da_huy"));
        Assert.False(HasColumn(temp.Path, "orders", "gsheet_da_co_van_don"));

        // Khởi tạo Database mới trỏ cùng file → Initialize() chạy migration ALTER TABLE.
        _ = new Database(temp.Path);

        // Sau migration: đã có 4 cột; đơn cũ CÒN NGUYÊN.
        Assert.True(HasColumn(temp.Path, "orders", "gsheet_synced_at"));
        Assert.True(HasColumn(temp.Path, "orders", "gsheet_file_url"));
        Assert.True(HasColumn(temp.Path, "orders", "gsheet_da_huy"));
        Assert.True(HasColumn(temp.Path, "orders", "gsheet_da_co_van_don"));

        var repo = new OrdersRepository(new Database(temp.Path));
        var row = Assert.Single(repo.Query(accountId: 9));
        Assert.Equal("SNGS", row.OrderSn);

        // GetForGsheetPush đọc được (superset trả cả đơn không tracking) — chỉ cần KHÔNG ném.
        var pending = repo.GetForGsheetPush(9);
        Assert.Single(pending); // đơn cũ SNGS vẫn trả dù không có vận đơn
    }
}
