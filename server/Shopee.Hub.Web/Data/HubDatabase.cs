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
/// TÀI KHOẢN BigSeller trong <see cref="AcquireLease"/> (chống 2 máy cùng ghi 1 workbook / 1 cookie nhiều IP).
/// </summary>
public sealed class HubDatabase : IDisposable
{
    private readonly object _gate = new();
    private readonly SqliteConnection _conn;

    public string FilesDir { get; }
    public TimeSpan StaleLease { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan StaleAccount { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Bật khoá xuyên-máy theo TÀI KHOẢN BigSeller ở <see cref="AcquireLease"/>: máy KHÁC đang giữ
    /// lease tươi của cùng bigseller_id (op bất kỳ) → từ chối cấp (Blocked). Chống 2 máy cùng ghi 1 workbook
    /// (bản chính giờ ở Hub) và 1 cookie dùng từ nhiều IP. Client cũ xử lý Blocked(...) sẵn nên bật an toàn.</summary>
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
    }

    private void AddColumnIfMissing(string table, string column, string decl)
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
    private void EnsureSchema() => ExecRaw(@"
CREATE TABLE IF NOT EXISTS leases(
  key TEXT PRIMARY KEY, bigseller_id TEXT, shop_id TEXT, sheet TEXT, op TEXT,
  machine_id TEXT, hostname TEXT, acquired_at TEXT, heartbeat_at TEXT, status TEXT);
CREATE TABLE IF NOT EXISTS account_leases(
  account_id TEXT PRIMARY KEY, machine_id TEXT, hostname TEXT, heartbeat_at TEXT);
CREATE TABLE IF NOT EXISTS ledger(
  key TEXT PRIMARY KEY, bigseller_id TEXT, shop_id TEXT, sheet TEXT, op TEXT,
  completed_json TEXT, last_row INTEGER, status TEXT,
  last_machine_id TEXT, last_hostname TEXT, last_run_at TEXT, updated_at TEXT);
CREATE TABLE IF NOT EXISTS machines(
  machine_id TEXT PRIMARY KEY, hostname TEXT, last_seen TEXT, app_version TEXT);
CREATE TABLE IF NOT EXISTS files(
  name TEXT PRIMARY KEY, version INTEGER, hash TEXT, size INTEGER, mtime TEXT,
  updated_by TEXT, updated_at TEXT);
CREATE TABLE IF NOT EXISTS machine_roles(
  machine_id TEXT PRIMARY KEY, role TEXT);
CREATE TABLE IF NOT EXISTS assignments(
  id TEXT PRIMARY KEY, bigseller_id TEXT, shop_id TEXT, sheet TEXT, op TEXT,
  target_machine_id TEXT, pinned INTEGER, status TEXT,
  claimed_by TEXT, claimed_host TEXT, last_error TEXT, created_at TEXT, updated_at TEXT,
  start_row INTEGER DEFAULT 0, end_row INTEGER DEFAULT 0, payload TEXT DEFAULT '');
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

    // ── Quản lý máy client: chặn (revoke) + reset việc/khoá của 1 máy ──
    /// <summary>Chặn 1 máy: mọi request kèm X-Machine-Id này bị 401 (mất kết nối dù còn token). Kèm xoá máy +
    /// reset việc để "xoá client" dứt điểm. Máy tạo machine.json mới (id khác) vẫn vào lại được (chặn theo id).</summary>
    public void RevokeMachine(string machineId)
    {
        if (string.IsNullOrWhiteSpace(machineId)) return;
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "INSERT INTO revoked_machines(machine_id,revoked_at) VALUES($m,$t) ON CONFLICT(machine_id) DO UPDATE SET revoked_at=$t;";
            c.Parameters.AddWithValue("$m", machineId);
            c.Parameters.AddWithValue("$t", Iso(DateTimeOffset.UtcNow));
            c.ExecuteNonQuery();
        }
    }

    public void UnrevokeMachine(string machineId)
    {
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "DELETE FROM revoked_machines WHERE machine_id=$m";
            c.Parameters.AddWithValue("$m", machineId);
            c.ExecuteNonQuery();
        }
    }

    public bool IsMachineRevoked(string machineId)
    {
        if (string.IsNullOrWhiteSpace(machineId)) return false;
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT 1 FROM revoked_machines WHERE machine_id=$m";
            c.Parameters.AddWithValue("$m", machineId);
            return c.ExecuteScalar() is not null;
        }
    }

    public List<string> RevokedMachines()
    {
        lock (_gate)
        {
            var list = new List<string>();
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT machine_id FROM revoked_machines";
            using var rd = c.ExecuteReader();
            while (rd.Read()) list.Add(S(rd, 0));
            return list;
        }
    }

    /// <summary>Reset 1 máy NGAY (không chờ 5' stale): huỷ mọi assignment máy này đang giữ/được ghim (queued/
    /// running → canceled) + nhả mọi lease việc + account-lease của máy → acc được nhả tức thì, ghim lại được.
    /// Trả số assignment đã huỷ.</summary>
    public int ResetMachineWork(string machineId)
    {
        if (string.IsNullOrWhiteSpace(machineId)) return 0;
        lock (_gate)
        {
            var now = Iso(DateTimeOffset.UtcNow);
            int n;
            using (var c = _conn.CreateCommand())
            {
                c.CommandText = "UPDATE assignments SET status='canceled', updated_at=$ua WHERE status IN ('queued','running') AND (claimed_by=$m OR target_machine_id=$m)";
                c.Parameters.AddWithValue("$ua", now);
                c.Parameters.AddWithValue("$m", machineId);
                n = c.ExecuteNonQuery();
            }
            foreach (var tbl in new[] { "leases", "account_leases" })
                using (var c = _conn.CreateCommand())
                {
                    c.CommandText = $"DELETE FROM {tbl} WHERE machine_id=$m";
                    c.Parameters.AddWithValue("$m", machineId);
                    c.ExecuteNonQuery();
                }
            return n;
        }
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

    // ── Leases (khoá việc theo shop+op) ─────────────────────────────────────────
    public LeaseAcquireResponse AcquireLease(LeaseAcquireRequest r)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var existing = ReadLeaseLocked(r.Key);
            if (existing is not null
                && !string.Equals(existing.MachineId, r.MachineId, StringComparison.Ordinal)
                && existing.Status is "running" or "finishing"
                && (now - existing.HeartbeatAt) < StaleLease
                && !r.Force)
            {
                return new LeaseAcquireResponse(false, existing.Hostname);
            }

            // KHOÁ THEO TÀI KHOẢN BigSeller (web-hub bổ sung): với bản chính workbook nằm ở Hub, 2 máy KHÁC
            // nhau chạy 2 op/2 shop của CÙNG acc (vd scrape shop1 ở máy A + update shop2 ở máy B) sẽ ghi đè
            // 2 bản workbook rời → mất dữ liệu khi push-back. AcquireLease chỉ khoá theo key (shop+op) nên hở.
            // Chặn: acc này đang do MÁY KHÁC giữ (lease tươi op bất kỳ) → từ chối, trừ khi Force.
            if (AccountScopedLease && !r.Force && !string.IsNullOrEmpty(r.BigsellerId))
            {
                var owner = AccountOwnersLocked(now);
                if (owner.TryGetValue(r.BigsellerId, out var om)
                    && !string.Equals(om, r.MachineId, StringComparison.Ordinal))
                    return new LeaseAcquireResponse(false, HostnameOfLocked(om));
            }

            using var c = _conn.CreateCommand();
            c.CommandText = @"
INSERT INTO leases(key,bigseller_id,shop_id,sheet,op,machine_id,hostname,acquired_at,heartbeat_at,status)
VALUES($k,$b,$s,$sh,$o,$m,$h,$a,$hb,'running')
ON CONFLICT(key) DO UPDATE SET
  bigseller_id=$b, shop_id=$s, sheet=$sh, op=$o, machine_id=$m, hostname=$h,
  acquired_at=$a, heartbeat_at=$hb, status='running';";
            c.Parameters.AddWithValue("$k", r.Key);
            c.Parameters.AddWithValue("$b", r.BigsellerId);
            c.Parameters.AddWithValue("$s", r.ShopId);
            c.Parameters.AddWithValue("$sh", r.Sheet);
            c.Parameters.AddWithValue("$o", r.Op);
            c.Parameters.AddWithValue("$m", r.MachineId);
            c.Parameters.AddWithValue("$h", r.Hostname);
            c.Parameters.AddWithValue("$a", Iso(now));
            c.Parameters.AddWithValue("$hb", Iso(now));
            c.ExecuteNonQuery();
            return new LeaseAcquireResponse(true, null);
        }
    }

    public void HeartbeatLease(string key, string machineId)
    {
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "UPDATE leases SET heartbeat_at=$hb WHERE key=$k AND machine_id=$m";
            c.Parameters.AddWithValue("$hb", Iso(DateTimeOffset.UtcNow));
            c.Parameters.AddWithValue("$k", key);
            c.Parameters.AddWithValue("$m", machineId);
            c.ExecuteNonQuery();
        }
    }

    public void ReleaseLease(string key, string machineId)
    {
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "DELETE FROM leases WHERE key=$k AND machine_id=$m";
            c.Parameters.AddWithValue("$k", key);
            c.Parameters.AddWithValue("$m", machineId);
            c.ExecuteNonQuery();
        }
    }

    public List<LeaseRecord> ActiveLeases()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var list = new List<LeaseRecord>();
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT key,bigseller_id,shop_id,sheet,op,machine_id,hostname,acquired_at,heartbeat_at,status FROM leases";
            using var rd = c.ExecuteReader();
            while (rd.Read())
            {
                var lr = ReadLeaseRow(rd);
                if ((now - lr.HeartbeatAt) < StaleLease) list.Add(lr);
            }
            return list;
        }
    }

    private LeaseRecord? ReadLeaseLocked(string key)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "SELECT key,bigseller_id,shop_id,sheet,op,machine_id,hostname,acquired_at,heartbeat_at,status FROM leases WHERE key=$k";
        c.Parameters.AddWithValue("$k", key);
        using var rd = c.ExecuteReader();
        return rd.Read() ? ReadLeaseRow(rd) : null;
    }

    private static LeaseRecord ReadLeaseRow(SqliteDataReader rd) => new()
    {
        Key = S(rd, 0), BigsellerId = S(rd, 1), ShopId = S(rd, 2), Sheet = S(rd, 3), Op = S(rd, 4),
        MachineId = S(rd, 5), Hostname = S(rd, 6),
        AcquiredAt = D(rd, 7), HeartbeatAt = D(rd, 8), Status = S(rd, 9),
    };

    // ── Account leases (chống dùng trùng acc Shopee xuyên máy) ───────────────────
    public AccountReserveResponse ReserveAccounts(AccountReserveRequest r)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var granted = new List<string>();
            var blocked = new List<string>();
            foreach (var id in r.AccountIds.Distinct(StringComparer.Ordinal))
            {
                var holder = ReadAccountLeaseLocked(id);
                if (holder is not null
                    && !string.Equals(holder.MachineId, r.MachineId, StringComparison.Ordinal)
                    && (now - holder.HeartbeatAt) < StaleAccount)
                {
                    blocked.Add(id);
                    continue;
                }
                using var c = _conn.CreateCommand();
                c.CommandText = @"
INSERT INTO account_leases(account_id,machine_id,hostname,heartbeat_at)
VALUES($id,$m,$h,$hb)
ON CONFLICT(account_id) DO UPDATE SET machine_id=$m, hostname=$h, heartbeat_at=$hb;";
                c.Parameters.AddWithValue("$id", id);
                c.Parameters.AddWithValue("$m", r.MachineId);
                c.Parameters.AddWithValue("$h", r.Hostname);
                c.Parameters.AddWithValue("$hb", Iso(now));
                c.ExecuteNonQuery();
                granted.Add(id);
            }
            return new AccountReserveResponse(granted, blocked);
        }
    }

    public void ReleaseAccounts(AccountReleaseRequest r)
    {
        lock (_gate)
        {
            foreach (var id in r.AccountIds.Distinct(StringComparer.Ordinal))
            {
                using var c = _conn.CreateCommand();
                c.CommandText = "DELETE FROM account_leases WHERE account_id=$id AND machine_id=$m";
                c.Parameters.AddWithValue("$id", id);
                c.Parameters.AddWithValue("$m", r.MachineId);
                c.ExecuteNonQuery();
            }
        }
    }

    public void HeartbeatAccounts(AccountReleaseRequest r)
    {
        lock (_gate)
        {
            foreach (var id in r.AccountIds.Distinct(StringComparer.Ordinal))
            {
                using var c = _conn.CreateCommand();
                c.CommandText = "UPDATE account_leases SET heartbeat_at=$hb WHERE account_id=$id AND machine_id=$m";
                c.Parameters.AddWithValue("$hb", Iso(DateTimeOffset.UtcNow));
                c.Parameters.AddWithValue("$id", id);
                c.Parameters.AddWithValue("$m", r.MachineId);
                c.ExecuteNonQuery();
            }
        }
    }

    public List<AccountLease> ActiveAccountLeases()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var list = new List<AccountLease>();
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT account_id,machine_id,hostname,heartbeat_at FROM account_leases";
            using var rd = c.ExecuteReader();
            while (rd.Read())
            {
                var al = new AccountLease { AccountId = S(rd, 0), MachineId = S(rd, 1), Hostname = S(rd, 2), HeartbeatAt = D(rd, 3) };
                if ((now - al.HeartbeatAt) < StaleAccount) list.Add(al);
            }
            return list;
        }
    }

    private AccountLease? ReadAccountLeaseLocked(string accountId)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "SELECT account_id,machine_id,hostname,heartbeat_at FROM account_leases WHERE account_id=$id";
        c.Parameters.AddWithValue("$id", accountId);
        using var rd = c.ExecuteReader();
        return rd.Read()
            ? new AccountLease { AccountId = S(rd, 0), MachineId = S(rd, 1), Hostname = S(rd, 2), HeartbeatAt = D(rd, 3) }
            : null;
    }

    // ── Ledger (sổ hoàn thành; gộp khoảng dòng phía server) ──────────────────────
    public void PublishLedger(WorkLedgerRecord incoming)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var existing = ReadLedgerLocked(incoming.Key);
            var completed = existing?.Completed ?? new List<RowRange>();
            foreach (var rr in incoming.Completed ?? [])
                completed = RowRangeMath.Merge(completed, rr.From, rr.To);
            var lastRow = Math.Max(existing?.LastRowReached ?? 0, incoming.LastRowReached);

            using var c = _conn.CreateCommand();
            c.CommandText = @"
INSERT INTO ledger(key,bigseller_id,shop_id,sheet,op,completed_json,last_row,status,last_machine_id,last_hostname,last_run_at,updated_at)
VALUES($k,$b,$s,$sh,$o,$cj,$lr,$st,$lm,$lh,$lra,$ua)
ON CONFLICT(key) DO UPDATE SET
  bigseller_id=$b, shop_id=$s, sheet=$sh, op=$o, completed_json=$cj, last_row=$lr,
  status=$st, last_machine_id=$lm, last_hostname=$lh, last_run_at=$lra, updated_at=$ua;";
            c.Parameters.AddWithValue("$k", incoming.Key);
            c.Parameters.AddWithValue("$b", incoming.BigsellerId);
            c.Parameters.AddWithValue("$s", incoming.ShopId);
            c.Parameters.AddWithValue("$sh", incoming.Sheet);
            c.Parameters.AddWithValue("$o", incoming.Op);
            c.Parameters.AddWithValue("$cj", JsonSerializer.Serialize(completed));
            c.Parameters.AddWithValue("$lr", lastRow);
            c.Parameters.AddWithValue("$st", incoming.Status);
            c.Parameters.AddWithValue("$lm", incoming.LastMachineId);
            c.Parameters.AddWithValue("$lh", incoming.LastHostname);
            c.Parameters.AddWithValue("$lra", (object?)Iso(incoming.LastRunAt) ?? DBNull.Value);
            c.Parameters.AddWithValue("$ua", Iso(now));
            c.ExecuteNonQuery();
        }
    }

    /// <summary>Hub ĐẶT TAY trạng thái sổ cho 1 (shop+op). idle/rỗng → XOÁ bản ghi (kèm tiến độ dòng) = "chưa
    /// chạy" → scrape giao lại + chạy lại từ đầu. completed/stopped → ghi đè status (GIỮ completed/last_row cũ),
    /// KHÔNG gộp khoảng dòng. Khác PublishLedger (gộp) — đây là can thiệp thủ công của operator.</summary>
    public void SetLedgerStatus(string key, string bigsellerId, string shopId, string sheet, string op, string status)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (string.IsNullOrWhiteSpace(status) || string.Equals(status, "idle", StringComparison.OrdinalIgnoreCase))
            {
                using var d = _conn.CreateCommand();
                d.CommandText = "DELETE FROM ledger WHERE key=$k";
                d.Parameters.AddWithValue("$k", key);
                d.ExecuteNonQuery();
                return;
            }
            var existing = ReadLedgerLocked(key);
            using var c = _conn.CreateCommand();
            c.CommandText = @"
INSERT INTO ledger(key,bigseller_id,shop_id,sheet,op,completed_json,last_row,status,last_machine_id,last_hostname,last_run_at,updated_at)
VALUES($k,$b,$s,$sh,$o,$cj,$lr,$st,'','',$ua,$ua)
ON CONFLICT(key) DO UPDATE SET status=$st, updated_at=$ua;";
            c.Parameters.AddWithValue("$k", key);
            c.Parameters.AddWithValue("$b", bigsellerId);
            c.Parameters.AddWithValue("$s", shopId);
            c.Parameters.AddWithValue("$sh", sheet);
            c.Parameters.AddWithValue("$o", op);
            c.Parameters.AddWithValue("$cj", existing is null ? "[]" : JsonSerializer.Serialize(existing.Completed));
            c.Parameters.AddWithValue("$lr", existing?.LastRowReached ?? 0);
            c.Parameters.AddWithValue("$st", status);
            c.Parameters.AddWithValue("$ua", Iso(now));
            c.ExecuteNonQuery();
        }
    }

    public List<WorkLedgerRecord> AllLedger()
    {
        lock (_gate)
        {
            var list = new List<WorkLedgerRecord>();
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT key,bigseller_id,shop_id,sheet,op,completed_json,last_row,status,last_machine_id,last_hostname,last_run_at,updated_at FROM ledger";
            using var rd = c.ExecuteReader();
            while (rd.Read()) list.Add(ReadLedgerRow(rd));
            return list;
        }
    }

    private WorkLedgerRecord? ReadLedgerLocked(string key)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "SELECT key,bigseller_id,shop_id,sheet,op,completed_json,last_row,status,last_machine_id,last_hostname,last_run_at,updated_at FROM ledger WHERE key=$k";
        c.Parameters.AddWithValue("$k", key);
        using var rd = c.ExecuteReader();
        return rd.Read() ? ReadLedgerRow(rd) : null;
    }

    private static WorkLedgerRecord ReadLedgerRow(SqliteDataReader rd)
    {
        var completed = new List<RowRange>();
        var cj = S(rd, 5);
        if (!string.IsNullOrWhiteSpace(cj))
        {
            try { completed = JsonSerializer.Deserialize<List<RowRange>>(cj) ?? new(); } catch { }
        }
        return new WorkLedgerRecord
        {
            Key = S(rd, 0), BigsellerId = S(rd, 1), ShopId = S(rd, 2), Sheet = S(rd, 3), Op = S(rd, 4),
            Completed = completed, LastRowReached = rd.IsDBNull(6) ? 0 : rd.GetInt32(6),
            Status = S(rd, 7), LastMachineId = S(rd, 8), LastHostname = S(rd, 9),
            LastRunAt = rd.IsDBNull(10) ? null : D(rd, 10), UpdatedAt = D(rd, 11),
        };
    }

    // ── Machines (nhịp sống) ─────────────────────────────────────────────────────
    public void MachineHeartbeat(MachineHeartbeatRequest r)
    {
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = @"
INSERT INTO machines(machine_id,hostname,last_seen,app_version)
VALUES($m,$h,$ls,$v)
ON CONFLICT(machine_id) DO UPDATE SET hostname=$h, last_seen=$ls, app_version=$v;";
            c.Parameters.AddWithValue("$m", r.MachineId);
            c.Parameters.AddWithValue("$h", r.Hostname);
            c.Parameters.AddWithValue("$ls", Iso(DateTimeOffset.UtcNow));
            c.Parameters.AddWithValue("$v", (object?)r.AppVersion ?? DBNull.Value);
            c.ExecuteNonQuery();
        }
    }

    /// <summary>TẤT CẢ máy đã từng kết nối — GIỮ cả máy offline (mất nhịp/đóng app) để theo dõi. Chỉ máy
    /// chủ động bấm "Ngắt kết nối" (<see cref="RemoveMachine"/>) mới bị xoá khỏi danh sách.</summary>
    public List<MachinePresence> AllMachines()
    {
        lock (_gate)
        {
            var list = new List<MachinePresence>();
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT machine_id,hostname,last_seen,app_version FROM machines";
            using var rd = c.ExecuteReader();
            while (rd.Read())
                list.Add(new MachinePresence { MachineId = S(rd, 0), Hostname = S(rd, 1), LastSeen = D(rd, 2), AppVersion = rd.IsDBNull(3) ? null : rd.GetString(3) });
            return list;
        }
    }

    /// <summary>Xoá hẳn 1 máy khỏi danh sách khi nó CHỦ ĐỘNG ngắt kết nối (biến mất NGAY ở lần poll kế). Máy chỉ
    /// OFFLINE (đóng app/mất nhịp) thì GIỮ lại để theo dõi — KHÔNG tự xoá.</summary>
    public void RemoveMachine(string machineId) { lock (_gate) DeleteMachineLocked(machineId); }

    private void DeleteMachineLocked(string machineId)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "DELETE FROM machines WHERE machine_id=$m; DELETE FROM machine_roles WHERE machine_id=$m;";
        c.Parameters.AddWithValue("$m", machineId);
        c.ExecuteNonQuery();
    }

    public FleetSnapshot Fleet() => new()
    {
        Leases = ActiveLeases(),
        AccountLeases = ActiveAccountLeases(),
        Ledger = AllLedger(),
        Machines = AllMachines(),
        Roles = AllRoles(),
        Assignments = ListAssignments(),
    };

    // ── Vai trò máy + Giao việc (Hub đẩy việc cho client) ────────────────────────
    public TimeSpan StaleMachine { get; init; } = TimeSpan.FromSeconds(45);
    /// <summary>Việc 'running' quá lâu không có nhịp (worker báo "running" mỗi ~10s) ⇒ coi như máy nhận đã
    /// thoát → đánh 'failed' để nhả khoá single-session cho tài khoản đó. Khoá lease (5') là lưới cuối.</summary>
    public TimeSpan StaleRunning { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Đánh 'failed' cho việc 'running' đã hết nhịp (worker chết) → tránh kẹt tài khoản vĩnh viễn.
    /// QUAN TRỌNG: LEASE là tín hiệu "đang chạy" ĐÁNG TIN HƠN nhịp assignment. Nhịp assignment tắt khi client
    /// mất kết nối/hub restart/hub down &gt;5' (nhưng máy vẫn CHẠY THẬT); lease heartbeat 30s lại tự hồi khi
    /// kết nối trở lại. Sweep cũ chỉ nhìn updated_at của assignment → đánh 'failed' oan việc đang chạy → hub mất
    /// khả năng HUỶ việc hub-giao (việc kẹt vô hình). Fix: (1) KHÔNG fail nếu lease còn sống; (2) HỒI SINH việc bị
    /// sweep đánh oan khi lease sống lại (client re-sync) → assignment khớp thực tế, hub huỷ lại được.</summary>
    private void SweepStaleLocked(DateTimeOffset now)
    {
        var cut = Iso(now - StaleRunning);
        // Lease "còn sống" của CÙNG (acc,shop,op) do CHÍNH máy nhận giữ, heartbeat trong ngưỡng stale.
        const string leaseAlive =
            "EXISTS (SELECT 1 FROM leases l WHERE l.bigseller_id=assignments.bigseller_id " +
            "AND l.shop_id=assignments.shop_id AND l.op=assignments.op " +
            "AND l.machine_id=assignments.claimed_by AND l.heartbeat_at >= $cut)";

        // (1) 'running' hết nhịp → 'failed', TRỪ KHI lease còn sống (máy vẫn chạy thật).
        using (var c = _conn.CreateCommand())
        {
            c.CommandText = "UPDATE assignments SET status='failed', last_error='hết nhịp (máy nhận có thể đã thoát)', updated_at=$ua "
                + "WHERE status='running' AND updated_at < $cut AND NOT " + leaseAlive;
            c.Parameters.AddWithValue("$ua", Iso(now));
            c.Parameters.AddWithValue("$cut", cut);
            c.ExecuteNonQuery();
        }

        // (2) HỒI SINH: việc bị CHÍNH sweep này đánh 'failed' nhưng lease VẪN SỐNG (client đã re-sync) → về 'running'
        //     để hub huỷ được. Chỉ hồi đúng cờ 'hết nhịp' + lease sống của đúng máy đã claim (an toàn: job thật lỗi
        //     thì client nhả lease → lease chết → không hồi).
        using (var c = _conn.CreateCommand())
        {
            c.CommandText = "UPDATE assignments SET status='running', last_error='', updated_at=$ua "
                + "WHERE status='failed' AND last_error='hết nhịp (máy nhận có thể đã thoát)' AND " + leaseAlive;
            c.Parameters.AddWithValue("$ua", Iso(now));
            c.Parameters.AddWithValue("$cut", cut);
            c.ExecuteNonQuery();
        }
    }

    public void SetRole(string machineId, string role)
    {
        if (string.IsNullOrWhiteSpace(machineId)) return;
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = @"
INSERT INTO machine_roles(machine_id,role) VALUES($m,$r)
ON CONFLICT(machine_id) DO UPDATE SET role=$r;";
            c.Parameters.AddWithValue("$m", machineId);
            c.Parameters.AddWithValue("$r", role ?? MachineRoles.Off);
            c.ExecuteNonQuery();
        }
    }

    public List<MachineRoleInfo> AllRoles()
    {
        lock (_gate)
        {
            var list = new List<MachineRoleInfo>();
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT machine_id,role FROM machine_roles";
            using var rd = c.ExecuteReader();
            while (rd.Read()) list.Add(new MachineRoleInfo { MachineId = S(rd, 0), Role = S(rd, 1) });
            return list;
        }
    }

    private string RoleOfLocked(string machineId)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "SELECT role FROM machine_roles WHERE machine_id=$m";
        c.Parameters.AddWithValue("$m", machineId);
        var v = c.ExecuteScalar() as string;
        return string.IsNullOrWhiteSpace(v) ? MachineRoles.Off : v;
    }

    /// <summary>Tạo việc; trùng (cùng bigseller+shop+op) đang chờ/chạy thì TRẢ LẠI việc cũ (idempotent).</summary>
    public Assignment CreateAssignment(CreateAssignmentRequest r)
    {
        lock (_gate)
        {
            // Op TỰ ĐỘNG (không ghim) đã 'completed' trong ledger → KHÔNG tạo lại (chống re-run do NextOp đọc
            // snapshot ledger trễ ở Hub). Ghim tay (Pinned) thì vẫn cho phép để operator chủ động chạy lại.
            if (!r.Pinned)
            {
                var selfLed = ReadLedgerLocked($"{r.BigsellerId}__{r.ShopId}__{r.Op}");
                if (selfLed?.Status == "completed")
                    return new Assignment { BigsellerId = r.BigsellerId, ShopId = r.ShopId, Sheet = r.Sheet, Op = r.Op, Status = "done" };
            }

            var dup = FindOpenAssignmentLocked(r.BigsellerId, r.ShopId, r.Op);
            if (dup is not null)
            {
                // Việc CÒN CHỜ (queued): người dùng giao lại có thể đổi đích/ghim/khoảng dòng/PAYLOAD (vd Search
                // đổi slice link, lane, khu vực) → cập nhật bản chờ để chạy đúng cái mới. RUNNING thì KHÔNG đụng.
                if (dup.Status == "queued")
                {
                    using var u = _conn.CreateCommand();
                    u.CommandText = "UPDATE assignments SET target_machine_id=$t, pinned=$p, start_row=$sr, end_row=$er, payload=$pl, updated_at=$ua WHERE id=$id AND status='queued'";
                    u.Parameters.AddWithValue("$t", (object?)r.TargetMachineId ?? DBNull.Value);
                    u.Parameters.AddWithValue("$p", r.Pinned ? 1 : 0);
                    u.Parameters.AddWithValue("$sr", r.StartRow);
                    u.Parameters.AddWithValue("$er", r.EndRow);
                    u.Parameters.AddWithValue("$pl", r.Payload ?? "");
                    u.Parameters.AddWithValue("$ua", Iso(DateTimeOffset.UtcNow));
                    u.Parameters.AddWithValue("$id", dup.Id);
                    u.ExecuteNonQuery();
                    dup.TargetMachineId = r.TargetMachineId; dup.Pinned = r.Pinned;
                    dup.StartRow = r.StartRow; dup.EndRow = r.EndRow; dup.Payload = r.Payload ?? "";
                }
                return dup;
            }

            var now = DateTimeOffset.UtcNow;
            var a = new Assignment
            {
                Id = Guid.NewGuid().ToString("N"),
                BigsellerId = r.BigsellerId, ShopId = r.ShopId, Sheet = r.Sheet, Op = r.Op,
                TargetMachineId = r.TargetMachineId, Pinned = r.Pinned,
                StartRow = r.StartRow, EndRow = r.EndRow, Payload = r.Payload ?? "",
                Status = "queued", CreatedAt = now, UpdatedAt = now,
            };
            using var c = _conn.CreateCommand();
            c.CommandText = @"
INSERT INTO assignments(id,bigseller_id,shop_id,sheet,op,target_machine_id,pinned,status,claimed_by,claimed_host,last_error,created_at,updated_at,start_row,end_row,payload)
VALUES($id,$b,$s,$sh,$o,$t,$p,'queued','','','',$ca,$ua,$sr,$er,$pl);";
            c.Parameters.AddWithValue("$id", a.Id);
            c.Parameters.AddWithValue("$b", a.BigsellerId);
            c.Parameters.AddWithValue("$s", a.ShopId);
            c.Parameters.AddWithValue("$sh", a.Sheet);
            c.Parameters.AddWithValue("$o", a.Op);
            c.Parameters.AddWithValue("$t", (object?)a.TargetMachineId ?? DBNull.Value);
            c.Parameters.AddWithValue("$p", a.Pinned ? 1 : 0);
            c.Parameters.AddWithValue("$ca", Iso(now));
            c.Parameters.AddWithValue("$ua", Iso(now));
            c.Parameters.AddWithValue("$sr", a.StartRow);
            c.Parameters.AddWithValue("$er", a.EndRow);
            c.Parameters.AddWithValue("$pl", a.Payload ?? "");
            c.ExecuteNonQuery();
            return a;
        }
    }

    /// <summary>Việc đang mở (queued|running) cho 1 đơn vị (bigseller+shop+op). null nếu không có.</summary>
    private Assignment? FindOpenAssignmentLocked(string bsId, string shopId, string op)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "SELECT * FROM assignments WHERE bigseller_id=$b AND shop_id=$s AND op=$o AND status IN ('queued','running') LIMIT 1";
        c.Parameters.AddWithValue("$b", bsId);
        c.Parameters.AddWithValue("$s", shopId);
        c.Parameters.AddWithValue("$o", op);
        using var rd = c.ExecuteReader();
        return rd.Read() ? ReadAssignmentRow(rd) : null;
    }

    public List<Assignment> ListAssignments()
    {
        lock (_gate)
        {
            SweepStaleLocked(DateTimeOffset.UtcNow);
            var list = new List<Assignment>();
            using var c = _conn.CreateCommand();
            // Bỏ việc đã kết thúc lâu (>2h) để bảng gọn.
            c.CommandText = "SELECT * FROM assignments WHERE status IN ('queued','running') OR updated_at > $cut ORDER BY created_at";
            c.Parameters.AddWithValue("$cut", Iso(DateTimeOffset.UtcNow - TimeSpan.FromHours(2)));
            using var rd = c.ExecuteReader();
            while (rd.Read()) list.Add(ReadAssignmentRow(rd));
            return list;
        }
    }

    /// <summary>
    /// Máy <paramref name="machineId"/> (vai trò <paramref name="role"/>) lấy tối đa <paramref name="max"/> việc
    /// đủ điều kiện: đúng vai trò / được ghim, GIỮ nguyên tắc single-session (1 op/1 tài khoản BigSeller),
    /// đúng thứ tự pipeline (import sau khi scrape xong, update sau khi import xong). Atomic dưới _gate.
    /// </summary>
    public List<Assignment> ClaimNext(string machineId, string role, int max)
    {
        if (string.IsNullOrWhiteSpace(machineId) || max <= 0) return [];
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            SweepStaleLocked(now);
            var host = HostnameOfLocked(machineId);
            var claimed = new List<Assignment>();

            // (Tài khoản, op) đang BẬN cho scrape/import (lease tươi BẤT KỲ máy nào, HOẶC assignment running).
            var busy = BusyOpsLocked(now);
            // Chủ sở hữu acc hiện tại: máy nào đang chạy BẤT KỲ op (scrape/import/update) của acc đó. GHIM acc
            // về 1 máy tại 1 thời điểm — máy KHÁC không được đụng op của acc đang do máy khác giữ (chống 1 cookie
            // BigSeller bị dùng từ nhiều IP cùng lúc → BigSeller đá phiên → login lại mãi khi chạy nhiều client).
            var owner = AccountOwnersLocked(now);

            // MỖI CLIENT CHỈ CHẠY 1 TÀI KHOẢN BigSeller TẠI 1 THỜI ĐIỂM: nếu máy này ĐANG giữ 1 acc (owner map),
            // chỉ cho nhận thêm việc của CHÍNH acc đó; chưa giữ acc nào thì acc đầu tiên nhận trong lượt này sẽ
            // khoá máy vào acc đó tới hết lượt. Search (bigseller_id rỗng) KHÔNG tính. Kết hợp với khoá acc-về-1-máy
            // ở trên → tại 1 thời điểm client↔acc là 1:1, tránh 1 máy mở nhiều phiên BigSeller song song.
            string? myAccount = null;
            foreach (var kv in owner)
                if (string.Equals(kv.Value, machineId, StringComparison.Ordinal) && kv.Key.Length > 0) { myAccount = kv.Key; break; }

            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT * FROM assignments WHERE status='queued' ORDER BY created_at";
            var candidates = new List<Assignment>();
            using (var rd = c.ExecuteReader())
                while (rd.Read()) candidates.Add(ReadAssignmentRow(rd));

            foreach (var a in candidates)
            {
                if (claimed.Count >= max) break;
                // Định tuyến: ghim đúng máy này, hoặc (không ghim) vai trò khớp op.
                var routed = a.Pinned || !string.IsNullOrEmpty(a.TargetMachineId)
                    ? string.Equals(a.TargetMachineId, machineId, StringComparison.Ordinal)
                    : MachineRoles.Handles(role, a.Op);
                if (!routed) continue;
                // GHIM acc về 1 máy (xuyên máy): acc đang do MÁY KHÁC giữ → bỏ qua. Máy ĐANG giữ acc vẫn lấy
                // thêm op/shop khác của chính acc đó (cùng 1 IP máy, BigSeller không coi là phiên lạ).
                if (owner.TryGetValue(a.BigsellerId, out var om) && !string.Equals(om, machineId, StringComparison.Ordinal)) continue;
                // 1 CLIENT = 1 ACC: máy đã bận acc khác → bỏ qua (acc khác rỗng nghĩa search thì không chặn).
                if (myAccount is not null && a.BigsellerId.Length > 0 && !string.Equals(a.BigsellerId, myAccount, StringComparison.Ordinal)) continue;
                // Trong CÙNG máy: giữ giới hạn cũ — mỗi acc chỉ 1 shop scrape + 1 shop import cùng lúc (scrape ↔
                // import vẫn song song, kể cả cùng shop). UPDATE không giới hạn (shop nào cũng chạy).
                if (a.Op is "scrape" or "import" && busy.Contains($"{a.BigsellerId}__{a.Op}")) continue;
                // Thứ tự pipeline.
                if (!PipelineReadyLocked(a)) continue;

                using var u = _conn.CreateCommand();
                u.CommandText = "UPDATE assignments SET status='running', claimed_by=$m, claimed_host=$h, updated_at=$ua WHERE id=$id AND status='queued'";
                u.Parameters.AddWithValue("$m", machineId);
                u.Parameters.AddWithValue("$h", host);
                u.Parameters.AddWithValue("$ua", Iso(now));
                u.Parameters.AddWithValue("$id", a.Id);
                if (u.ExecuteNonQuery() == 1)
                {
                    a.Status = "running"; a.ClaimedByMachineId = machineId; a.ClaimedByHostname = host; a.UpdatedAt = now;
                    claimed.Add(a);
                    if (a.Op is "scrape" or "import") busy.Add($"{a.BigsellerId}__{a.Op}");   // không cấp thêm CÙNG op cho acc này trong cùng lượt
                    if (myAccount is null && a.BigsellerId.Length > 0) myAccount = a.BigsellerId;   // khoá máy vào acc vừa nhận (1 client = 1 acc)
                }
            }
            return claimed;
        }
    }

    /// <summary>Tập (tài khoản, op) đang BẬN cho các op CẦN ĐỘC-QUYỀN-THEO-ACC = scrape, import: mỗi acc chỉ
    /// 1 shop scrape + 1 shop import cùng lúc. scrape ↔ import KHÔNG chặn nhau (cùng/khác shop chạy song song).
    /// UPDATE KHÔNG tính ở đây (shop nào cũng chạy được, vì đã import chọn shop). Key = "{bigsellerId}__{op}".
    /// Nguồn bận: lease tươi hoặc assignment đang running.</summary>
    private HashSet<string> BusyOpsLocked(DateTimeOffset now)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        using (var c = _conn.CreateCommand())
        {
            c.CommandText = "SELECT bigseller_id,op,heartbeat_at,status FROM leases";
            using var rd = c.ExecuteReader();
            while (rd.Read())
            {
                var hb = DateTimeOffset.TryParse(S(rd, 2), out var d) ? d : DateTimeOffset.MinValue;
                if ((now - hb) < StaleLease && S(rd, 3) is "running" or "finishing" && S(rd, 1) is "scrape" or "import")
                    set.Add($"{S(rd, 0)}__{S(rd, 1)}");
            }
        }
        using (var c = _conn.CreateCommand())
        {
            c.CommandText = "SELECT bigseller_id,op FROM assignments WHERE status='running' AND op IN ('scrape','import')";
            using var rd = c.ExecuteReader();
            while (rd.Read()) set.Add($"{S(rd, 0)}__{S(rd, 1)}");
        }
        return set;
    }

    /// <summary>Chủ sở hữu hiện tại của mỗi tài khoản BigSeller = máy đang chạy BẤT KỲ op nào (scrape/import/
    /// update) của acc đó, lấy từ lease TƯƠI hoặc assignment đang 'running'. Dùng để GHIM acc về 1 máy tại 1
    /// thời điểm: máy khác không được claim op của acc đang do máy khác giữ → 1 cookie BigSeller không bị dùng từ
    /// nhiều IP cùng lúc (nguyên nhân BigSeller đá phiên, login lại mãi khi chạy nhiều client). Key = bigsellerId
    /// → machineId (giá trị đầu gặp; sau khi hội tụ chỉ 1 máy/acc — giai đoạn quá độ nếu 2 máy còn giữ thì 1 máy
    /// được ghi nhận, máy kia bị chặn, tự hội tụ). Máy chết KHÔNG khoá acc vĩnh viễn: SweepStaleLocked đánh 'failed'
    /// assignment quá hạn nhịp + lease có ngưỡng tươi (StaleLease) nên đều tự nhả.</summary>
    private Dictionary<string, string> AccountOwnersLocked(DateTimeOffset now)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        void Put(string bs, string m) { if (bs.Length > 0 && m.Length > 0 && !map.ContainsKey(bs)) map[bs] = m; }
        using (var c = _conn.CreateCommand())
        {
            c.CommandText = "SELECT bigseller_id,machine_id,heartbeat_at,status FROM leases";
            using var rd = c.ExecuteReader();
            while (rd.Read())
            {
                var hb = DateTimeOffset.TryParse(S(rd, 2), out var d) ? d : DateTimeOffset.MinValue;
                if ((now - hb) < StaleLease && S(rd, 3) is "running" or "finishing") Put(S(rd, 0), S(rd, 1));
            }
        }
        using (var c = _conn.CreateCommand())
        {
            c.CommandText = "SELECT bigseller_id,claimed_by FROM assignments WHERE status='running'";
            using var rd = c.ExecuteReader();
            while (rd.Read()) Put(S(rd, 0), S(rd, 1));
        }
        return map;
    }

    /// <summary>Import chỉ chạy khi scrape của (bs+shop) đã 'completed'; update khi import đã 'completed'.
    /// Op tự động đã 'completed' thì không claim lại (chống re-run khi 1 assignment lỡ lọt vào 'queued').</summary>
    private bool PipelineReadyLocked(Assignment a)
    {
        // GHIM TAY = operator CHỦ ĐỘNG chọn chạy op này trên máy này → BỎ QUA ràng buộc thứ tự pipeline (đừng
        // đòi scrape/import phải 'completed' trong ledger). Trước đây pinned vẫn bị chặn nên import/update ghim
        // tay kẹt 'đã xếp' mãi khi scrape chưa 'completed' (scrape dừng dở / login-first / cào ở máy khác).
        // Lưới an toàn vẫn còn: tiền-kiểm workbook/sheet phía client (CanDispatchUpdate) + single-session (busy).
        if (a.Pinned) return true;

        var self = ReadLedgerLocked($"{a.BigsellerId}__{a.ShopId}__{a.Op}");
        if (self?.Status == "completed") return false;
        string? need = a.Op switch { "import" => "scrape", "update" => "import", _ => null };
        if (need is null) return true;
        var key = $"{a.BigsellerId}__{a.ShopId}__{need}";
        var led = ReadLedgerLocked(key);
        return led is not null && led.Status == "completed";
    }

    public void UpdateAssignmentStatus(string id, string machineId, string status, string? error)
    {
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            if (status == "requeue")
            {
                // Máy nhận đã claim (running) nhưng CHƯA chạy được vì lỗi TẠM THỜI (client mới chưa kịp đồng bộ
                // tài khoản/workbook…) → trả việc về 'queued' + bỏ claim để claim lại nhịp sau. Giữ '• đã xếp'
                // thay vì lặng lẽ 'failed' → về 'chờ'. Chỉ tác động khi CHÍNH máy đó còn giữ (running).
                c.CommandText = "UPDATE assignments SET status='queued', claimed_by='', claimed_host='', last_error=$e, updated_at=$ua WHERE id=$id AND claimed_by=$m AND status='running'";
                c.Parameters.AddWithValue("$e", error ?? "");
                c.Parameters.AddWithValue("$ua", Iso(DateTimeOffset.UtcNow));
                c.Parameters.AddWithValue("$id", id);
                c.Parameters.AddWithValue("$m", machineId);
                c.ExecuteNonQuery();
                return;
            }
            // Guard: chỉ đổi khi đang 'running' (hoặc set lại CHÍNH trạng thái đó) → KHÔNG cho nhịp 'running'
            // hồi sinh việc đã done/failed/canceled (giữ Cancel có hiệu lực + không hiện job ma).
            c.CommandText = "UPDATE assignments SET status=$st, last_error=$e, updated_at=$ua WHERE id=$id AND claimed_by=$m AND (status='running' OR status=$st)";
            c.Parameters.AddWithValue("$st", status);
            c.Parameters.AddWithValue("$e", error ?? "");
            c.Parameters.AddWithValue("$ua", Iso(DateTimeOffset.UtcNow));
            c.Parameters.AddWithValue("$id", id);
            c.Parameters.AddWithValue("$m", machineId);
            c.ExecuteNonQuery();
        }
    }

    public void CancelAssignment(string id)
    {
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "UPDATE assignments SET status='canceled', updated_at=$ua WHERE id=$id AND status IN ('queued','running')";
            c.Parameters.AddWithValue("$ua", Iso(DateTimeOffset.UtcNow));
            c.Parameters.AddWithValue("$id", id);
            c.ExecuteNonQuery();
        }
    }

    private string HostnameOfLocked(string machineId)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "SELECT hostname FROM machines WHERE machine_id=$m";
        c.Parameters.AddWithValue("$m", machineId);
        return c.ExecuteScalar() as string ?? "";
    }

    private static Assignment ReadAssignmentRow(SqliteDataReader rd)
    {
        // Cột theo thứ tự bảng: id,bigseller_id,shop_id,sheet,op,target_machine_id,pinned,status,claimed_by,claimed_host,last_error,created_at,updated_at
        int i(string n) => rd.GetOrdinal(n);
        return new Assignment
        {
            Id = S(rd, i("id")), BigsellerId = S(rd, i("bigseller_id")), ShopId = S(rd, i("shop_id")),
            Sheet = S(rd, i("sheet")), Op = S(rd, i("op")),
            TargetMachineId = rd.IsDBNull(i("target_machine_id")) ? null : rd.GetString(i("target_machine_id")),
            Pinned = !rd.IsDBNull(i("pinned")) && rd.GetInt32(i("pinned")) != 0,
            Status = S(rd, i("status")), ClaimedByMachineId = S(rd, i("claimed_by")),
            ClaimedByHostname = S(rd, i("claimed_host")), LastError = S(rd, i("last_error")),
            StartRow = rd.IsDBNull(i("start_row")) ? 0 : rd.GetInt32(i("start_row")),
            EndRow = rd.IsDBNull(i("end_row")) ? 0 : rd.GetInt32(i("end_row")),
            Payload = S(rd, i("payload")),
            CreatedAt = D(rd, i("created_at")), UpdatedAt = D(rd, i("updated_at")),
        };
    }

    // ── Kho gộp kết quả Search (client đẩy sản phẩm cào được → Hub gộp, dedup theo item_id) ──
    /// <summary>Lưu 1 lô sản phẩm client gửi; trùng item_id thì GHI ĐÈ (bản mới nhất). Chạy trong 1 transaction.</summary>
    public void SaveSearchProducts(SearchProductsPushRequest r)
    {
        if (r?.Products is null || r.Products.Count == 0) return;
        lock (_gate)
        {
            var now = Iso(DateTimeOffset.UtcNow);
            using var tx = _conn.BeginTransaction();
            using var c = _conn.CreateCommand();
            c.Transaction = tx;
            c.CommandText = @"
INSERT INTO search_products(item_id,json,machine_id,source_file,updated_at)
VALUES($i,$j,$m,$s,$u)
ON CONFLICT(item_id) DO UPDATE SET json=$j, machine_id=$m, source_file=$s, updated_at=$u;";
            var pI = c.Parameters.Add("$i", SqliteType.Integer);
            var pJ = c.Parameters.Add("$j", SqliteType.Text);
            var pM = c.Parameters.Add("$m", SqliteType.Text);
            var pS = c.Parameters.Add("$s", SqliteType.Text);
            var pU = c.Parameters.Add("$u", SqliteType.Text);
            foreach (var p in r.Products)
            {
                if (p is null || p.ItemId == 0 || string.IsNullOrEmpty(p.Json)) continue;
                pI.Value = p.ItemId; pJ.Value = p.Json; pM.Value = r.MachineId ?? ""; pS.Value = r.SourceFile ?? ""; pU.Value = now;
                c.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Toàn bộ blob JSON sản phẩm đã gộp (để client xuất Excel gộp). Dedup đã sẵn theo item_id.</summary>
    public List<string> AllSearchProductJson()
    {
        lock (_gate)
        {
            var list = new List<string>();
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT json FROM search_products";
            using var rd = c.ExecuteReader();
            while (rd.Read()) { var j = S(rd, 0); if (!string.IsNullOrEmpty(j)) list.Add(j); }
            return list;
        }
    }

    public int SearchProductCount()
    {
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT COUNT(*) FROM search_products";
            return Convert.ToInt32(c.ExecuteScalar() ?? 0);
        }
    }

    public void ClearSearchProducts() { lock (_gate) ExecRaw("DELETE FROM search_products;"); }

    /// <summary>Từng bản ghi (máy đã đẩy, file nguồn, json) — cho bảng kết quả search theo máy.</summary>
    public List<(string MachineId, string SourceFile, string Json)> AllSearchProductRows()
    {
        lock (_gate)
        {
            var list = new List<(string, string, string)>();
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT machine_id,source_file,json FROM search_products";
            using var rd = c.ExecuteReader();
            while (rd.Read()) list.Add((S(rd, 0), S(rd, 1), S(rd, 2)));
            return list;
        }
    }

    /// <summary>Số sản phẩm kho gộp theo TỪNG MÁY (kết quả search của từng client).</summary>
    public List<(string MachineId, int Count)> SearchProductCountByMachine()
    {
        lock (_gate)
        {
            var list = new List<(string, int)>();
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT machine_id,COUNT(*) FROM search_products GROUP BY machine_id";
            using var rd = c.ExecuteReader();
            while (rd.Read()) list.Add((S(rd, 0), rd.GetInt32(1)));
            return list;
        }
    }

    // ── Log tập trung (nhiều máy gửi → Hub gom; giữ ~3000 dòng mới nhất) ──────────
    public void AppendLog(AppendLogRequest r)
    {
        if (string.IsNullOrWhiteSpace(r?.Text)) return;
        lock (_gate)
        {
            using (var c = _conn.CreateCommand())
            {
                c.CommandText = "INSERT INTO logs(machine_id,hostname,ts,level,text) VALUES($m,$h,$ts,$lv,$tx)";
                c.Parameters.AddWithValue("$m", r.MachineId ?? "");
                c.Parameters.AddWithValue("$h", r.Hostname ?? "");
                c.Parameters.AddWithValue("$ts", Iso(DateTimeOffset.UtcNow));
                c.Parameters.AddWithValue("$lv", string.IsNullOrWhiteSpace(r.Level) ? "info" : r.Level);
                c.Parameters.AddWithValue("$tx", r.Text);
                c.ExecuteNonQuery();
            }
            ExecRaw("DELETE FROM logs WHERE id <= (SELECT MAX(id) FROM logs) - 3000;");   // giữ 3000 mới nhất
        }
    }

    /// <summary>Log cho tab Log. after&lt;=0 → 'max' dòng MỚI NHẤT; after&gt;0 → các dòng id&gt;after (tăng dần).</summary>
    public List<LogEntry> GetLogs(long after, int max)
    {
        lock (_gate)
        {
            var list = new List<LogEntry>();
            using var c = _conn.CreateCommand();
            c.CommandText = after > 0
                ? "SELECT id,machine_id,hostname,ts,level,text FROM logs WHERE id > $a ORDER BY id ASC LIMIT $n"
                : "SELECT id,machine_id,hostname,ts,level,text FROM (SELECT * FROM logs ORDER BY id DESC LIMIT $n) ORDER BY id ASC";
            c.Parameters.AddWithValue("$a", after);
            c.Parameters.AddWithValue("$n", max);
            using var rd = c.ExecuteReader();
            while (rd.Read())
                list.Add(new LogEntry { Id = rd.GetInt64(0), MachineId = S(rd, 1), Hostname = S(rd, 2), Ts = D(rd, 3), Level = S(rd, 4), Text = S(rd, 5) });
            return list;
        }
    }

    public void ClearLogs() { lock (_gate) ExecRaw("DELETE FROM logs;"); }

    // ── Client báo acc Shopee lỗi/captcha (Hub xem + quyết giữ/xóa) ───────────────
    public void ReportAccountError(AccountErrorRequest r)
    {
        if (string.IsNullOrEmpty(r?.AccountId)) return;
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = @"
INSERT INTO account_errors(account_id,machine_id,hostname,reason,captcha_url,status,reported_at)
VALUES($id,$m,$h,$r,$cu,$st,$ua)
ON CONFLICT(account_id) DO UPDATE SET machine_id=$m, hostname=$h, reason=$r, captcha_url=$cu, status=$st, reported_at=$ua;";
            c.Parameters.AddWithValue("$id", r.AccountId);
            c.Parameters.AddWithValue("$m", r.MachineId ?? "");
            c.Parameters.AddWithValue("$h", r.Hostname ?? "");
            c.Parameters.AddWithValue("$r", r.Reason ?? "");
            c.Parameters.AddWithValue("$cu", (object?)r.CaptchaUrl ?? DBNull.Value);
            c.Parameters.AddWithValue("$st", string.IsNullOrWhiteSpace(r.Status) ? "captcha" : r.Status);
            c.Parameters.AddWithValue("$ua", Iso(DateTimeOffset.UtcNow));
            c.ExecuteNonQuery();
        }
    }

    public List<AccountError> AllAccountErrors()
    {
        lock (_gate)
        {
            var list = new List<AccountError>();
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT account_id,machine_id,hostname,reason,captcha_url,status,reported_at FROM account_errors";
            using var rd = c.ExecuteReader();
            while (rd.Read())
                list.Add(new AccountError
                {
                    AccountId = S(rd, 0), MachineId = S(rd, 1), Hostname = S(rd, 2), Reason = S(rd, 3),
                    CaptchaUrl = rd.IsDBNull(4) ? null : rd.GetString(4), Status = S(rd, 5), ReportedAt = D(rd, 6),
                });
            return list;
        }
    }

    public void ClearAccountError(string accountId)
    {
        if (string.IsNullOrEmpty(accountId)) return;
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "DELETE FROM account_errors WHERE account_id=$id";
            c.Parameters.AddWithValue("$id", accountId);
            c.ExecuteNonQuery();
        }
    }

    // ── Files (manifest + blob trên đĩa) ────────────────────────────────────────
    public List<FileManifestEntry> ListFiles()
    {
        lock (_gate)
        {
            var list = new List<FileManifestEntry>();
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT name,version,hash,size,mtime FROM files";
            using var rd = c.ExecuteReader();
            while (rd.Read())
                list.Add(new FileManifestEntry { Name = S(rd, 0), Version = rd.GetInt32(1), Hash = S(rd, 2), Size = rd.GetInt64(3), Mtime = D(rd, 4) });
            return list;
        }
    }

    public byte[]? ReadFile(string name)
    {
        var path = SafeFullPath(FilesDir, name);
        if (path is null) return null;
        // Đọc blob KHÔNG cần _gate (không đụng SqliteConnection); file ghi nguyên tử qua tmp+Move nên đọc luôn an toàn.
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>Ghi file. ifMatch != null ⇒ kiểm tra version khớp (optimistic concurrency).</summary>
    public FilePutResponse PutFile(string name, byte[] data, int? ifMatch, string updatedBy)
    {
        var path = SafeFullPath(FilesDir, name);
        if (path is null) return new FilePutResponse(false, 0, "bad-name");

        // Ghi bytes ra tmp NGOÀI lock (việc nặng) → không chặn lease/heartbeat của cả fleet. Tmp duy nhất (Guid)
        // để 2 client ghi cùng tên không đạp lên nhau. Chỉ rename + cập nhật DB mới nằm trong lock.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var hash = Sha256(data);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(tmp, data);

        lock (_gate)
        {
            var current = ReadFileMetaLocked(name);
            if (ifMatch.HasValue && current is not null && current.Version != ifMatch.Value)
            {
                try { File.Delete(tmp); } catch { }
                return new FilePutResponse(false, current.Version, "version-conflict");
            }

            // Guard hoa-thường (Linux case-sensitive): đã có 1 file trùng tên khác hoa-thường (vd 'Workbooks/x'
            // vs 'workbooks/x') → từ chối để manifest + thư mục files\ không tách đôi/nhân bản trên ext4.
            if (current is null)
            {
                var variant = FindCaseVariantLocked(name);
                if (variant is not null)
                {
                    try { File.Delete(tmp); } catch { }
                    return new FilePutResponse(false, 0, "case-variant:" + variant);
                }
            }

            var newVer = (current?.Version ?? 0) + 1;
            File.Move(tmp, path, overwrite: true);   // rename nhanh, giữ trong lock để khớp với bản ghi DB

            var now = DateTimeOffset.UtcNow;
            using var c = _conn.CreateCommand();
            c.CommandText = @"
INSERT INTO files(name,version,hash,size,mtime,updated_by,updated_at)
VALUES($n,$v,$h,$s,$mt,$ub,$ua)
ON CONFLICT(name) DO UPDATE SET version=$v, hash=$h, size=$s, mtime=$mt, updated_by=$ub, updated_at=$ua;";
            c.Parameters.AddWithValue("$n", name);
            c.Parameters.AddWithValue("$v", newVer);
            c.Parameters.AddWithValue("$h", hash);
            c.Parameters.AddWithValue("$s", (long)data.Length);
            c.Parameters.AddWithValue("$mt", Iso(now));
            c.Parameters.AddWithValue("$ub", updatedBy);
            c.Parameters.AddWithValue("$ua", Iso(now));
            c.ExecuteNonQuery();
            return new FilePutResponse(true, newVer, null);
        }
    }

    /// <summary>Tên file đã có trong manifest TRÙNG <paramref name="name"/> khi bỏ qua hoa-thường nhưng KHÁC
    /// chính tả (chỉ khác case). null nếu không có. Dùng để chặn nhân bản file trên Linux (case-sensitive).</summary>
    private string? FindCaseVariantLocked(string name)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "SELECT name FROM files WHERE name=$n COLLATE NOCASE AND name<>$n LIMIT 1";
        c.Parameters.AddWithValue("$n", name);
        return c.ExecuteScalar() as string;
    }

    /// <summary>Xoá 1 file dùng chung: bỏ bản ghi manifest + blob trên đĩa. Trả false nếu tên xấu.
    /// (Web-hub: trang Files cho admin xoá workbook cũ; UI CHẶN xoá config/* để khỏi tự bắn chân.)</summary>
    public bool DeleteFile(string name)
    {
        var path = SafeFullPath(FilesDir, name);
        if (path is null) return false;
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "DELETE FROM files WHERE name=$n";
            c.Parameters.AddWithValue("$n", name);
            c.ExecuteNonQuery();
        }
        try { if (File.Exists(path)) File.Delete(path); } catch { }
        return true;
    }

    private FileManifestEntry? ReadFileMetaLocked(string name)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "SELECT name,version,hash,size,mtime FROM files WHERE name=$n";
        c.Parameters.AddWithValue("$n", name);
        using var rd = c.ExecuteReader();
        return rd.Read()
            ? new FileManifestEntry { Name = S(rd, 0), Version = rd.GetInt32(1), Hash = S(rd, 2), Size = rd.GetInt64(3), Mtime = D(rd, 4) }
            : null;
    }

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
