using Microsoft.Data.Sqlite;
using Shopee.Core.Coordination;
using Shopee.Core.Scrape;
using Shopee.Hub;

namespace Shopee.Hub.Web.Tests;

/// <summary>
/// SỔ DÒNG BỎ QUA trên HUB (đợt A). Trước đây dòng bỏ qua chỉ sống trong tiến độ LOCAL của máy cào, còn lên
/// ledger thì đi lẫn vào <c>completed</c> — máy KHÁC fold về chỉ thấy "✔ Hoàn thành toàn bộ", không biết đang
/// thiếu SP nào ("báo thành công khi thiếu" xuyên máy).
/// <para>Bốn hợp đồng ở đây: (1) hub UNION sổ qua nhiều lượt publish; (2) client CŨ (không gửi
/// <c>Skipped</c>) KHÔNG xoá trắng sổ của máy mới — điều kiện để deploy hub trước khi phát hành client;
/// (3) bản ghi có TRƯỚC migration (chưa có cột <c>skipped_json</c>) vẫn đọc được; (4) mở lại dòng đã bỏ =
/// khoét vùng phủ + xoá sổ + hạ trạng thái, và <c>/ledger/set idle</c> vẫn xoá sạch bản ghi.</para>
/// </summary>
public sealed class LedgerSkippedTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "hub-skipped-test-" + Guid.NewGuid().ToString("N"));

    private const string Key = "acc1__shop1__scrape";

    private static WorkLedgerRecord Rec(List<RowRange> completed, List<int> skipped, string machine = "mach1") => new()
    {
        Key = Key, BigsellerId = "acc1", ShopId = "shop1", Sheet = "SheetA", Op = AssignmentOps.Scrape,
        Completed = completed, Skipped = skipped, LastRowReached = 0,
        Status = LedgerStatus.Running, LastMachineId = machine, LastHostname = "PC-" + machine,
    };

    private static List<RowRange> Rng(params (int from, int to)[] rs) =>
        [.. rs.Select(r => new RowRange { From = r.from, To = r.to })];

    private static IEnumerable<(int from, int to)> Pairs(IEnumerable<RowRange> rs) => rs.Select(r => (r.From, r.To));

    // ===== 1. Union qua nhiều lượt publish =====
    [Fact]
    public void PublishLedger_GopSoBoQua_KhongThayThe()
    {
        using var db = new HubDatabase(_dataDir);
        // Đúng hình dạng client đẩy lên: PublishSkipped gửi MỘT dòng mỗi lượt → sổ đầy đủ chỉ có nếu hub GỘP.
        db.PublishLedger(Rec(Rng((2, 6)), [4]));
        db.PublishLedger(Rec(Rng((7, 9)), [8], machine: "mach2"));
        db.PublishLedger(Rec(Rng((7, 9)), [8], machine: "mach2"));   // publish trùng (retry chunk) → không nhân đôi

        var led = Assert.Single(db.AllLedger());
        Assert.Equal([4, 8], led.Skipped);           // dedup + sắp xếp
        Assert.Equal([(2, 9)], Pairs(led.Completed));   // completed vẫn gộp như cũ
    }

    // ===== 2. Tương thích ngược: client CŨ không gửi Skipped ⇒ union rỗng, KHÔNG xoá sổ đang có =====
    [Fact]
    public void ClientCu_KhongGuiSkipped_GiuNguyenSoDaCo()
    {
        using var db = new HubDatabase(_dataDir);
        db.PublishLedger(Rec(Rng((2, 6)), [4]));

        // Đúng hình dạng bản ghi client v1.9.2 đẩy lên: JSON không có field Skipped → deserialize ra list rỗng.
        var cu = Rec(Rng((7, 9)), [], machine: "mach-cu");
        db.PublishLedger(cu);

        // Nếu ghi ĐÈ thay vì union thì mỗi lượt publish của một máy client cũ là xoá trắng sổ của máy mới.
        Assert.Equal([4], Assert.Single(db.AllLedger()).Skipped);
    }

    // ===== 3. Migration: bản ghi ghi TRƯỚC khi có cột skipped_json vẫn đọc được =====
    [Fact]
    public void BanGhiCu_ChuaCoCotSkipped_VanDocDuoc_VaPublishSauVanGop()
    {
        // Dựng DB "đời cũ": tạo bảng ledger THIẾU cột skipped_json rồi mới mở HubDatabase (migration chạy).
        Directory.CreateDirectory(_dataDir);
        using (var conn = OpenHubDb())
        {
            using var c = conn.CreateCommand();
            c.CommandText = @"
CREATE TABLE ledger(
  key TEXT PRIMARY KEY, bigseller_id TEXT, shop_id TEXT, sheet TEXT, op TEXT,
  completed_json TEXT, last_row INTEGER, status TEXT,
  last_machine_id TEXT, last_hostname TEXT, last_run_at TEXT, updated_at TEXT);
INSERT INTO ledger(key,bigseller_id,shop_id,sheet,op,completed_json,last_row,status,last_machine_id,last_hostname,updated_at)
VALUES($k,'acc1','shop1','SheetA','scrape','[{""From"":2,""To"":6}]',6,'stopped','mach-cu','PC-cu','2026-08-01T00:00:00+00:00');";
            c.Parameters.AddWithValue("$k", Key);
            c.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var db = new HubDatabase(_dataDir);
        var doc = Assert.Single(db.AllLedger());
        Assert.Empty(doc.Skipped);                      // KHÔNG backfill: dòng cũ về sổ rỗng, không ném
        Assert.Equal([(2, 6)], Pairs(doc.Completed));

        db.PublishLedger(Rec(Rng((7, 7)), [5]));
        Assert.Equal([5], Assert.Single(db.AllLedger()).Skipped);
    }

    // ===== 4a. Mở lại dòng đã bỏ: khoét vùng phủ + xoá sổ + hạ status =====
    [Fact]
    public void ReopenSkipped_KhoetVungPhu_XoaSo_VeStopped()
    {
        using var db = new HubDatabase(_dataDir);
        db.PublishLedger(Rec(Rng((2, 8)), [4, 7]));
        db.SetLedgerStatus(Key, "acc1", "shop1", "SheetA", AssignmentOps.Scrape, LedgerStatus.Completed);

        Assert.Equal(2, db.ReopenSkippedLedger(Key));

        var led = Assert.Single(db.AllLedger());
        Assert.Equal([(2, 3), (5, 6), (8, 8)], Pairs(led.Completed));
        Assert.Empty(led.Skipped);
        // Còn dòng chưa cào → không được để "✔ xong", kẻo máy khác fold về lại tưởng shop này xong hẳn.
        Assert.Equal(LedgerStatus.Stopped, led.Status);
    }

    // ===== 4b. Không có bản ghi / sổ đã sạch → 0, KHÔNG ném (client vẫn được sửa tiếp phần local) =====
    [Fact]
    public void ReopenSkipped_KhongCoBanGhi_HoacSoSach_TraKhong()
    {
        using var db = new HubDatabase(_dataDir);
        Assert.Equal(0, db.ReopenSkippedLedger("khong-ton-tai"));

        db.PublishLedger(Rec(Rng((2, 8)), []));
        db.SetLedgerStatus(Key, "acc1", "shop1", "SheetA", AssignmentOps.Scrape, LedgerStatus.Completed);
        Assert.Equal(0, db.ReopenSkippedLedger(Key));

        // Sổ sạch thì nút này phải là no-op HOÀN TOÀN: không đụng vùng phủ, và KHÔNG được hạ "✔ xong" xuống
        // stopped (client sẽ đọc lại status này ở lượt fold — hạ oan là báo "chưa xong" cho shop đã xong).
        var led = Assert.Single(db.AllLedger());
        Assert.Equal([(2, 8)], Pairs(led.Completed));
        Assert.Equal(LedgerStatus.Completed, led.Status);
    }

    // ===== 4e. Dòng bỏ TRƯỚC khi hub có cột skipped (sổ server RỖNG, không backfill): client gửi kèm
    // danh sách dòng của mình → hub union rồi vẫn khoét được. Thiếu đường này thì nút "Cào lại dòng đã bỏ"
    // là no-op im lặng cho mọi dòng đời cũ: hub trả OK không khoét gì, client xoá sổ local, lượt fold sau
    // phủ lại → mất dấu vĩnh viễn. =====
    [Fact]
    public void ReopenSkipped_SoServerRong_ClientGuiKemDong_VanKhoet()
    {
        using var db = new HubDatabase(_dataDir);
        // Hình dạng dữ liệu ĐỜI CŨ sau deploy: vùng phủ trùm dòng 50, sổ skipped rỗng.
        db.PublishLedger(Rec(Rng((2, 60)), []));
        db.SetLedgerStatus(Key, "acc1", "shop1", "SheetA", AssignmentOps.Scrape, LedgerStatus.Completed);

        Assert.Equal(0, db.ReopenSkippedLedger(Key));            // không gửi rows → no-op như 4b
        Assert.Equal(1, db.ReopenSkippedLedger(Key, [50]));      // gửi rows → khoét được dù sổ server rỗng

        var led = Assert.Single(db.AllLedger());
        Assert.Equal([(2, 49), (51, 60)], Pairs(led.Completed));
        Assert.Equal(LedgerStatus.Stopped, led.Status);
    }

    // ===== 4c. /ledger/set idle vẫn XOÁ sạch bản ghi (sổ bỏ qua chết theo — đúng ý reset) =====
    [Fact]
    public void SetLedgerStatus_Idle_XoaCaSoBoQua()
    {
        using var db = new HubDatabase(_dataDir);
        db.PublishLedger(Rec(Rng((2, 8)), [4]));
        db.SetLedgerStatus(Key, "acc1", "shop1", "SheetA", AssignmentOps.Scrape, LedgerStatus.Idle);

        Assert.Empty(db.AllLedger());
    }

    // ===== 4d. Đặt tay completed/stopped KHÔNG được làm mất sổ bỏ qua =====
    [Fact]
    public void SetLedgerStatus_Completed_GiuNguyenSoBoQua()
    {
        using var db = new HubDatabase(_dataDir);
        db.PublishLedger(Rec(Rng((2, 8)), [4]));
        db.SetLedgerStatus(Key, "acc1", "shop1", "SheetA", AssignmentOps.Scrape, LedgerStatus.Completed);

        var led = Assert.Single(db.AllLedger());
        Assert.Equal([4], led.Skipped);
        Assert.Equal(LedgerStatus.Completed, led.Status);
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
