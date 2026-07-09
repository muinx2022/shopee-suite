using Shopee.Core.Coordination;

namespace Shopee.Hub;

/// <summary>Phần HubDatabase: log tập trung nhiều máy + client báo acc Shopee lỗi/captcha (Hub xem, quyết giữ/xoá).</summary>
public sealed partial class HubDatabase
{
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
}
