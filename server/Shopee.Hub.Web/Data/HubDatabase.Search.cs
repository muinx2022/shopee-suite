using Microsoft.Data.Sqlite;
using Shopee.Core.Coordination;

namespace Shopee.Hub;

/// <summary>Phần HubDatabase: kho gộp kết quả Search (client đẩy sản phẩm cào → Hub dedup theo item_id).</summary>
public sealed partial class HubDatabase
{
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
}
