using Microsoft.Data.Sqlite;
using Shopee.Core.Coordination;

namespace Shopee.Hub;

/// <summary>Phần HubDatabase: khoá việc theo shop+op (leases) + khoá tài khoản Shopee xuyên máy (account_leases).</summary>
public sealed partial class HubDatabase
{
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
}
