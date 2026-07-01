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
/// </summary>
public sealed class HubDatabase : IDisposable
{
    private readonly object _gate = new();
    private readonly SqliteConnection _conn;

    public string FilesDir { get; }
    public TimeSpan StaleLease { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan StaleAccount { get; init; } = TimeSpan.FromMinutes(5);

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
  account_id TEXT PRIMARY KEY, machine_id TEXT, hostname TEXT, reason TEXT, captcha_url TEXT, status TEXT, reported_at TEXT);");

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

    /// <summary>Đánh 'failed' cho việc 'running' đã hết nhịp (worker chết) → tránh kẹt tài khoản vĩnh viễn.</summary>
    private void SweepStaleLocked(DateTimeOffset now)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "UPDATE assignments SET status='failed', last_error='hết nhịp (máy nhận có thể đã thoát)', updated_at=$ua WHERE status='running' AND updated_at < $cut";
        c.Parameters.AddWithValue("$ua", Iso(now));
        c.Parameters.AddWithValue("$cut", Iso(now - StaleRunning));
        c.ExecuteNonQuery();
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
                // Độc-quyền-theo-acc CHO scrape/import: mỗi acc chỉ 1 shop scrape + 1 shop import cùng lúc
                // (scrape ↔ import vẫn song song được, kể cả cùng shop). UPDATE không giới hạn (shop nào cũng chạy).
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
