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

    public Shop? GetShop(long id)
    {
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT id,name,username,password,cookie,proxy_key,note,created_at,updated_at FROM shops WHERE id=$id";
            c.Parameters.AddWithValue("$id", id);
            using var rd = c.ExecuteReader();
            return rd.Read() ? ReadShopRow(rd) : null;
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

    /// <summary>Xoá 1 shop + mọi đơn của nó. Trả true nếu có hàng bị xoá.</summary>
    public bool DeleteShop(long id)
    {
        lock (_gate)
        {
            using (var d = _conn.CreateCommand())
            {
                d.CommandText = "DELETE FROM orders WHERE shop_id=$id";
                d.Parameters.AddWithValue("$id", id);
                d.ExecuteNonQuery();
            }
            using var c = _conn.CreateCommand();
            c.CommandText = "DELETE FROM shops WHERE id=$id";
            c.Parameters.AddWithValue("$id", id);
            return c.ExecuteNonQuery() > 0;
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
