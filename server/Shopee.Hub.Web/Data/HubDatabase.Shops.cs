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
    /// username nếu trống). Trả id shop. Dùng ở đường push đơn để client khỏi biết id trên hub.</summary>
    public long GetOrCreateShopByUsername(string username, string? name)
    {
        lock (_gate)
        {
            using (var q = _conn.CreateCommand())
            {
                q.CommandText = "SELECT id FROM shops WHERE username=$u";
                q.Parameters.AddWithValue("$u", username);
                var found = q.ExecuteScalar();
                if (found is not null && found is not DBNull) return Convert.ToInt64(found);
            }

            var now = Iso(DateTimeOffset.UtcNow);
            var shopName = string.IsNullOrWhiteSpace(name) ? username : name.Trim();
            using (var c = _conn.CreateCommand())
            {
                c.CommandText = @"
INSERT INTO shops(name,username,created_at,updated_at) VALUES($n,$u,$ca,$ua);";
                c.Parameters.AddWithValue("$n", shopName);
                c.Parameters.AddWithValue("$u", username);
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
