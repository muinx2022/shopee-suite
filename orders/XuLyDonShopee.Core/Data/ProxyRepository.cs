using Microsoft.Data.Sqlite;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Core.Data;

/// <summary>
/// CRUD cho proxy trong bảng <c>proxies</c>.
/// </summary>
public class ProxyRepository
{
    private readonly Database _db;

    public ProxyRepository(Database db) => _db = db;

    public List<ProxyEntry> GetAll()
    {
        var list = new List<ProxyEntry>();
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT Id, Host, Port, Username, Password, Type, Status, CreatedAt
                            FROM proxies ORDER BY Id;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(Map(reader));
        }
        return list;
    }

    public ProxyEntry? GetById(long id)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT Id, Host, Port, Username, Password, Type, Status, CreatedAt
                            FROM proxies WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    /// <summary>Thêm một proxy. Gán CreatedAt nếu chưa có, cập nhật Id và trả về Id.</summary>
    public long Insert(ProxyEntry proxy)
    {
        using var conn = _db.OpenConnection();
        return InsertInternal(conn, proxy);
    }

    /// <summary>Thêm nhiều proxy trong một transaction. Trả về số dòng đã thêm.</summary>
    public int InsertMany(IEnumerable<ProxyEntry> proxies)
    {
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();
        var count = 0;
        foreach (var p in proxies)
        {
            InsertInternal(conn, p, tx);
            count++;
        }
        tx.Commit();
        return count;
    }

    public void Update(ProxyEntry proxy)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE proxies
                            SET Host = $host, Port = $port, Username = $username, Password = $password,
                                Type = $type, Status = $status
                            WHERE Id = $id;";
        BindWritableFields(cmd, proxy);
        cmd.Parameters.AddWithValue("$id", proxy.Id);
        cmd.ExecuteNonQuery();
    }

    public void Delete(long id)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM proxies WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Xóa toàn bộ proxy.</summary>
    public void DeleteAll()
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM proxies;";
        cmd.ExecuteNonQuery();
    }

    private static long InsertInternal(SqliteConnection conn, ProxyEntry proxy, SqliteTransaction? tx = null)
    {
        if (proxy.CreatedAt == default)
        {
            proxy.CreatedAt = DateTime.UtcNow;
        }

        using var cmd = conn.CreateCommand();
        if (tx != null)
        {
            cmd.Transaction = tx;
        }
        cmd.CommandText = @"INSERT INTO proxies (Host, Port, Username, Password, Type, Status, CreatedAt)
                            VALUES ($host, $port, $username, $password, $type, $status, $createdAt);
                            SELECT last_insert_rowid();";
        BindWritableFields(cmd, proxy);
        cmd.Parameters.AddWithValue("$createdAt", DbSerialization.FormatDate(proxy.CreatedAt));

        var id = (long)cmd.ExecuteScalar()!;
        proxy.Id = id;
        return id;
    }

    private static void BindWritableFields(SqliteCommand cmd, ProxyEntry p)
    {
        cmd.Parameters.AddWithValue("$host", p.Host);
        cmd.Parameters.AddWithValue("$port", p.Port);
        cmd.Parameters.AddWithValue("$username", (object?)p.Username ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$password", (object?)p.Password ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$type", p.Type.ToString());
        cmd.Parameters.AddWithValue("$status", p.Status.ToString());
    }

    private static ProxyEntry Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Host = r.GetString(1),
        Port = r.GetInt32(2),
        Username = r.IsDBNull(3) ? null : r.GetString(3),
        Password = r.IsDBNull(4) ? null : r.GetString(4),
        Type = DbSerialization.ParseEnum<ProxyType>(r.GetString(5)),
        Status = DbSerialization.ParseEnum<ProxyStatus>(r.GetString(6)),
        CreatedAt = DbSerialization.ParseDate(r.GetString(7))
    };
}
