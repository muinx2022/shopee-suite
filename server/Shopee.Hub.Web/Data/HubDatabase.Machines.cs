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

    /// <summary>Reset 1 máy NGAY (không chờ 5' stale) = XOÁ SẠCH việc của máy: (1) huỷ mọi assignment máy đang giữ/
    /// được ghim (queued/running → canceled); (2) BỎ mọi việc gián đoạn (failed/canceled) của máy khỏi danh sách
    /// (dismissed=1) → nút ▶ Tiếp tục biến mất; (3) nhả mọi lease việc + account-lease của máy → acc nhả tức thì,
    /// ghim lại được. Sticky-cancel giữ nguyên (status không đổi). Trả (số huỷ, số bỏ gián đoạn).</summary>
    public (int Canceled, int Dismissed) ResetMachineWork(string machineId)
    {
        if (string.IsNullOrWhiteSpace(machineId)) return (0, 0);
        lock (_gate)
        {
            var now = Iso(DateTimeOffset.UtcNow);
            int canceled, dismissed;
            using (var c = _conn.CreateCommand())
            {
                c.CommandText = "UPDATE assignments SET status='canceled', updated_at=$ua WHERE status IN ('queued','running') AND (claimed_by=$m OR target_machine_id=$m)";
                c.Parameters.AddWithValue("$ua", now);
                c.Parameters.AddWithValue("$m", machineId);
                canceled = c.ExecuteNonQuery();
            }
            // Bỏ mọi việc gián đoạn của máy (kể cả bản vừa huỷ ở trên) khỏi danh sách "▶ Tiếp tục". KHÔNG bump
            // updated_at: cột này quyết "bản mới nhất mỗi nhóm" trong ListInterrupted; 1 nhóm có thể có bản CŨ của
            // máy này + bản MỚI HƠN của máy khác — bump sẽ đẩy bản dismissed lên mới nhất → ẩn OAN nhóm đang hiện
            // ở máy khác. Chỉ set cờ dismissed, giữ nguyên updated_at.
            using (var c = _conn.CreateCommand())
            {
                c.CommandText = "UPDATE assignments SET dismissed=1 WHERE status IN ('failed','canceled') AND dismissed=0 AND (claimed_by=$m OR target_machine_id=$m)";
                c.Parameters.AddWithValue("$m", machineId);
                dismissed = c.ExecuteNonQuery();
            }
            foreach (var tbl in new[] { "leases", "account_leases" })
                using (var c = _conn.CreateCommand())
                {
                    c.CommandText = $"DELETE FROM {tbl} WHERE machine_id=$m";
                    c.Parameters.AddWithValue("$m", machineId);
                    c.ExecuteNonQuery();
                }
            return (canceled, dismissed);
        }
    }

    // ── Machines (nhịp sống) ─────────────────────────────────────────────────────
    /// <summary>Nhịp sống máy + kênh đẩy lệnh update xuống client. Đọc cờ update trước: nếu cờ đang bật MÀ máy đã
    /// báo app_version KHÁC lúc ra lệnh → coi như update xong, tự clear cờ + ghi ✓. Trả về cờ CÒN LẠI để client
    /// tự quyết update (null = không có lệnh). Upsert nhịp KHÔNG đụng 3 cột update (chỉ RequestUpdate/AckUpdate/
    /// nhánh clear ở đây mới ghi).</summary>
    public MachineHeartbeatResponse MachineHeartbeat(MachineHeartbeatRequest r)
    {
        lock (_gate)
        {
            string requestedAt = "", requestedFrom = "";
            using (var c = _conn.CreateCommand())
            {
                c.CommandText = "SELECT update_requested_at, update_requested_from FROM machines WHERE machine_id=$m";
                c.Parameters.AddWithValue("$m", r.MachineId);
                using var rd = c.ExecuteReader();
                if (rd.Read()) { requestedAt = S(rd, 0); requestedFrom = S(rd, 1); }
            }
            // Cờ đang bật + máy báo bản MỚI (khác bản lúc ra lệnh) = đã update xong → tự clear cờ, ghi ✓.
            if (requestedAt.Length > 0 && !string.IsNullOrWhiteSpace(r.AppVersion) && r.AppVersion != requestedFrom)
            {
                using var c = _conn.CreateCommand();
                c.CommandText = "UPDATE machines SET update_requested_at='', update_requested_from='', update_status=$s WHERE machine_id=$m";
                c.Parameters.AddWithValue("$s", $"✓ đã lên v{r.AppVersion}");
                c.Parameters.AddWithValue("$m", r.MachineId);
                c.ExecuteNonQuery();
                requestedAt = "";   // coi như không còn lệnh
            }
            using (var c = _conn.CreateCommand())
            {
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
            return new MachineHeartbeatResponse { UpdateRequestedAt = requestedAt.Length == 0 ? null : requestedAt };
        }
    }

    /// <summary>Operator ra lệnh update app cho 1 máy: ghi mốc thời gian ra lệnh + app_version LÚC ra lệnh (mốc so
    /// "đã lên bản khác chưa" ở heartbeat) + dòng trạng thái chờ. Bấm LẠI khi cờ đang bật = ra lệnh MỚI (ghi đè
    /// timestamp) — đó là cách operator retry sau khi lần trước lỗi. Trả true nếu có máy khớp (rows>0).</summary>
    public bool RequestUpdate(string machineId)
    {
        if (string.IsNullOrWhiteSpace(machineId)) return false;
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = @"
UPDATE machines SET update_requested_at=$t, update_requested_from=COALESCE(app_version,''),
       update_status='⏳ đã ra lệnh — chờ máy nhận (nhịp 12s)…'
WHERE machine_id=$m;";
            c.Parameters.AddWithValue("$t", Iso(DateTimeOffset.UtcNow));
            c.Parameters.AddWithValue("$m", machineId);
            return c.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Client báo tiến trình/kết quả tự-update. Map status → dòng hiển thị; ack TERMINAL (already-latest/
    /// unsupported/failed…) CLEAR 2 cột cờ để hub thôi đẩy lệnh; ack tiến trình (checking/restarting) GIỮ cờ (máy
    /// đang làm, heartbeat kế còn cần lệnh). Status lạ: lưu nguyên văn, giữ cờ.</summary>
    public void AckUpdate(string machineId, string status)
    {
        if (string.IsNullOrWhiteSpace(machineId) || string.IsNullOrWhiteSpace(status)) return;
        var s = status.Trim();
        string text; bool clear;
        if (s == "checking") { text = "⏳ máy đang kiểm tra/tải bản mới…"; clear = false; }
        else if (s == "restarting") { text = "🔄 máy đang dừng việc êm + khởi động lại…"; clear = false; }
        else if (s == "already-latest") { text = "🟢 máy báo đã là bản mới nhất"; clear = true; }
        else if (s == "unsupported") { text = "⚠ máy chạy bản dev (không cài qua bộ cài Velopack) — không tự update được"; clear = true; }
        else if (s.StartsWith("failed", StringComparison.OrdinalIgnoreCase)) { text = "⚠ " + s; clear = true; }
        else { text = s; clear = false; }
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = clear
                ? "UPDATE machines SET update_status=$s, update_requested_at='', update_requested_from='' WHERE machine_id=$m"
                : "UPDATE machines SET update_status=$s WHERE machine_id=$m";
            c.Parameters.AddWithValue("$s", text);
            c.Parameters.AddWithValue("$m", machineId);
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
            c.CommandText = "SELECT machine_id,hostname,last_seen,app_version,max_brave,update_status,update_requested_at FROM machines";
            using var rd = c.ExecuteReader();
            while (rd.Read())
                list.Add(new MachinePresence { MachineId = S(rd, 0), Hostname = S(rd, 1), LastSeen = D(rd, 2), AppVersion = rd.IsDBNull(3) ? null : rd.GetString(3), MaxBrave = rd.IsDBNull(4) ? 0 : rd.GetInt32(4), UpdateStatus = S(rd, 5), UpdateRequestedAt = S(rd, 6) is { Length: > 0 } ua && DateTimeOffset.TryParse(ua, out var uat) ? uat : null });
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
