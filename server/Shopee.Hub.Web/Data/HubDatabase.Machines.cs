using Shopee.Core.Coordination;

namespace Shopee.Hub;

/// <summary>Phần HubDatabase: quản lý máy client — chặn/gỡ chặn (revoke), reset việc, nhịp sống, vai trò, hostname.</summary>
public sealed partial class HubDatabase
{
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

    // ── Machines (nhịp sống) ─────────────────────────────────────────────────────
    public void MachineHeartbeat(MachineHeartbeatRequest r)
    {
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = @"
INSERT INTO machines(machine_id,hostname,last_seen,app_version,max_brave)
VALUES($m,$h,$ls,$v,$mb)
ON CONFLICT(machine_id) DO UPDATE SET hostname=$h, last_seen=$ls, app_version=$v, max_brave=$mb;";
            c.Parameters.AddWithValue("$m", r.MachineId);
            c.Parameters.AddWithValue("$h", r.Hostname);
            c.Parameters.AddWithValue("$ls", Iso(DateTimeOffset.UtcNow));
            c.Parameters.AddWithValue("$v", (object?)r.AppVersion ?? DBNull.Value);
            c.Parameters.AddWithValue("$mb", r.MaxBrave);
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
            c.CommandText = "SELECT machine_id,hostname,last_seen,app_version,max_brave FROM machines";
            using var rd = c.ExecuteReader();
            while (rd.Read())
                list.Add(new MachinePresence { MachineId = S(rd, 0), Hostname = S(rd, 1), LastSeen = D(rd, 2), AppVersion = rd.IsDBNull(3) ? null : rd.GetString(3), MaxBrave = rd.IsDBNull(4) ? 0 : rd.GetInt32(4) });
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

    private string HostnameOfLocked(string machineId)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "SELECT hostname FROM machines WHERE machine_id=$m";
        c.Parameters.AddWithValue("$m", machineId);
        return c.ExecuteScalar() as string ?? "";
    }
}
