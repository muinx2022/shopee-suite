using Microsoft.Data.Sqlite;
using Shopee.Core.Coordination;
using Shopee.Core.Scrape;

namespace Shopee.Hub;

/// <summary>Phần HubDatabase: sổ hoàn thành (ledger) — gộp khoảng dòng đã xong phía server + đặt tay trạng thái.</summary>
public sealed partial class HubDatabase
{
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

            // Tích luỹ tập máy đã tham gia việc này (union machine gần nhất vào tập cũ) — cho Thống kê.
            var machines = existing?.MachineIds ?? new List<string>();
            if (!string.IsNullOrEmpty(incoming.LastMachineId) && !machines.Contains(incoming.LastMachineId))
                machines.Add(incoming.LastMachineId);

            using var c = _conn.CreateCommand();
            c.CommandText = @"
INSERT INTO ledger(key,bigseller_id,shop_id,sheet,op,completed_json,last_row,status,last_machine_id,last_hostname,last_run_at,updated_at,machines_json)
VALUES($k,$b,$s,$sh,$o,$cj,$lr,$st,$lm,$lh,$lra,$ua,$mj)
ON CONFLICT(key) DO UPDATE SET
  bigseller_id=$b, shop_id=$s, sheet=$sh, op=$o, completed_json=$cj, last_row=$lr,
  status=$st, last_machine_id=$lm, last_hostname=$lh, last_run_at=$lra, updated_at=$ua, machines_json=$mj;";
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
            c.Parameters.AddWithValue("$mj", JsonSerializer.Serialize(machines));
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
            c.CommandText = "SELECT key,bigseller_id,shop_id,sheet,op,completed_json,last_row,status,last_machine_id,last_hostname,last_run_at,updated_at,machines_json FROM ledger";
            using var rd = c.ExecuteReader();
            while (rd.Read()) list.Add(ReadLedgerRow(rd));
            return list;
        }
    }

    private WorkLedgerRecord? ReadLedgerLocked(string key)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "SELECT key,bigseller_id,shop_id,sheet,op,completed_json,last_row,status,last_machine_id,last_hostname,last_run_at,updated_at,machines_json FROM ledger WHERE key=$k";
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
        var machines = new List<string>();
        // machines_json là cột mới (index 12) — DB cũ chưa migrate có thể thiếu → thủ FieldCount.
        if (rd.FieldCount > 12)
        {
            var mj = S(rd, 12);
            if (!string.IsNullOrWhiteSpace(mj))
            {
                try { machines = JsonSerializer.Deserialize<List<string>>(mj) ?? new(); } catch { }
            }
        }
        return new WorkLedgerRecord
        {
            Key = S(rd, 0), BigsellerId = S(rd, 1), ShopId = S(rd, 2), Sheet = S(rd, 3), Op = S(rd, 4),
            Completed = completed, LastRowReached = rd.IsDBNull(6) ? 0 : rd.GetInt32(6),
            Status = S(rd, 7), LastMachineId = S(rd, 8), LastHostname = S(rd, 9),
            MachineIds = machines,
            LastRunAt = rd.IsDBNull(10) ? null : D(rd, 10), UpdatedAt = D(rd, 11),
        };
    }
}
