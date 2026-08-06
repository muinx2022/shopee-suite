using Microsoft.Data.Sqlite;
using Shopee.Core.BigSeller;
using Shopee.Core.Coordination;
using Shopee.Core.Scrape;
using Shopee.Hub;
using Shopee.Hub.Web.Services;

namespace Shopee.Hub.Web.Tests;

/// <summary>
/// Hai hợp đồng của đợt H3 phía HUB:
/// <list type="bullet">
/// <item><b>H3.2</b> — tham số "khoảng nghỉ giữa 2 link" của lượt giao (rest_min_sec/rest_max_sec) đi trọn
/// vòng tạo → đọc → giao lại, và giữ ĐÚNG khuôn hub-run-params: <b>0 = dùng cấu hình client</b> (mặc định
/// 120–240s), op không phải scrape thì hub không ghi gì.</item>
/// <item><b>H3.3</b> — bộ 3 giá trị điền form Update là HUB-OWNED per-shop: hub lưu được, và bản upsert của
/// client (kể cả client CŨ không biết field) KHÔNG xoá trắng chúng.</item>
/// </list>
/// </summary>
public sealed class DispatchRunParamsTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "hub-h3-test-" + Guid.NewGuid().ToString("N"));

    // ===== 1. Khoảng nghỉ đi trọn vòng tạo → đọc lại =====
    [Fact]
    public void KhoangNghi_LuuVaDocLaiDung()
    {
        using var db = new HubDatabase(_dataDir);
        var a = db.CreateAssignment(new CreateAssignmentRequest(
            "acc1", "shop1", "SheetA", AssignmentOps.Scrape, "mach1", Pinned: true,
            RestMinSec: 45, RestMaxSec: 90));

        Assert.Equal(45, a.RestMinSec);
        Assert.Equal(90, a.RestMaxSec);

        var doc = Assert.Single(db.ListAssignments());
        Assert.Equal(45, doc.RestMinSec);
        Assert.Equal(90, doc.RestMaxSec);
    }

    // ===== 2. KHÔNG đặt gì → 0 = "dùng cấu hình client" → client resolve về mặc định 120–240s =====
    [Fact]
    public void KhongDatGi_VeKhong_ClientDungMacDinh()
    {
        using var db = new HubDatabase(_dataDir);
        var a = db.CreateAssignment(new CreateAssignmentRequest(
            "acc1", "shop1", "SheetA", AssignmentOps.Scrape, "mach1", Pinned: true));

        Assert.Equal(0, a.RestMinSec);
        Assert.Equal(0, a.RestMaxSec);

        // Đúng phép quy đổi mà AssignmentWorker + ScrapeViewModel dùng: >0 mới override, còn lại về cấu hình
        // client; client chưa đặt gì (0) thì Resolve trả mặc định 120–240s.
        var w = ScrapeRestWindow.Resolve(
            a.RestMinSec > 0 ? a.RestMinSec : 0,
            a.RestMaxSec > 0 ? a.RestMaxSec : 0);
        Assert.Equal(120_000, w.MinMs);
        Assert.Equal(240_000, w.MaxMs);
    }

    // ===== 3. Giao LẠI việc còn 'queued' cập nhật luôn khoảng nghỉ (khuôn của processes/frame/reload) =====
    [Fact]
    public void GiaoLaiViecConCho_CapNhatKhoangNghi()
    {
        using var db = new HubDatabase(_dataDir);
        db.CreateAssignment(new CreateAssignmentRequest(
            "acc1", "shop1", "SheetA", AssignmentOps.Scrape, "mach1", Pinned: true,
            RestMinSec: 10, RestMaxSec: 20));

        var lai = db.CreateAssignment(new CreateAssignmentRequest(
            "acc1", "shop1", "SheetA", AssignmentOps.Scrape, "mach1", Pinned: true,
            RestMinSec: 300, RestMaxSec: 600));

        Assert.Equal(300, lai.RestMinSec);
        Assert.Equal(600, lai.RestMaxSec);
        Assert.Equal(300, Assert.Single(db.ListAssignments()).RestMinSec);
    }

    // ===== 4. DB CŨ (chưa có 2 cột) → migration thêm cột, bản ghi cũ đọc ra 0 (= dùng cấu hình client) =====
    [Fact]
    public void DbCu_ThieuCot_MigrationThemVaDocRaKhong()
    {
        Directory.CreateDirectory(_dataDir);
        using (var conn = OpenHubDb())
        using (var c = conn.CreateCommand())
        {
            // Bảng assignments kiểu bản CŨ: có processes/frame_size/reload_seconds nhưng CHƯA có rest_*.
            c.CommandText = @"
CREATE TABLE assignments(
  id TEXT PRIMARY KEY, bigseller_id TEXT, shop_id TEXT, sheet TEXT, op TEXT,
  target_machine_id TEXT, pinned INTEGER, status TEXT,
  claimed_by TEXT, claimed_host TEXT, last_error TEXT, created_at TEXT, updated_at TEXT,
  start_row INTEGER DEFAULT 0, end_row INTEGER DEFAULT 0, payload TEXT DEFAULT '',
  processes INTEGER DEFAULT 0, frame_size INTEGER DEFAULT 0, reload_seconds INTEGER DEFAULT 0,
  dismissed INTEGER DEFAULT 0);
INSERT INTO assignments(id,bigseller_id,shop_id,sheet,op,pinned,status,claimed_by,claimed_host,last_error,created_at,updated_at)
VALUES('a1','acc1','shop1','SheetA','scrape',1,'queued','','','','2026-08-06T00:00:00.0000000+00:00','2026-08-06T00:00:00.0000000+00:00');";
            c.ExecuteNonQuery();
        }

        using var db = new HubDatabase(_dataDir);   // = hub khởi động lại trên DB cũ → MigrateSchema chạy
        var cu = Assert.Single(db.ListAssignments());
        Assert.Equal(0, cu.RestMinSec);
        Assert.Equal(0, cu.RestMaxSec);

        // Và vẫn tạo được việc MỚI có khoảng nghỉ trên chính DB vừa migrate.
        db.CreateAssignment(new CreateAssignmentRequest(
            "acc2", "shop2", "SheetB", AssignmentOps.Scrape, "mach1", Pinned: true, RestMinSec: 5, RestMaxSec: 6));
        Assert.Equal(5, db.ListAssignments().Single(x => x.BigsellerId == "acc2").RestMinSec);
    }

    // ===== 5. H3.3: hub LƯU được bộ 3 giá trị điền form Update của shop =====
    [Fact]
    public void HubLuuDuocBoBaGiaTriDienForm()
    {
        using var db = new HubDatabase(_dataDir);
        var cfg = new FileStoreConfigService(db);
        SeedShop(cfg, stock: "50000", weight: "1200", ship: "Tiết kiệm");

        var shop = cfg.BigSellerAccounts().Single().Shops.Single();
        Assert.Equal("50000", shop.UpdateStockValue);
        Assert.Equal("1200", shop.UpdateWeightValue);
        Assert.Equal("Tiết kiệm", shop.UpdateShippingChannel);
    }

    // ===== 6. H3.3 — BẪY CHÍNH: client đẩy shop lên KHÔNG được xoá trắng cấu hình hub vừa đặt =====
    /// <summary>Client (kể cả bản CŨ chưa biết 3 field, và bản mới đang cầm bản pull cũ) đẩy shop lên hub qua
    /// <c>/bigseller/upsert</c> — 3 field HUB-OWNED phải GIỮ NGUYÊN. Nếu hợp đồng này vỡ thì mỗi nhịp upsert
    /// sẽ đưa mọi shop về mặc định 30069/500/Nhanh mà không ai báo.</summary>
    [Fact]
    public void ClientDayShopLen_KhongXoaTrangCauHinhHub()
    {
        using var db = new HubDatabase(_dataDir);
        var cfg = new FileStoreConfigService(db);
        SeedShop(cfg, stock: "50000", weight: "1200", ship: "Tiết kiệm");

        // Bản CLIENT: cùng Id acc/shop, 3 field RỖNG (client không có UI cho chúng), có sửa 1 field chung khác.
        cfg.UpsertBigSellerAccounts(new List<BigSellerAccount>
        {
            new()
            {
                Id = "acc1", Label = "A", Email = "a@x.com",
                Shops = [new BigSellerShop { Id = "s1", Name = "Shop A đổi ở client", ShopeeDataSheet = "SheetA" }],
            },
        });

        var shop = cfg.BigSellerAccounts().Single().Shops.Single();
        Assert.Equal("50000", shop.UpdateStockValue);
        Assert.Equal("1200", shop.UpdateWeightValue);
        Assert.Equal("Tiết kiệm", shop.UpdateShippingChannel);
        Assert.Equal("Shop A đổi ở client", shop.Name);   // field chung THẬT vẫn nhận từ client như cũ
    }

    /// <summary>Shop MỚI do client tạo → hub nhận với 3 field để TRỐNG (= mặc định 30069/500/Nhanh), admin vào
    /// Fleet đặt sau. Không được nhận bừa giá trị lạ từ client.</summary>
    [Fact]
    public void ShopMoiTuClient_BaFieldDeTrong()
    {
        using var db = new HubDatabase(_dataDir);
        var cfg = new FileStoreConfigService(db);
        cfg.UpsertBigSellerAccounts(new List<BigSellerAccount>
        {
            new()
            {
                Id = "accX", Label = "X", Email = "x@x.com",
                Shops = [new BigSellerShop { Id = "sX", Name = "Shop X", UpdateStockValue = "999" }],
            },
        });

        var shop = cfg.BigSellerAccounts().Single().Shops.Single();
        Assert.Equal("", shop.UpdateStockValue);
        Assert.Equal("30069", BigSellerShop.OrDefault(shop.UpdateStockValue, BigSellerShop.DefaultUpdateStock));
    }

    /// <summary>Ghi 1 acc + 1 shop lên kho file của hub với bộ 3 giá trị đã đặt (mô phỏng admin bấm Lưu ở
    /// ShopConfigPanel).</summary>
    private static void SeedShop(FileStoreConfigService cfg, string stock, string weight, string ship)
    {
        var list = new List<BigSellerAccount>
        {
            new()
            {
                Id = "acc1", Label = "A", Email = "a@x.com",
                Shops =
                [
                    new BigSellerShop
                    {
                        Id = "s1", Name = "Shop A", ShopeeDataSheet = "SheetA",
                        UpdateStockValue = stock, UpdateWeightValue = weight, UpdateShippingChannel = ship,
                    },
                ],
            },
        };
        Assert.True(cfg.Save(FileStoreConfigService.BigSellerFile, list, cfg.VersionOf(FileStoreConfigService.BigSellerFile)).Ok);
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
