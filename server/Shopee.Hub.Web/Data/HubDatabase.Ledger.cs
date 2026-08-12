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

            // Sổ dòng BỎ QUA: UNION như Completed, KHÔNG thay thế. Client cũ (≤ v1.9.2) không gửi field này →
            // incoming.Skipped rỗng → sổ đã có trên Hub GIỮ NGUYÊN (nếu ghi đè thì mỗi lượt publish của một máy
            // client cũ là xoá trắng sổ của máy mới — "báo thành công khi thiếu" quay lại y như trước khi vá).
            var skipped = UnionRows(existing?.Skipped, incoming.Skipped);

            // Tích luỹ tập máy đã tham gia việc này (union machine gần nhất vào tập cũ) — cho Thống kê.
            var machines = existing?.MachineIds ?? new List<string>();
            if (!string.IsNullOrEmpty(incoming.LastMachineId) && !machines.Contains(incoming.LastMachineId))
                machines.Add(incoming.LastMachineId);

            using var c = _conn.CreateCommand();
            c.CommandText = @"
INSERT INTO ledger(key,bigseller_id,shop_id,sheet,op,completed_json,last_row,status,last_machine_id,last_hostname,last_run_at,updated_at,machines_json,skipped_json)
VALUES($k,$b,$s,$sh,$o,$cj,$lr,$st,$lm,$lh,$lra,$ua,$mj,$sj)
ON CONFLICT(key) DO UPDATE SET
  bigseller_id=$b, shop_id=$s, sheet=$sh, op=$o, completed_json=$cj, last_row=$lr,
  status=$st, last_machine_id=$lm, last_hostname=$lh, last_run_at=$lra, updated_at=$ua, machines_json=$mj,
  skipped_json=$sj;";
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
            c.Parameters.AddWithValue("$sj", JsonSerializer.Serialize(skipped));
            c.ExecuteNonQuery();
        }
    }

    /// <summary>Gộp 2 sổ dòng bỏ qua: dedup + sắp xếp, bỏ dòng ≤ 0. Bên nào rỗng/null thì trả bên kia (chính là
    /// chỗ giữ tương thích với client cũ không gửi <c>Skipped</c>).</summary>
    private static List<int> UnionRows(List<int>? a, List<int>? b)
    {
        var set = new SortedSet<int>();
        foreach (var r in a ?? []) if (r > 0) set.Add(r);
        foreach (var r in b ?? []) if (r > 0) set.Add(r);
        return [.. set];
    }

    /// <summary>MỞ LẠI các dòng đã bỏ qua của 1 việc: bỏ chúng khỏi vùng phủ <c>completed</c>, xoá sổ, đưa
    /// trạng thái về <see cref="LedgerStatus.Stopped"/> (còn dòng chưa cào → không được để "✔ xong").
    /// <para><paramref name="clientRows"/> = sổ của CLIENT, union với sổ trên hub trước khi khoét: dòng bỏ
    /// TRƯỚC khi có cột skipped (không backfill) chỉ còn dấu ở client — thiếu union này thì hub trả OK mà
    /// không khoét gì → client xoá sổ local xong lượt fold phủ lại, mất dấu vĩnh viễn.</para>
    /// Trả về SỐ dòng vừa mở lại; 0 = không có bản ghi hoặc không có dòng nào để mở (KHÔNG ném — client bấm
    /// nút khi Hub chưa có bản ghi nào là chuyện bình thường, phải coi là thành công để nó còn sửa tiếp local:
    /// hub không có bản ghi thì cũng không có vùng phủ nào fold về đè được).</summary>
    public int ReopenSkippedLedger(string key, List<int>? clientRows = null)
    {
        if (string.IsNullOrWhiteSpace(key)) return 0;
        lock (_gate)
        {
            var existing = ReadLedgerLocked(key);
            if (existing is null) return 0;
            var rows = UnionRows(existing.Skipped, clientRows);
            if (rows.Count == 0) return 0;
            var completed = RowRangeMath.SubtractRows(existing.Completed, rows);

            using var c = _conn.CreateCommand();
            c.CommandText = @"
UPDATE ledger SET completed_json=$cj, skipped_json='[]', status=$st, updated_at=$ua WHERE key=$k;";
            c.Parameters.AddWithValue("$k", key);
            c.Parameters.AddWithValue("$cj", JsonSerializer.Serialize(completed));
            c.Parameters.AddWithValue("$st", LedgerStatus.Stopped);
            c.Parameters.AddWithValue("$ua", Iso(DateTimeOffset.UtcNow));
            c.ExecuteNonQuery();
            return rows.Count;
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
            if (string.IsNullOrWhiteSpace(status) || string.Equals(status, LedgerStatus.Idle, StringComparison.OrdinalIgnoreCase))
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
INSERT INTO ledger(key,bigseller_id,shop_id,sheet,op,completed_json,last_row,status,last_machine_id,last_hostname,last_run_at,updated_at,skipped_json)
VALUES($k,$b,$s,$sh,$o,$cj,$lr,$st,'','',$ua,$ua,$sj)
ON CONFLICT(key) DO UPDATE SET status=$st, updated_at=$ua;";
            c.Parameters.AddWithValue("$k", key);
            c.Parameters.AddWithValue("$b", bigsellerId);
            c.Parameters.AddWithValue("$s", shopId);
            c.Parameters.AddWithValue("$sh", sheet);
            c.Parameters.AddWithValue("$o", op);
            c.Parameters.AddWithValue("$cj", existing is null ? "[]" : JsonSerializer.Serialize(existing.Completed));
            // Nhánh INSERT (chưa có bản ghi) chỉ có thể là sổ rỗng; nhánh UPDATE không đụng skipped_json nên sổ
            // đang có GIỮ NGUYÊN — đặt tay completed/stopped KHÔNG được làm mất dấu các dòng đang thiếu.
            c.Parameters.AddWithValue("$sj", existing is null ? "[]" : JsonSerializer.Serialize(existing.Skipped));
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
            c.CommandText = "SELECT key,bigseller_id,shop_id,sheet,op,completed_json,last_row,status,last_machine_id,last_hostname,last_run_at,updated_at,machines_json,skipped_json FROM ledger";
            using var rd = c.ExecuteReader();
            while (rd.Read()) list.Add(ReadLedgerRow(rd));
            return list;
        }
    }

    private WorkLedgerRecord? ReadLedgerLocked(string key)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "SELECT key,bigseller_id,shop_id,sheet,op,completed_json,last_row,status,last_machine_id,last_hostname,last_run_at,updated_at,machines_json,skipped_json FROM ledger WHERE key=$k";
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
        var skipped = new List<int>();
        // skipped_json là cột mới (index 13) — như machines_json, DB cũ chưa migrate có thể thiếu → thủ FieldCount.
        // Chuỗi rỗng (bản ghi có TRƯỚC migration, không backfill) → sổ rỗng, KHÔNG ném.
        if (rd.FieldCount > 13)
        {
            var sj = S(rd, 13);
            if (!string.IsNullOrWhiteSpace(sj))
            {
                try { skipped = JsonSerializer.Deserialize<List<int>>(sj) ?? new(); } catch { }
            }
        }
        return new WorkLedgerRecord
        {
            Key = S(rd, 0), BigsellerId = S(rd, 1), ShopId = S(rd, 2), Sheet = S(rd, 3), Op = S(rd, 4),
            Completed = completed, LastRowReached = rd.IsDBNull(6) ? 0 : rd.GetInt32(6),
            Status = S(rd, 7), LastMachineId = S(rd, 8), LastHostname = S(rd, 9),
            MachineIds = machines, Skipped = skipped,
            LastRunAt = rd.IsDBNull(10) ? null : D(rd, 10), UpdatedAt = D(rd, 11),
        };
    }
}
