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
        Assert.True(HasColumn(temp.Path, "accounts", "VerifyEmail"));
        Assert.True(HasColumn(temp.Path, "accounts", "VerifyEmailPassword"));
        Assert.True(HasColumn(temp.Path, "accounts", "verify_failed_at"));
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

    // ===================== Migration 2 cột VerifyEmail / VerifyEmailPassword cho bảng accounts =====================

    [Fact]
    public void KhoiTao_DbCu_ThieuVerifyEmail_DuocThemCot_KhongMatDuLieu()
    {
        using var temp = new TempDatabase();
        CreateOldSchemaWithRow(temp.Path, "old@x.com");

        // Trước migration: schema cũ chưa có 2 cột email xác minh.
        Assert.False(HasColumn(temp.Path, "accounts", "VerifyEmail"));
        Assert.False(HasColumn(temp.Path, "accounts", "VerifyEmailPassword"));

        // Khởi tạo Database mới trỏ cùng file → Initialize() chạy migration.
        _ = new Database(temp.Path);

        // Sau migration: đã có 2 cột.
        Assert.True(HasColumn(temp.Path, "accounts", "VerifyEmail"));
        Assert.True(HasColumn(temp.Path, "accounts", "VerifyEmailPassword"));

        // Dữ liệu cũ CÒN NGUYÊN; email xác minh mặc định "" (Map null → "").
        var repo = new AccountRepository(new Database(temp.Path));
        var all = repo.GetAll();
        Assert.Single(all);
        var acc = all[0];
        Assert.Equal("old@x.com", acc.Email);
        Assert.Equal("cookie-cu", acc.Cookie);
        Assert.Equal(AccountStatus.HoatDong, acc.Status);
        Assert.Equal("", acc.VerifyEmail);
        Assert.Equal("", acc.VerifyEmailPassword);
    }

    [Fact]
    public void KhoiTao_DbCu_SauMigration_GhiDocVerifyEmailBinhThuong()
    {
        using var temp = new TempDatabase();
        CreateOldSchemaWithRow(temp.Path, "old@x.com");

        var repo = new AccountRepository(new Database(temp.Path)); // migration chạy tại đây
        var acc = repo.GetAll()[0];

        acc.VerifyEmail = "verify@hotmail.com";
        acc.VerifyEmailPassword = "mkemail";
        repo.Update(acc);

        var reloaded = repo.GetById(acc.Id)!;
        Assert.Equal("verify@hotmail.com", reloaded.VerifyEmail);
        Assert.Equal("mkemail", reloaded.VerifyEmailPassword);
    }

    // ===================== Migration cột verify_failed_at cho bảng accounts =====================

    [Fact]
    public void KhoiTao_DbCu_ThieuVerifyFailedAt_DuocThemCot_KhongMatDuLieu()
    {
        using var temp = new TempDatabase();
        CreateOldSchemaWithRow(temp.Path, "old@x.com");

        // Trước migration: schema cũ chưa có cột verify_failed_at.
        Assert.False(HasColumn(temp.Path, "accounts", "verify_failed_at"));

        // Khởi tạo Database mới trỏ cùng file → Initialize() chạy migration.
        _ = new Database(temp.Path);

        // Sau migration: đã có cột.
        Assert.True(HasColumn(temp.Path, "accounts", "verify_failed_at"));

        // Dữ liệu cũ CÒN NGUYÊN; cờ mặc định null (không đánh dấu).
        var repo = new AccountRepository(new Database(temp.Path));
        var acc = Assert.Single(repo.GetAll());
        Assert.Equal("old@x.com", acc.Email);
        Assert.Equal("cookie-cu", acc.Cookie);
        Assert.Equal(AccountStatus.HoatDong, acc.Status);
        Assert.Null(acc.VerifyFailedAt);
    }

    [Fact]
    public void KhoiTao_DbCu_SauMigration_MarkVerifyFailedBinhThuong()
    {
        using var temp = new TempDatabase();
        CreateOldSchemaWithRow(temp.Path, "old@x.com");

        var repo = new AccountRepository(new Database(temp.Path)); // migration chạy tại đây
        var acc = repo.GetAll()[0];

        var at = new DateTime(2026, 7, 23, 10, 0, 0, DateTimeKind.Utc);
        repo.MarkVerifyFailed(acc.Id, at);

        var reloaded = repo.GetById(acc.Id)!;
        Assert.NotNull(reloaded.VerifyFailedAt);
        Assert.Equal(at, reloaded.VerifyFailedAt);
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

    // ==== Migration cột gsheet_da_co_uoc_tinh cho bảng orders (đẩy lại sheet khi số "Ước tính" xuất hiện sau) ====

    [Fact]
    public void KhoiTao_DbCu_Orders_ThieuGsheetDaCoUocTinh_DuocThemCot_KhongMatDuLieu_DonCuDuDieuKienDayLai()
    {
        using var temp = new TempDatabase();
        CreateOldOrdersSchemaWithRow(temp.Path, accountId: 51, orderSn: "SNUT");

        // Trước migration: schema orders cũ chưa có cột gsheet_da_co_uoc_tinh.
        Assert.False(HasColumn(temp.Path, "orders", "gsheet_da_co_uoc_tinh"));

        // Khởi tạo Database mới trỏ cùng file → Initialize() chạy migration ALTER TABLE.
        _ = new Database(temp.Path);

        // Sau migration: đã có cột; đơn cũ CÒN NGUYÊN; cột mặc định NULL (không backfill).
        Assert.True(HasColumn(temp.Path, "orders", "gsheet_da_co_uoc_tinh"));
        Assert.Null(ReadOrderColumn(temp.Path, "SNUT", "gsheet_da_co_uoc_tinh"));

        var repo = new OrdersRepository(new Database(temp.Path));
        var row = Assert.Single(repo.Query(accountId: 51));
        Assert.Equal("SNUT", row.OrderSn);
        Assert.Equal(166500, row.TotalPrice);

        // Đơn cũ có cờ NULL (!= 1) → khi ước tính xuất hiện sẽ được đẩy LẠI một lần để điền đúng số tiền.
        var p = Assert.Single(repo.GetForGsheetPush(51));
        Assert.Null(p.GsheetDaCoUocTinh);
    }

    // ==== Migration cột hub_rev + cho_day cho bảng pickup_address_alerts (banner lỗi địa chỉ theo rev/outbox) ====

    /// <summary>
    /// DB đời trước (bảng banner chỉ có 5 cột) phải được thêm <c>hub_rev</c> + <c>cho_day</c> mà KHÔNG mất
    /// banner đang có. Dòng cũ nhận 0/0: <c>hub_rev=0</c> nên lượt ghi kế trên Hub (rev ≥ 1) chắc chắn lớn hơn
    /// → máy vẫn nhận được thay đổi; <c>cho_day=0</c> nên không đẩy nhầm dòng đã đồng bộ từ đời cũ.
    /// </summary>
    [Fact]
    public void KhoiTao_DbCu_PickupAlerts_ThieuHubRevVaChoDay_DuocThemCot_KhongMatDuLieu()
    {
        using var temp = new TempDatabase();
        var cs = new SqliteConnectionStringBuilder { DataSource = temp.Path }.ToString();
        using (var conn = new SqliteConnection(cs))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE pickup_address_alerts (
    account_id INTEGER NOT NULL,
    shop_login TEXT NOT NULL,
    province TEXT DEFAULT '',
    created_at TEXT NOT NULL,
    dismissed_at TEXT,
    PRIMARY KEY(account_id, shop_login));
INSERT INTO pickup_address_alerts(account_id, shop_login, province, created_at, dismissed_at)
VALUES(7, 'cu.store', 'Thanh Hoa', '2026-08-01T00:00:00.0000000Z', NULL);";
            cmd.ExecuteNonQuery();
        }

        Assert.False(HasColumn(temp.Path, "pickup_address_alerts", "hub_rev"));
        Assert.False(HasColumn(temp.Path, "pickup_address_alerts", "cho_day"));

        var db = new Database(temp.Path);

        Assert.True(HasColumn(temp.Path, "pickup_address_alerts", "hub_rev"));
        Assert.True(HasColumn(temp.Path, "pickup_address_alerts", "cho_day"));

        var repo = new PickupAddressAlertsRepository(db);
        var row = Assert.Single(repo.ListActive(7));
        Assert.Equal("cu.store", row.ShopLogin);
        Assert.Equal("Thanh Hoa", row.Province);
        Assert.Equal(0, row.HubRev);
        Assert.False(row.ChoDay);
        Assert.Empty(repo.ListChoDay(7));   // dòng đời cũ KHÔNG bị coi là đang chờ đẩy
    }

    // ==== Migration cột hub_synced_at cho bảng orders (đẩy đơn lên hub) ====

    [Fact]
    public void KhoiTao_DbCu_Orders_ThieuHubSyncedAt_DuocThemCot_KhongMatDuLieu_PendingHoatDong()
    {
        using var temp = new TempDatabase();
        CreateOldOrdersSchemaWithRow(temp.Path, accountId: 11, orderSn: "SNHUB");

        // Trước migration: schema orders cũ chưa có cột hub_synced_at.
        Assert.False(HasColumn(temp.Path, "orders", "hub_synced_at"));

        // Khởi tạo Database mới trỏ cùng file → Initialize() chạy migration ALTER TABLE.
        _ = new Database(temp.Path);

        // Sau migration: đã có cột; đơn cũ CÒN NGUYÊN.
        Assert.True(HasColumn(temp.Path, "orders", "hub_synced_at"));

        var repo = new OrdersRepository(new Database(temp.Path));
        var row = Assert.Single(repo.Query(accountId: 11));
        Assert.Equal("SNHUB", row.OrderSn);

        // hub_synced_at mặc định NULL → đơn cũ vào hàng đợi đẩy hub (GetForHubPush trả về).
        var pending = repo.GetForHubPush(11);
        Assert.Equal(new[] { "SNHUB" }, pending.Select(o => o.OrderSn));

        // Đánh dấu → biến khỏi hàng đợi (chống đẩy trùng).
        repo.MarkHubSynced(11, new[] { "SNHUB" }, DateTime.UtcNow);
        Assert.Empty(repo.GetForHubPush(11));
    }

    // ==== Migration cặp cột "thế hệ" hub_push_gen / hub_push_gen_sent (chống đua khi đẩy hub) ====

    [Fact]
    public void KhoiTao_DbCu_Orders_ThieuHubPushGen_DuocThemCot_ChayLai2LanKhongDoiGi()
    {
        using var temp = new TempDatabase();
        CreateOldOrdersSchemaWithRow(temp.Path, accountId: 21, orderSn: "SNGEN");
        Assert.False(HasColumn(temp.Path, "orders", "hub_push_gen"));
        Assert.False(HasColumn(temp.Path, "orders", "hub_push_gen_sent"));

        _ = new Database(temp.Path);

        Assert.True(HasColumn(temp.Path, "orders", "hub_push_gen"));
        Assert.True(HasColumn(temp.Path, "orders", "hub_push_gen_sent"));
        // Đơn CŨ: thế hệ 0, chưa từng chụp (NULL) — vẫn đẩy được lên hub như trước.
        Assert.Equal(0L, Convert.ToInt64(ReadOrderColumn(temp.Path, "SNGEN", "hub_push_gen")));
        Assert.Null(ReadOrderColumn(temp.Path, "SNGEN", "hub_push_gen_sent"));

        var repo = new OrdersRepository(new Database(temp.Path));
        Assert.Equal(new[] { "SNGEN" }, repo.GetForHubPush(21).Select(o => o.OrderSn));
        repo.MarkHubSynced(21, new[] { "SNGEN" }, DateTime.UtcNow);
        Assert.Empty(repo.GetForHubPush(21));

        // IDEMPOTENT: mở lại DB lần nữa (migration chạy lại) → không lỗi, không đổi thêm gì.
        var genTruoc = ReadOrderColumn(temp.Path, "SNGEN", "hub_push_gen");
        var syncedTruoc = ReadOrderColumn(temp.Path, "SNGEN", "hub_synced_at");
        _ = new Database(temp.Path);
        Assert.Equal(genTruoc, ReadOrderColumn(temp.Path, "SNGEN", "hub_push_gen"));
        Assert.Equal(syncedTruoc, ReadOrderColumn(temp.Path, "SNGEN", "hub_synced_at"));
        Assert.Empty(new OrdersRepository(new Database(temp.Path)).GetForHubPush(21));
    }

    // ==== Migration cột hub_slip_synced_at cho bảng orders (đẩy FILE PHIẾU lên hub) ====

    [Fact]
    public void KhoiTao_DbCu_Orders_ThieuHubSlipSyncedAt_DuocThemCot_KhongMatDuLieu()
    {
        using var temp = new TempDatabase();
        CreateOldOrdersSchemaWithRow(temp.Path, accountId: 21, orderSn: "SNSLIP");

        // Trước migration: schema orders cũ chưa có cột hub_slip_synced_at.
        Assert.False(HasColumn(temp.Path, "orders", "hub_slip_synced_at"));

        // Khởi tạo Database mới trỏ cùng file → Initialize() chạy migration ALTER TABLE.
        _ = new Database(temp.Path);

        // Sau migration: đã có cột; đơn cũ CÒN NGUYÊN.
        Assert.True(HasColumn(temp.Path, "orders", "hub_slip_synced_at"));

        var repo = new OrdersRepository(new Database(temp.Path));
        var row = Assert.Single(repo.Query(accountId: 21));
        Assert.Equal("SNSLIP", row.OrderSn);

        // Đơn cũ chưa lên hub + không vận đơn → KHÔNG lọt hàng đợi đẩy phiếu; GetForHubSlipPush chỉ cần KHÔNG ném.
        Assert.Empty(repo.GetForHubSlipPush(21));
    }

    // ==== Migration cột sold_counted_at cho bảng orders (+1 "Đã bán" theo SKU) ====

    [Fact]
    public void KhoiTao_DbCu_Orders_ThieuSoldCountedAt_DuocThemCot_KhongMatDuLieu()
    {
        using var temp = new TempDatabase();
        CreateOldOrdersSchemaWithRow(temp.Path, accountId: 13, orderSn: "SNSOLD");

        // Trước migration: schema orders cũ chưa có cột sold_counted_at.
        Assert.False(HasColumn(temp.Path, "orders", "sold_counted_at"));

        // Khởi tạo Database mới trỏ cùng file → Initialize() chạy migration ALTER TABLE.
        _ = new Database(temp.Path);

        // Sau migration: đã có cột; đơn cũ CÒN NGUYÊN.
        Assert.True(HasColumn(temp.Path, "orders", "sold_counted_at"));

        var repo = new OrdersRepository(new Database(temp.Path));
        var row = Assert.Single(repo.Query(accountId: 13));
        Assert.Equal("SNSOLD", row.OrderSn);
    }

    // ==== Migration + BACKFILL cột gsheet_tab cho bảng orders (nhớ tab đã ghi để không nhân đôi dòng khi sang tháng) ====

    /// <summary>
    /// Dựng schema orders CÓ nhóm cột gsheet (gsheet_synced_at…) NHƯNG chưa có gsheet_tab, kèm bảng settings;
    /// ghi 1 đơn ĐÃ ghi sheet (order_sn "SYNCED", gsheet_synced_at NOT NULL) + 1 đơn CHƯA ghi ("UNSYNCED",
    /// gsheet_synced_at NULL). <paramref name="tabSetting"/> != null → ghi setting <c>gsheet_tab_name</c>.
    /// </summary>
    private static void CreateOrdersSchemaBeforeGsheetTab(string path, long accountId, string? tabSetting)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var conn = new SqliteConnection(cs);
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
CREATE TABLE settings (
    key   TEXT PRIMARY KEY,
    value TEXT
);
CREATE TABLE orders (
    id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id         INTEGER NOT NULL,
    order_sn           TEXT NOT NULL,
    shopee_order_id    TEXT,
    buyer_username     TEXT,
    items_json         TEXT,
    item_count         INTEGER,
    item_summary       TEXT,
    sku                TEXT,
    gsheet_synced_at   TEXT,
    gsheet_file_url    TEXT,
    gsheet_da_huy      INTEGER,
    gsheet_da_co_van_don INTEGER,
    hub_synced_at      TEXT,
    hub_slip_synced_at TEXT,
    sold_counted_at    TEXT,
    total_price        INTEGER,
    total_price_text   TEXT,
    final_amount       INTEGER,
    final_amount_text  TEXT,
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

        // Đơn ĐÃ ghi sheet (gsheet_synced_at NOT NULL) → backfill sẽ nhớ tab.
        using (var ins = conn.CreateCommand())
        {
            ins.CommandText = @"INSERT INTO orders
    (account_id, order_sn, gsheet_synced_at, status, synced_at, created_at, updated_at)
    VALUES ($acc, 'SYNCED', '2026-06-01T00:00:00.0000000', 'Chờ lấy hàng',
            '2026-06-01T00:00:00.0000000', '2026-06-01T00:00:00.0000000', '2026-06-01T00:00:00.0000000');";
            ins.Parameters.AddWithValue("$acc", accountId);
            ins.ExecuteNonQuery();
        }

        // Đơn CHƯA ghi sheet (gsheet_synced_at NULL) → backfill KHÔNG chạm → gsheet_tab giữ NULL.
        using (var ins = conn.CreateCommand())
        {
            ins.CommandText = @"INSERT INTO orders
    (account_id, order_sn, status, synced_at, created_at, updated_at)
    VALUES ($acc, 'UNSYNCED', 'Chờ lấy hàng',
            '2026-06-01T00:00:00.0000000', '2026-06-01T00:00:00.0000000', '2026-06-01T00:00:00.0000000');";
            ins.Parameters.AddWithValue("$acc", accountId);
            ins.ExecuteNonQuery();
        }

        if (tabSetting is not null)
        {
            using var s = conn.CreateCommand();
            s.CommandText = "INSERT INTO settings (key, value) VALUES ('gsheet_tab_name', $v);";
            s.Parameters.AddWithValue("$v", tabSetting);
            s.ExecuteNonQuery();
        }
    }

    /// <summary>Đọc giá trị một cột của đơn theo order_sn (null nếu NULL/không có).</summary>
    private static string? ReadOrderColumn(string path, string orderSn, string column)
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

    [Fact]
    public void KhoiTao_DbCu_Orders_ThieuGsheetTab_DuocThemCot_BackfillTheoSetting_TrimKhoangTrang()
    {
        using var temp = new TempDatabase();
        CreateOrdersSchemaBeforeGsheetTab(temp.Path, accountId: 31, tabSetting: "  Tab Thang 6  ");

        // Trước migration: schema orders cũ chưa có cột gsheet_tab.
        Assert.False(HasColumn(temp.Path, "orders", "gsheet_tab"));

        // Khởi tạo Database mới trỏ cùng file → Initialize() chạy migration ALTER TABLE + backfill.
        _ = new Database(temp.Path);

        // Sau migration: đã có cột gsheet_tab.
        Assert.True(HasColumn(temp.Path, "orders", "gsheet_tab"));

        // Đơn ĐÃ ghi sheet → gsheet_tab = tên setting đã TRIM (khoảng trắng bị bỏ).
        Assert.Equal("Tab Thang 6", ReadOrderColumn(temp.Path, "SYNCED", "gsheet_tab"));

        // Đơn CHƯA ghi sheet → gsheet_tab giữ NULL (không backfill).
        Assert.Null(ReadOrderColumn(temp.Path, "UNSYNCED", "gsheet_tab"));
    }

    [Fact]
    public void KhoiTao_DbCu_Orders_ThieuGsheetTab_KhongSetting_BackfillMacDinhThang4()
    {
        using var temp = new TempDatabase();
        CreateOrdersSchemaBeforeGsheetTab(temp.Path, accountId: 32, tabSetting: null);

        _ = new Database(temp.Path);

        // Không có setting gsheet_tab_name → backfill dùng mặc định LEGACY "tháng 4".
        Assert.Equal("tháng 4", ReadOrderColumn(temp.Path, "SYNCED", "gsheet_tab"));
        Assert.Null(ReadOrderColumn(temp.Path, "UNSYNCED", "gsheet_tab"));
    }

    [Fact]
    public void KhoiTao_DbCu_Orders_SettingToanKhoangTrang_BackfillMacDinhThang4()
    {
        using var temp = new TempDatabase();
        CreateOrdersSchemaBeforeGsheetTab(temp.Path, accountId: 34, tabSetting: "    ");

        _ = new Database(temp.Path);

        // Setting toàn khoảng trắng → NULLIF(TRIM(...),'') coi như chưa đặt → mặc định "tháng 4".
        Assert.Equal("tháng 4", ReadOrderColumn(temp.Path, "SYNCED", "gsheet_tab"));
    }

    [Fact]
    public void KhoiTao_DbCu_Orders_BackfillGsheetTab_Idempotent_FirstWins()
    {
        using var temp = new TempDatabase();
        CreateOrdersSchemaBeforeGsheetTab(temp.Path, accountId: 33, tabSetting: "Tab Ban Dau");

        // Lần 1: backfill nhớ "Tab Ban Dau".
        var db = new Database(temp.Path);
        Assert.Equal("Tab Ban Dau", ReadOrderColumn(temp.Path, "SYNCED", "gsheet_tab"));

        // Đổi setting rồi khởi tạo lại → backfill KHÔNG chạm đơn đã có gsheet_tab (WHERE gsheet_tab IS NULL) →
        // giữ tab lần đầu, KHÔNG đổi theo setting mới (idempotent + first-write-wins).
        new SettingsRepository(db).SetGsheetTabName("Tab Moi");
        _ = new Database(temp.Path); // migration + backfill lần 2

        Assert.Equal("Tab Ban Dau", ReadOrderColumn(temp.Path, "SYNCED", "gsheet_tab"));
        Assert.Null(ReadOrderColumn(temp.Path, "UNSYNCED", "gsheet_tab"));
    }

    // ==== Migration cột shop_id cho bảng orders (mô hình 1 subaccount = nhiều shop) ====

    [Fact]
    public void KhoiTao_DbCu_Orders_ThieuShopId_DuocThemCot_KhongMatDuLieu_GetForGsheetPushKhongNem()
    {
        using var temp = new TempDatabase();
        CreateOldOrdersSchemaWithRow(temp.Path, accountId: 41, orderSn: "SNSHOP");

        // Trước migration: schema orders cũ chưa có cột shop_id.
        Assert.False(HasColumn(temp.Path, "orders", "shop_id"));

        // Khởi tạo Database mới trỏ cùng file → Initialize() chạy migration ALTER TABLE.
        _ = new Database(temp.Path);

        // Sau migration: đã có cột; đơn cũ CÒN NGUYÊN; shop_id mặc định NULL (không backfill).
        Assert.True(HasColumn(temp.Path, "orders", "shop_id"));
        Assert.Null(ReadOrderColumn(temp.Path, "SNSHOP", "shop_id"));

        var repo = new OrdersRepository(new Database(temp.Path));
        var row = Assert.Single(repo.Query(accountId: 41));
        Assert.Equal("SNSHOP", row.OrderSn);

        // Đơn cũ shop_id NULL: lọc theo shop → KHÔNG lọt; không lọc → vẫn trả (cả 2 nhánh KHÔNG ném).
        Assert.Empty(repo.GetForGsheetPush(41, "SHOP_A"));
        Assert.Single(repo.GetForGsheetPush(41));
    }

    // ===== Backfill "Số tiền cuối cùng" lên hub: đơn ĐÃ lên hub trước bản fix phải được đẩy LẠI một lần =====

    /// <summary>Xoá khoá đánh dấu backfill trong bảng <c>settings</c> → mô phỏng DB CŨ chưa từng chạy backfill.</summary>
    private static void XoaKhoaBackfill(string path)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM settings WHERE key = 'backfill_hub_final_amount_v1';";
        cmd.ExecuteNonQuery();
    }

    private static SyncedOrder DonCoTienCuoiCung(string sn) => new()
    {
        OrderSn = sn,
        ItemsJson = "[]",
        TotalPrice = 200000,
        FinalAmount = 180000,
        FinalAmountText = "₫180.000",
        TrackingNumber = "SPXVN1",
    };

    [Fact]
    public void Backfill_DonDaLenHubCoTienCuoiCung_DuocDayLai()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(7, new[] { DonCoTienCuoiCung("SNBF") }, DateTime.UtcNow);
        repo.MarkHubSynced(7, new[] { "SNBF" }, DateTime.UtcNow);
        Assert.Empty(repo.GetForHubPush(7));   // đã lên hub

        // DB CŨ: chưa từng chạy backfill → mở lại app phải reset cờ để đẩy lại kèm số tiền.
        XoaKhoaBackfill(temp.Path);
        var repoSauKhiMoLai = new OrdersRepository(temp.Open());

        var pending = repoSauKhiMoLai.GetForHubPush(7);
        Assert.Equal(new[] { "SNBF" }, pending.Select(o => o.OrderSn));
        Assert.Equal(180000, pending[0].FinalAmount);
    }

    [Fact]
    public void Backfill_ChayDungMotLan_KhongDayLaiVoHan()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(7, new[] { DonCoTienCuoiCung("SNBF") }, DateTime.UtcNow);

        // Lần mở lại ĐẦU: backfill chạy (khoá vừa bị xoá) → đẩy lại; đánh dấu đã đẩy.
        XoaKhoaBackfill(temp.Path);
        var repo2 = new OrdersRepository(temp.Open());
        Assert.Single(repo2.GetForHubPush(7));
        repo2.MarkHubSynced(7, new[] { "SNBF" }, DateTime.UtcNow);

        // Các lần mở lại SAU: khoá đã có → KHÔNG reset nữa (kẻo đẩy lại mỗi lần khởi động).
        var repo3 = new OrdersRepository(temp.Open());
        Assert.Empty(repo3.GetForHubPush(7));
    }
}
