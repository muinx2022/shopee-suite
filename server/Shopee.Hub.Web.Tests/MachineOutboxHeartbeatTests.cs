using Microsoft.Data.Sqlite;
using Shopee.Core.Coordination;
using Shopee.Hub;

namespace Shopee.Hub.Web.Tests;

/// <summary>
/// Backlog "chờ đẩy" mang trên NHỊP SỐNG (H1.4) — hợp đồng client↔hub phải chịu được CẢ HAI chiều lệch bản:
/// client CŨ (không gửi field) → hub mới đọc ra <c>null</c> = "máy không báo" (bảng hiện "—"), khác hẳn 0;
/// client MỚI gửi field → hub lưu đúng số. Kèm migration trên DB CŨ (bảng <c>machines</c> chưa có cột).
/// </summary>
public sealed class MachineOutboxHeartbeatTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "hub-outbox-test-" + Guid.NewGuid().ToString("N"));

    /// <summary>Client MỚI (suất đơn hàng) gửi số tồn → hub lưu và trả lại đúng số đó.</summary>
    [Fact]
    public void ClientMoi_GuiSoTon_HubLuuDungSo()
    {
        using var db = new HubDatabase(_dataDir);
        db.MachineHeartbeat(new MachineHeartbeatRequest("m1:orders", "PC-01", "1.7.19", 0,
            "Full", MachineSlots.Orders, "m1", OutboxPending: 7));

        Assert.Equal(7, MayCuaTest(db, "m1:orders").OutboxPending);
    }

    /// <summary>0 KHÁC null: "đã đẩy hết" phải hiện ra số 0, không được rơi về "—".</summary>
    [Fact]
    public void GuiKhong_LuuKhong_KhongPhaiNull()
    {
        using var db = new HubDatabase(_dataDir);
        db.MachineHeartbeat(new MachineHeartbeatRequest("m1:orders", "PC-01", "1.7.19", 0,
            "Full", MachineSlots.Orders, "m1", OutboxPending: 0));

        Assert.Equal(0, MayCuaTest(db, "m1:orders").OutboxPending);
    }

    /// <summary>Client CŨ (dựng request KHÔNG có tham số mới — đúng cách bản cũ gọi) → null, và KHÔNG ném.
    /// Suất workspace của bản mới cũng đi đúng đường này (không bao giờ gửi field).</summary>
    [Fact]
    public void ClientCu_KhongGuiField_HubDocRaNull()
    {
        using var db = new HubDatabase(_dataDir);
        db.MachineHeartbeat(new MachineHeartbeatRequest("m1", "PC-01", "1.6.0", 4));

        Assert.Null(MayCuaTest(db, "m1").OutboxPending);
    }

    /// <summary>Máy từng báo rồi thôi báo (hạ bản / đổi chế độ) → về "—", KHÔNG giữ lại con số chết. Đây là lý do
    /// cột này ghi thẳng chứ không COALESCE giá trị cũ.</summary>
    [Fact]
    public void DangBaoRoiThoiBao_VeNull_KhongGiuSoChet()
    {
        using var db = new HubDatabase(_dataDir);
        db.MachineHeartbeat(new MachineHeartbeatRequest("m1:orders", "PC-01", "1.7.19", 0,
            "Full", MachineSlots.Orders, "m1", OutboxPending: 12));
        Assert.Equal(12, MayCuaTest(db, "m1:orders").OutboxPending);

        db.MachineHeartbeat(new MachineHeartbeatRequest("m1:orders", "PC-01", "1.6.0", 0,
            "Full", MachineSlots.Orders, "m1"));

        Assert.Null(MayCuaTest(db, "m1:orders").OutboxPending);
    }

    /// <summary>Số ÂM (client lỗi / gọi API tay) bị hub KẸP về 0 — hub không tin client đã kẹp. Nếu lọt, bảng
    /// /machines tô số âm bằng màu "đã đẩy hết" (pill xanh "-9"), tức báo NGƯỢC tình hình.</summary>
    [Fact]
    public void GuiSoAm_HubKepVeKhong()
    {
        using var db = new HubDatabase(_dataDir);
        db.MachineHeartbeat(new MachineHeartbeatRequest("m1:orders", "PC-01", "1.7.19", 0,
            "Full", MachineSlots.Orders, "m1", OutboxPending: -9));

        Assert.Equal(0, MayCuaTest(db, "m1:orders").OutboxPending);   // 0, KHÔNG phải -9 và KHÔNG phải null
    }

    /// <summary>Số tồn của máy này KHÔNG lây sang máy khác (mỗi suất một dòng).</summary>
    [Fact]
    public void MoiSuatMotSo_KhongLayNhau()
    {
        using var db = new HubDatabase(_dataDir);
        db.MachineHeartbeat(new MachineHeartbeatRequest("m1", "PC-01", "1.7.19", 4, "Full", MachineSlots.Workspace, "m1"));
        db.MachineHeartbeat(new MachineHeartbeatRequest("m1:orders", "PC-01", "1.7.19", 0,
            "Full", MachineSlots.Orders, "m1", OutboxPending: 5));

        Assert.Null(MayCuaTest(db, "m1").OutboxPending);          // suất workspace: không báo
        Assert.Equal(5, MayCuaTest(db, "m1:orders").OutboxPending);
    }

    /// <summary>DB CŨ (bảng machines chưa có cột) → mở hub: thêm cột, dòng máy cũ ra null, nhịp mới ghi được.</summary>
    [Fact]
    public void MoDbCu_ThemCot_DongCuNull_VaVanGhiDuoc()
    {
        Directory.CreateDirectory(_dataDir);
        using (var conn = MoDb())
        using (var c = conn.CreateCommand())
        {
            // Bảng machines ĐÚNG như bản trước H1.4 (chưa có outbox_pending), đã có sẵn một máy.
            c.CommandText = @"
CREATE TABLE machines(
  machine_id TEXT PRIMARY KEY, hostname TEXT, last_seen TEXT, app_version TEXT, max_brave INTEGER DEFAULT 0,
  update_requested_at TEXT DEFAULT '', update_requested_from TEXT DEFAULT '', update_status TEXT DEFAULT '',
  mode TEXT DEFAULT '', kind TEXT DEFAULT '', host_id TEXT DEFAULT '');
INSERT INTO machines(machine_id, hostname, last_seen, app_version)
VALUES('m-cu', 'PC-CU', '2026-08-01T10:00:00.0000000+00:00', '1.6.0');";
            c.ExecuteNonQuery();
        }

        using var db = new HubDatabase(_dataDir);   // = hub khởi động lại trên DB cũ
        Assert.Null(MayCuaTest(db, "m-cu").OutboxPending);
        Assert.Equal("PC-CU", MayCuaTest(db, "m-cu").Hostname);   // dòng cũ KHÔNG mất

        db.MachineHeartbeat(new MachineHeartbeatRequest("m-cu", "PC-CU", "1.7.19", 0,
            "Shopee", MachineSlots.Orders, "m-cu", OutboxPending: 3));
        Assert.Equal(3, MayCuaTest(db, "m-cu").OutboxPending);
    }

    private static MachinePresence MayCuaTest(HubDatabase db, string machineId) =>
        Assert.Single(db.AllMachines().Where(m => m.MachineId == machineId));

    private SqliteConnection MoDb()
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
