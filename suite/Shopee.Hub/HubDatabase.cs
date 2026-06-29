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
  updated_by TEXT, updated_at TEXT);");

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

    public FleetSnapshot Fleet() => new()
    {
        Leases = ActiveLeases(),
        AccountLeases = ActiveAccountLeases(),
        Ledger = AllLedger(),
        Machines = AllMachines(),
    };

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
