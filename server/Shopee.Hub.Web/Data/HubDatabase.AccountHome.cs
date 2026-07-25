using Shopee.Core.Coordination;

namespace Shopee.Hub;

/// <summary>Phần HubDatabase: "nhà" (home machine) của mỗi tk Shopee — affinity tk↔máy cho Scrape. KHÁC
/// <c>account_leases</c> (khoá loại-trừ TỨC THỜI, xoá khi nhả): <c>account_home</c> BỀN, KHÔNG xoá khi nhả
/// lease → nhớ "tk từng dựng profile trusted ở máy nào" qua các lượt chạy rời. Ràng buộc (Binding) dựa vào
/// nhịp sống của MÁY nhà (<c>machines.last_seen</c>) so với <see cref="HomeTakeoverAfter"/>: máy nhà còn
/// online ⇒ máy khác tránh; offline quá ngưỡng ⇒ tk tự do, máy khác tiếp quản + re-home. Là lớp CỐ VẤN chọn
/// khung; khoá loại trừ thật vẫn là account-lease xuyên máy (<see cref="ReserveAccounts"/>).</summary>
public sealed partial class HubDatabase
{
    /// <summary>Ghi "nhà" = máy này cho từng tk (Distinct, Ordinal). Chỉ gọi cho tk đã được LEASE cấp cho máy
    /// này ⇒ upsert an toàn: tk mồ côi → thành của mình; tk nhà-offline → tiếp quản; tk của mình → refresh.</summary>
    public void SetAccountHome(SetAccountHomeRequest r)
    {
        lock (_gate)
        {
            var now = Iso(DateTimeOffset.UtcNow);
            foreach (var id in r.AccountIds.Distinct(StringComparer.Ordinal))
            {
                using var c = _conn.CreateCommand();
                c.CommandText = @"
INSERT INTO account_home(account_id,machine_id,hostname,updated_at)
VALUES($id,$m,$h,$t)
ON CONFLICT(account_id) DO UPDATE SET machine_id=$m, hostname=$h, updated_at=$t;";
                c.Parameters.AddWithValue("$id", id);
                c.Parameters.AddWithValue("$m", r.MachineId);
                c.Parameters.AddWithValue("$h", r.Hostname);
                c.Parameters.AddWithValue("$t", now);
                c.ExecuteNonQuery();
            }
        }
    }

    /// <summary>Toàn bộ "nhà" tk + cờ <see cref="AccountHomeItem.Binding"/> (máy nhà CÒN online theo
    /// <c>last_seen</c> vs <see cref="HomeTakeoverAfter"/>). Client dùng để dựng khung: tk của MÌNH ưu tiên
    /// (tái dùng profile trusted), tk máy KHÁC còn binding thì tránh (nhường).</summary>
    public List<AccountHomeItem> AccountHomes()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            // Nhịp sống MÁY nhà (không cần heartbeat per-tk): last_seen của từng máy vào dict để tra nhanh.
            var seen = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            using (var c = _conn.CreateCommand())
            {
                c.CommandText = "SELECT machine_id,last_seen FROM machines";
                using var rd = c.ExecuteReader();
                while (rd.Read()) seen[S(rd, 0)] = D(rd, 1);
            }
            var list = new List<AccountHomeItem>();
            using (var c = _conn.CreateCommand())
            {
                c.CommandText = "SELECT account_id,machine_id,hostname FROM account_home";
                using var rd = c.ExecuteReader();
                while (rd.Read())
                {
                    var mid = S(rd, 1);
                    var binding = seen.TryGetValue(mid, out var ls) && (now - ls) < HomeTakeoverAfter;
                    list.Add(new AccountHomeItem(S(rd, 0), mid, S(rd, 2), binding));
                }
            }
            return list;
        }
    }
}
