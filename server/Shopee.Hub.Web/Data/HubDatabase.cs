using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Shopee.Core.Coordination;
using Shopee.Core.Scrape;

namespace Shopee.Hub;

/// <summary>
/// Tầng dữ liệu Hub trên SQLite: khoá việc (leases), khoá tài khoản Shopee (account_leases),
/// sổ hoàn thành (ledger), nhịp máy (machines), manifest file dùng chung (files + blob trên đĩa).
/// Một process duy nhất xử lý ghi → mọi thao tác chạy DƯỚI một lock → nguyên tử, không đua.
///
/// COPY từ suite\Shopee.Hub\HubDatabase.cs (bản nhúng WPF vẫn dùng file gốc tới lúc cutover). Bản web-hub
/// BỔ SUNG: bảng key/value <c>settings</c> (thay hub-server.json: admin PBKDF2, api token, cờ điều phối),
/// <see cref="DeleteFile"/>, guard tên file trùng khác hoa-thường (Linux case-sensitive), và khoá theo
/// TÀI KHOẢN BigSeller trong <see cref="AcquireLease"/> (chống 2 máy cùng dùng 1 acc BigSeller — cookie xoay
/// theo IP, 2 máy cùng phiên là bay cookie).
/// </summary>
public sealed partial class HubDatabase : IDisposable
{
    private readonly object _gate = new();
    private readonly SqliteConnection _conn;

    public string FilesDir { get; }
    public TimeSpan StaleLease { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan StaleAccount { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Bật khoá xuyên-máy theo TÀI KHOẢN BigSeller ở <see cref="AcquireLease"/>: máy KHÁC đang giữ
    /// lease tươi của cùng bigseller_id (op bất kỳ) → từ chối cấp (Blocked). Chống 2 máy cùng dùng 1 acc
    /// BigSeller (cookie xoay theo IP — 2 máy cùng phiên là bay cookie). Client cũ xử lý Blocked(...) sẵn nên bật an toàn.</summary>
    public bool AccountScopedLease { get; init; } = true;

    public HubDatabase(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        FilesDir = Path.Combine(dataDir, "files");
        Directory.CreateDirectory(FilesDir);

        _conn = new SqliteConnection($"Data Source={Path.Combine(dataDir, "hub.db")}");
        _conn.Open();
        ExecRaw("PRAGMA journal_mode=WAL;");
        ExecRaw("PRAGMA busy_timeout=5000;");
        EnsureSchema();
        MigrateSchema();
    }

    /// <summary>Thêm cột mới vào DB ĐÃ TỒN TẠI (CREATE TABLE IF NOT EXISTS không thêm cột cho bảng cũ).</summary>
    private void MigrateSchema()
    {
        AddColumnIfMissing("assignments", "start_row", "INTEGER DEFAULT 0");
        AddColumnIfMissing("assignments", "end_row", "INTEGER DEFAULT 0");
        AddColumnIfMissing("assignments", "payload", "TEXT DEFAULT ''");
        // Tham số chạy Hub đặt cho lượt việc (ghi đè cấu hình client; 0 = dùng cấu hình client).
        AddColumnIfMissing("assignments", "processes", "INTEGER DEFAULT 0");
        AddColumnIfMissing("assignments", "frame_size", "INTEGER DEFAULT 0");
        AddColumnIfMissing("assignments", "reload_seconds", "INTEGER DEFAULT 0");
        // Cờ "đã bỏ khỏi danh sách gián đoạn" do operator Reset máy / bỏ hẳn — status GIỮ NGUYÊN để sticky-cancel còn hiệu lực.
        AddColumnIfMissing("assignments", "dismissed", "INTEGER DEFAULT 0");
        // Trần cửa sổ Brave máy client tự báo lên (0 = chưa báo).
        AddColumnIfMissing("machines", "max_brave", "INTEGER DEFAULT 0");
        // Lệnh update app cho từng máy: ISO lúc ra lệnh + app_version LÚC ra lệnh (để biết "đã lên bản khác chưa")
        // + dòng trạng thái hiển thị trên /machines. '' = không có lệnh.
        AddColumnIfMissing("machines", "update_requested_at", "TEXT DEFAULT ''");
        AddColumnIfMissing("machines", "update_requested_from", "TEXT DEFAULT ''");
        AddColumnIfMissing("machines", "update_status", "TEXT DEFAULT ''");
        // Tập máy đã tham gia mỗi việc (Thống kê). Backfill: khởi tạo = [last_machine_id] cho bản ghi cũ
        // (machine_id là hex GUID, an toàn để nối chuỗi JSON). Publish sau sẽ union thêm máy mới.
        if (AddColumnIfMissing("ledger", "machines_json", "TEXT DEFAULT ''"))
            ExecRaw("UPDATE ledger SET machines_json = '[\"' || last_machine_id || '\"]' " +
                    "WHERE (machines_json IS NULL OR machines_json = '') AND last_machine_id IS NOT NULL AND last_machine_id <> '';");
    }

    /// <summary>Thêm cột nếu thiếu; trả true nếu VỪA thêm (để chạy backfill 1 lần).</summary>
    private bool AddColumnIfMissing(string table, string column, string decl)
    {
        var exists = false;
        using (var c = _conn.CreateCommand())
        {
            c.CommandText = $"PRAGMA table_info({table})";
            using var rd = c.ExecuteReader();
            while (rd.Read())
                if (string.Equals(rd.GetString(1), column, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
        }
        if (!exists) ExecRaw($"ALTER TABLE {table} ADD COLUMN {column} {decl};");
        return !exists;
    }

    public void Dispose() { lock (_gate) _conn.Dispose(); }

    /// <summary>Snapshot nhất quán của DB ra <paramref name="destPath"/> (VACUUM INTO) + checkpoint WAL để
    /// hub.db-wal khỏi phình. Dùng cho backup đêm. destPath phải CHƯA tồn tại (SQLite tự tạo).</summary>
    public void VacuumInto(string destPath)
    {
        lock (_gate)
        {
            try { if (File.Exists(destPath)) File.Delete(destPath); } catch { }
            using (var c = _conn.CreateCommand())
            {
                c.CommandText = "VACUUM INTO $p";
                c.Parameters.AddWithValue("$p", destPath);
                c.ExecuteNonQuery();
            }
            ExecRaw("PRAGMA wal_checkpoint(TRUNCATE);");
        }
    }

    // ── Schema ────────────────────────────────────────────────────────────────
    private void EnsureSchema()
    {
        ExecRaw(@"
CREATE TABLE IF NOT EXISTS leases(
  key TEXT PRIMARY KEY, bigseller_id TEXT, shop_id TEXT, sheet TEXT, op TEXT,
  machine_id TEXT, hostname TEXT, acquired_at TEXT, heartbeat_at TEXT, status TEXT);
CREATE TABLE IF NOT EXISTS account_leases(
  account_id TEXT PRIMARY KEY, machine_id TEXT, hostname TEXT, heartbeat_at TEXT);
CREATE TABLE IF NOT EXISTS ledger(
  key TEXT PRIMARY KEY, bigseller_id TEXT, shop_id TEXT, sheet TEXT, op TEXT,
  completed_json TEXT, last_row INTEGER, status TEXT,
  last_machine_id TEXT, last_hostname TEXT, last_run_at TEXT, updated_at TEXT,
  machines_json TEXT DEFAULT '');
CREATE TABLE IF NOT EXISTS machines(
  machine_id TEXT PRIMARY KEY, hostname TEXT, last_seen TEXT, app_version TEXT, max_brave INTEGER DEFAULT 0,
  update_requested_at TEXT DEFAULT '', update_requested_from TEXT DEFAULT '', update_status TEXT DEFAULT '');
CREATE TABLE IF NOT EXISTS files(
  name TEXT PRIMARY KEY, version INTEGER, hash TEXT, size INTEGER, mtime TEXT,
  updated_by TEXT, updated_at TEXT);
CREATE TABLE IF NOT EXISTS machine_roles(
  machine_id TEXT PRIMARY KEY, role TEXT);
CREATE TABLE IF NOT EXISTS assignments(
  id TEXT PRIMARY KEY, bigseller_id TEXT, shop_id TEXT, sheet TEXT, op TEXT,
  target_machine_id TEXT, pinned INTEGER, status TEXT,
  claimed_by TEXT, claimed_host TEXT, last_error TEXT, created_at TEXT, updated_at TEXT,
  start_row INTEGER DEFAULT 0, end_row INTEGER DEFAULT 0, payload TEXT DEFAULT '',
  processes INTEGER DEFAULT 0, frame_size INTEGER DEFAULT 0, reload_seconds INTEGER DEFAULT 0,
  dismissed INTEGER DEFAULT 0);
CREATE TABLE IF NOT EXISTS search_products(
  item_id INTEGER PRIMARY KEY, json TEXT, machine_id TEXT, source_file TEXT, updated_at TEXT);
CREATE TABLE IF NOT EXISTS account_errors(
  account_id TEXT PRIMARY KEY, machine_id TEXT, hostname TEXT, reason TEXT, captcha_url TEXT, status TEXT, reported_at TEXT);
CREATE TABLE IF NOT EXISTS logs(
  id INTEGER PRIMARY KEY AUTOINCREMENT, machine_id TEXT, hostname TEXT, ts TEXT, level TEXT, text TEXT);
CREATE TABLE IF NOT EXISTS settings(
  key TEXT PRIMARY KEY, value TEXT);
CREATE TABLE IF NOT EXISTS revoked_machines(
  machine_id TEXT PRIMARY KEY, revoked_at TEXT);");

        // Bảng nghiệp vụ đơn hàng (partial ở HubDatabase.Shops.cs / HubDatabase.Orders.cs).
        EnsureShopsSchema();
        EnsureOrdersSchema();
    }

    // ── Settings (key/value; thay hub-server.json: admin PBKDF2, api token, cờ điều phối…) ──
    public string? GetSetting(string key)
    {
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT value FROM settings WHERE key=$k";
            c.Parameters.AddWithValue("$k", key);
            return c.ExecuteScalar() as string;
        }
    }

    public void SetSetting(string key, string value)
    {
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "INSERT INTO settings(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v;";
            c.Parameters.AddWithValue("$k", key);
            c.Parameters.AddWithValue("$v", value ?? "");
            c.ExecuteNonQuery();
        }
    }

    public FleetSnapshot Fleet() => new()
    {
        Leases = ActiveLeases(),
        AccountLeases = ActiveAccountLeases(),
        Ledger = AllLedger(),
        Machines = AllMachines(),
        Roles = AllRoles(),
        Assignments = ListAssignments(),
        Interrupted = ListInterrupted(),
    };

    // ── Tiện ích ────────────────────────────────────────────────────────────────
    private void ExecRaw(string sql)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    private static string Iso(DateTimeOffset d) => d.ToString("o");
    private static string? Iso(DateTimeOffset? d) => d?.ToString("o");
    private static string S(SqliteDataReader rd, int i) => rd.IsDBNull(i) ? "" : rd.GetString(i);
    private static DateTimeOffset D(SqliteDataReader rd, int i)
        => rd.IsDBNull(i) ? DateTimeOffset.MinValue
           : DateTimeOffset.TryParse(rd.GetString(i), out var d) ? d : DateTimeOffset.MinValue;

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    /// <summary>Chuẩn hoá tên file thành đường dẫn TUYỆT ĐỐI an toàn nằm TRONG <paramref name="baseDir"/>.
    /// Chặn traversal ("..", "."), đường dẫn có ổ đĩa ("C:\..."), UNC, và alternate-data-stream ("tên:stream"),
    /// rồi chốt chặn cuối bằng kiểm tra prefix sau khi GetFullPath. null nếu tên xấu / thoát ra ngoài baseDir.</summary>
    private static string? SafeFullPath(string baseDir, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var rel = name.Replace('\\', '/').Trim('/');
        if (rel.Length == 0) return null;
        // Mỗi đoạn phải "lành": không rỗng/./.. và KHÔNG chứa ':' ("C:" rooted hoặc ADS "tên:stream").
        if (rel.Split('/').Any(seg => seg is "" or "." or ".." || seg.Contains(':'))) return null;

        var baseFull = Path.GetFullPath(baseDir);
        var full = Path.GetFullPath(Path.Combine(baseFull, rel.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = baseFull.EndsWith(Path.DirectorySeparatorChar) ? baseFull : baseFull + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
}
