using Microsoft.Data.Sqlite;

namespace Shopee.Hub;

/// <summary>Một shop Shopee do hub theo dõi (username = khóa tự đăng ký khi client push đơn). Credentials
/// KHÔNG bắt buộc — trang Shops ở đây là DANH BẠ shop tự đăng ký (sửa ghi chú / xóa), không phải form nhập
/// đủ trường như bản fork.</summary>
public sealed class Shop
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? Cookie { get; init; }
    public string? ProxyKey { get; init; }
    public string? Note { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Một dòng shop trên trang danh bạ nhóm theo subacc. <see cref="Hub"/> null = chỉ có trong gương
/// client (chưa từng push đơn → chưa có hàng <c>shops</c>) → UI không cho Sửa/Xoá.</summary>
public sealed record ShopListItem(string ShopLogin, string DisplayName, Shop? Hub);

/// <summary>Nhóm shop theo subacc (<see cref="SubLogin"/>) hoặc nhóm mồ côi (<see cref="IsOrphan"/> =
/// shop hub không xuất hiện trong gương <c>orders_account_shops</c>).</summary>
public sealed record ShopSubAccountGroup(string SubLogin, bool IsOrphan, List<ShopListItem> Items);

/// <summary>Phần HubDatabase: nghiệp vụ SHOP — bảng <c>shops</c> (UNIQUE username) + CRUD +
/// <see cref="GetOrCreateShopByUsername"/> (hub tự đăng ký shop khi client push). Id=0 khi Upsert = thêm mới.</summary>
public sealed partial class HubDatabase
{
    private void EnsureShopsSchema() => ExecRaw(@"
CREATE TABLE IF NOT EXISTS shops(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  name TEXT NOT NULL DEFAULT '', username TEXT, password TEXT, cookie TEXT,
  proxy_key TEXT, note TEXT, created_at TEXT, updated_at TEXT);
CREATE UNIQUE INDEX IF NOT EXISTS ux_shops_username ON shops(username);");

    /// <summary>Khoá "đã chạy" của <see cref="MergeDuplicateShopsOnce"/> trong bảng <c>settings</c>.</summary>
    private const string MergeShopsKey = "merge_shops_nocase_v1";

    /// <summary>
    /// GỘP các hàng <c>shops</c> trùng username sau khi bỏ hoa/thường rồi dựng lại
    /// <c>ux_shops_username</c> thành <c>COLLATE NOCASE</c> — chạy MỘT LẦN trên DB đang có dữ liệu.
    /// <para>
    /// Vì sao có trùng: fallback <c>ResolveShopUsername</c> của client lấy tạm email subaccount rồi lần sau đổi
    /// sang shop_login thật, và hai máy gõ email lệch HOA/thường. Hai hàng shop cho cùng một shop vật lý làm
    /// khoá chống trùng đơn <c>(shop_id, order_sn)</c> mất tác dụng → cùng một đơn được đếm 2 lần.
    /// </para>
    /// Cách gộp: giữ hàng CŨ NHẤT (id nhỏ nhất) của mỗi nhóm; chuyển đơn của các hàng thừa về hàng giữ lại —
    /// đơn trùng <c>order_sn</c> ở cả hai bên thì giữ bản có <c>synced_at</c> MỚI NHẤT; dời luôn thư mục phiếu
    /// <c>slips/&lt;shopId&gt;</c>; xoá hàng shop thừa.
    /// <para><b>Idempotent:</b> chốt bằng khoá trong <c>settings</c> + bản thân phép gộp chạy lần 2 cũng không
    /// còn nhóm trùng nào (chạy 2 lần không đổi thêm gì).</para>
    /// </summary>
    private void MergeDuplicateShopsOnce(string dataDir)
    {
        lock (_gate)
        {
            if (GetSetting(MergeShopsKey) is not null) return;

            // (id GIỮ LẠI, id BỎ) của mọi hàng thừa — nhóm theo username đã bỏ hoa/thường + trim.
            var pairs = new List<(long Keep, long Drop)>();
            using (var c = _conn.CreateCommand())
            {
                c.CommandText = @"
SELECT MIN(id), GROUP_CONCAT(id) FROM shops
WHERE username IS NOT NULL AND TRIM(username) <> ''
GROUP BY LOWER(TRIM(username)) HAVING COUNT(*) > 1";
                using var rd = c.ExecuteReader();
                while (rd.Read())
                {
                    var keep = rd.GetInt64(0);
                    foreach (var raw in S(rd, 1).Split(',', StringSplitOptions.RemoveEmptyEntries))
                        if (long.TryParse(raw, out var id) && id != keep) pairs.Add((keep, id));
                }
            }

            if (pairs.Count > 0)
            {
                using var tx = _conn.BeginTransaction();
                foreach (var (keep, drop) in pairs)
                {
                    // Đơn trùng order_sn ở CẢ hai shop: bỏ bản CŨ hơn (so synced_at dạng ISO = so chuỗi) rồi mới
                    // chuyển phần còn lại sang — làm ngược thứ tự là vi phạm UNIQUE(shop_id, order_sn).
                    using (var d = _conn.CreateCommand())
                    {
                        d.Transaction = tx;
                        d.CommandText = @"
DELETE FROM orders WHERE shop_id=$drop AND order_sn IN (
  SELECT o2.order_sn FROM orders o2 WHERE o2.shop_id=$keep
    AND COALESCE(o2.synced_at,'') >= COALESCE((SELECT o3.synced_at FROM orders o3
        WHERE o3.shop_id=$drop AND o3.order_sn=o2.order_sn), ''));
DELETE FROM orders WHERE shop_id=$keep AND order_sn IN (
  SELECT o2.order_sn FROM orders o2 WHERE o2.shop_id=$drop);";
                        d.Parameters.AddWithValue("$keep", keep);
                        d.Parameters.AddWithValue("$drop", drop);
                        d.ExecuteNonQuery();
                    }
                    using (var m = _conn.CreateCommand())
                    {
                        m.Transaction = tx;
                        m.CommandText = "UPDATE orders SET shop_id=$keep WHERE shop_id=$drop; DELETE FROM shops WHERE id=$drop;";
                        m.Parameters.AddWithValue("$keep", keep);
                        m.Parameters.AddWithValue("$drop", drop);
                        m.ExecuteNonQuery();
                    }
                }
                tx.Commit();

                // Phiếu PDF trên đĩa keyed theo shopId → dời sang thư mục của shop giữ lại (file trùng tên: giữ
                // bản đích, đơn nào cũng chỉ có một phiếu). Lỗi I/O KHÔNG được làm sập lượt khởi động hub.
                foreach (var (keep, drop) in pairs) MoveSlipDir(dataDir, drop, keep);
            }

            ExecRaw("DROP INDEX IF EXISTS ux_shops_username;");
            ExecRaw("CREATE UNIQUE INDEX IF NOT EXISTS ux_shops_username ON shops(username COLLATE NOCASE);");
            SetSetting(MergeShopsKey, DateTimeOffset.UtcNow.ToString("o"));
        }
    }

    /// <summary>Dời <c>slips/&lt;from&gt;</c> vào <c>slips/&lt;to&gt;</c> (file trùng tên → giữ bản đích). Nuốt lỗi
    /// I/O: phiếu là dữ liệu phụ, không đáng để hub không khởi động được.</summary>
    private static void MoveSlipDir(string dataDir, long from, long to)
    {
        try
        {
            var src = Path.Combine(dataDir, "slips", from.ToString());
            if (!Directory.Exists(src)) return;
            var dst = Path.Combine(dataDir, "slips", to.ToString());
            Directory.CreateDirectory(dst);
            foreach (var file in Directory.EnumerateFiles(src))
            {
                var target = Path.Combine(dst, Path.GetFileName(file));
                if (File.Exists(target)) File.Delete(file);
                else File.Move(file, target);
            }
            try { Directory.Delete(src, recursive: false); } catch { }
        }
        catch { /* phiếu là dữ liệu phụ — không chặn khởi động hub */ }
    }

    public List<Shop> ListShops()
    {
        lock (_gate)
        {
            var list = new List<Shop>();
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT id,name,username,password,cookie,proxy_key,note,created_at,updated_at FROM shops ORDER BY name COLLATE NOCASE, id";
            using var rd = c.ExecuteReader();
            while (rd.Read()) list.Add(ReadShopRow(rd));
            return list;
        }
    }

    /// <summary>
    /// Danh bạ shop nhóm theo subacc lấy từ gương <c>orders_account_shops</c> (gộp mọi máy; distinct
    /// <c>login</c>+<c>shop_login</c>). Join <c>shops.username</c> để có bản ghi hub (note/id/đơn). Shop hub
    /// không có trong gương → nhóm cuối <see cref="ShopSubAccountGroup.IsOrphan"/>.
    /// </summary>
    public List<ShopSubAccountGroup> ListShopGroupsBySubAccount()
    {
        lock (_gate)
        {
            var hubByLogin = new Dictionary<string, Shop>(StringComparer.OrdinalIgnoreCase);
            using (var c = _conn.CreateCommand())
            {
                c.CommandText = "SELECT id,name,username,password,cookie,proxy_key,note,created_at,updated_at FROM shops";
                using var rd = c.ExecuteReader();
                while (rd.Read())
                {
                    var shop = ReadShopRow(rd);
                    var key = shop.Username?.Trim() ?? "";
                    if (key.Length == 0) continue;
                    hubByLogin[key] = shop;
                }
            }

            // login → (shop_login → display name gần nhất)
            var grouped = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            using (var c = _conn.CreateCommand())
            {
                c.CommandText = @"
SELECT login, shop_login, shop_name FROM orders_account_shops
ORDER BY login COLLATE NOCASE, sort_order, shop_login COLLATE NOCASE";
                using var rd = c.ExecuteReader();
                while (rd.Read())
                {
                    var sub = S(rd, 0).Trim();
                    var shopLogin = S(rd, 1).Trim();
                    if (sub.Length == 0 || shopLogin.Length == 0) continue;
                    var name = S(rd, 2).Trim();
                    if (!grouped.TryGetValue(sub, out var map))
                        grouped[sub] = map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (!map.ContainsKey(shopLogin) || (name.Length > 0 && map[shopLogin].Length == 0))
                        map[shopLogin] = name.Length > 0 ? name : shopLogin;
                }
            }

            var seenShopLogins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<ShopSubAccountGroup>();
            foreach (var sub in grouped.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var items = new List<ShopListItem>();
                foreach (var (shopLogin, display) in grouped[sub]
                             .OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                {
                    seenShopLogins.Add(shopLogin);
                    hubByLogin.TryGetValue(shopLogin, out var hub);
                    var title = hub is { Name.Length: > 0 } ? hub.Name
                        : display.Length > 0 ? display : shopLogin;
                    items.Add(new ShopListItem(shopLogin, title, hub));
                }
                if (items.Count > 0)
                    result.Add(new ShopSubAccountGroup(sub, IsOrphan: false, items));
            }

            var orphans = new List<ShopListItem>();
            foreach (var shop in hubByLogin.Values
                         .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(s => s.Id))
            {
                var key = shop.Username?.Trim() ?? "";
                if (key.Length == 0 || seenShopLogins.Contains(key)) continue;
                orphans.Add(new ShopListItem(key, string.IsNullOrWhiteSpace(shop.Name) ? key : shop.Name, shop));
            }
            if (orphans.Count > 0)
                result.Add(new ShopSubAccountGroup("", IsOrphan: true, orphans));

            return result;
        }
    }

    /// <summary>Tìm shop theo username; chưa có → TẠO shop mới (name = <paramref name="name"/> hoặc chính
    /// username nếu trống). Trả id shop. Dùng ở đường push đơn để client khỏi biết id trên hub.
    /// <para>So khớp KHÔNG phân biệt hoa/thường (+ Trim) — cùng luật với unique index
    /// <c>ux_shops_username</c>: "ShopA" và "shopa" là MỘT shop vật lý. Khớp phân biệt hoa/thường sẽ đẻ 2 hàng
    /// <c>shops</c>, mà khoá chống trùng đơn là <c>(shop_id, order_sn)</c> → CÙNG một đơn bị đếm 2 lần trong
    /// thống kê, còn lọc theo shop thì trả 0 một cách LẶNG. Chữ hoa/thường LƯU vẫn giữ nguyên bản đầu tiên
    /// (chỉ để hiển thị).</para></summary>
    public long GetOrCreateShopByUsername(string username, string? name)
    {
        lock (_gate)
        {
            var u = username?.Trim() ?? "";
            using (var q = _conn.CreateCommand())
            {
                q.CommandText = "SELECT id FROM shops WHERE username=$u COLLATE NOCASE";
                q.Parameters.AddWithValue("$u", u);
                var found = q.ExecuteScalar();
                if (found is not null && found is not DBNull) return Convert.ToInt64(found);
            }

            var now = Iso(DateTimeOffset.UtcNow);
            var shopName = string.IsNullOrWhiteSpace(name) ? u : name.Trim();
            using (var c = _conn.CreateCommand())
            {
                c.CommandText = @"
INSERT INTO shops(name,username,created_at,updated_at) VALUES($n,$u,$ca,$ua);";
                c.Parameters.AddWithValue("$n", shopName);
                c.Parameters.AddWithValue("$u", u);
                c.Parameters.AddWithValue("$ca", now);
                c.Parameters.AddWithValue("$ua", now);
                c.ExecuteNonQuery();
            }
            using var idc = _conn.CreateCommand();
            idc.CommandText = "SELECT last_insert_rowid();";
            return Convert.ToInt64(idc.ExecuteScalar());
        }
    }

    /// <summary>Thêm (id&lt;=0) hoặc sửa (id&gt;0) 1 shop. Trả id sau khi ghi (id mới khi thêm). Thất bại (không có
    /// hàng nào khớp khi sửa) → trả 0.</summary>
    public long UpsertShop(Shop s)
    {
        lock (_gate)
        {
            var now = Iso(DateTimeOffset.UtcNow);
            if (s.Id <= 0)
            {
                using (var c = _conn.CreateCommand())
                {
                    c.CommandText = @"
INSERT INTO shops(name,username,password,cookie,proxy_key,note,created_at,updated_at)
VALUES($n,$u,$p,$ck,$pk,$note,$ca,$ua);";
                    Bind(c, s);
                    c.Parameters.AddWithValue("$ca", now);
                    c.Parameters.AddWithValue("$ua", now);
                    c.ExecuteNonQuery();
                }
                using var idc = _conn.CreateCommand();
                idc.CommandText = "SELECT last_insert_rowid();";
                return Convert.ToInt64(idc.ExecuteScalar());
            }
            using (var c = _conn.CreateCommand())
            {
                c.CommandText = @"
UPDATE shops SET name=$n, username=$u, password=$p, cookie=$ck, proxy_key=$pk, note=$note, updated_at=$ua
WHERE id=$id;";
                Bind(c, s);
                c.Parameters.AddWithValue("$ua", now);
                c.Parameters.AddWithValue("$id", s.Id);
                return c.ExecuteNonQuery() > 0 ? s.Id : 0;
            }
        }
    }

    /// <summary>Xoá 1 shop + mọi đơn của nó. Trả true nếu có hàng bị xoá. Bọc transaction: crash giữa 2 lệnh
    /// (xoá orders → xoá shops) KHÔNG để lại shop mồ côi đã mất đơn (hoặc ngược lại).</summary>
    public bool DeleteShop(long id)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            using (var d = _conn.CreateCommand())
            {
                d.Transaction = tx;
                d.CommandText = "DELETE FROM orders WHERE shop_id=$id";
                d.Parameters.AddWithValue("$id", id);
                d.ExecuteNonQuery();
            }
            bool removed;
            using (var c = _conn.CreateCommand())
            {
                c.Transaction = tx;
                c.CommandText = "DELETE FROM shops WHERE id=$id";
                c.Parameters.AddWithValue("$id", id);
                removed = c.ExecuteNonQuery() > 0;
            }
            tx.Commit();
            return removed;
        }
    }

    private static void Bind(SqliteCommand c, Shop s)
    {
        c.Parameters.AddWithValue("$n", s.Name ?? "");
        c.Parameters.AddWithValue("$u", (object?)s.Username ?? DBNull.Value);
        c.Parameters.AddWithValue("$p", (object?)s.Password ?? DBNull.Value);
        c.Parameters.AddWithValue("$ck", (object?)s.Cookie ?? DBNull.Value);
        c.Parameters.AddWithValue("$pk", (object?)s.ProxyKey ?? DBNull.Value);
        c.Parameters.AddWithValue("$note", (object?)s.Note ?? DBNull.Value);
    }

    private static Shop ReadShopRow(SqliteDataReader rd) => new()
    {
        Id = rd.GetInt64(0),
        Name = S(rd, 1),
        Username = rd.IsDBNull(2) ? null : rd.GetString(2),
        Password = rd.IsDBNull(3) ? null : rd.GetString(3),
        Cookie = rd.IsDBNull(4) ? null : rd.GetString(4),
        ProxyKey = rd.IsDBNull(5) ? null : rd.GetString(5),
        Note = rd.IsDBNull(6) ? null : rd.GetString(6),
        CreatedAt = D(rd, 7),
        UpdatedAt = D(rd, 8),
    };
}
