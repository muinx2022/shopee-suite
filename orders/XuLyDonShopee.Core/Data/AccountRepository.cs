using Microsoft.Data.Sqlite;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Core.Data;

/// <summary>
/// CRUD cho tài khoản Shopee trong bảng <c>accounts</c>.
/// </summary>
public class AccountRepository
{
    private readonly Database _db;

    public AccountRepository(Database db) => _db = db;

    public List<Account> GetAll()
    {
        var list = new List<Account>();
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT Id, Email, Password, Phone, Cookie, Note, ProxyKey, PickupAddress, Status, CreatedAt, UpdatedAt
                            FROM accounts ORDER BY Id;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(Map(reader));
        }
        return list;
    }

    public Account? GetById(long id)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT Id, Email, Password, Phone, Cookie, Note, ProxyKey, PickupAddress, Status, CreatedAt, UpdatedAt
                            FROM accounts WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    /// <summary>Thêm mới. Gán CreatedAt/UpdatedAt = giờ hiện tại (UTC), cập nhật Id vào object và trả về Id.</summary>
    public long Insert(Account account)
    {
        var now = DateTime.UtcNow;
        account.CreatedAt = now;
        account.UpdatedAt = now;

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO accounts (Email, Password, Phone, Cookie, Note, ProxyKey, PickupAddress, Status, CreatedAt, UpdatedAt)
                            VALUES ($email, $password, $phone, $cookie, $note, $proxyKey, $pickupAddress, $status, $createdAt, $updatedAt);
                            SELECT last_insert_rowid();";
        BindWritableFields(cmd, account);
        cmd.Parameters.AddWithValue("$createdAt", DbSerialization.FormatDate(account.CreatedAt));
        cmd.Parameters.AddWithValue("$updatedAt", DbSerialization.FormatDate(account.UpdatedAt));

        var id = (long)cmd.ExecuteScalar()!;
        account.Id = id;
        return id;
    }

    /// <summary>Cập nhật. Tự đặt UpdatedAt = giờ hiện tại (UTC).</summary>
    public void Update(Account account)
    {
        account.UpdatedAt = DateTime.UtcNow;

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE accounts
                            SET Email = $email, Password = $password, Phone = $phone, Cookie = $cookie,
                                Note = $note, ProxyKey = $proxyKey, PickupAddress = $pickupAddress,
                                Status = $status, UpdatedAt = $updatedAt
                            WHERE Id = $id;";
        BindWritableFields(cmd, account);
        cmd.Parameters.AddWithValue("$updatedAt", DbSerialization.FormatDate(account.UpdatedAt));
        cmd.Parameters.AddWithValue("$id", account.Id);
        cmd.ExecuteNonQuery();
    }

    public void Delete(long id)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM accounts WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private static void BindWritableFields(SqliteCommand cmd, Account a)
    {
        cmd.Parameters.AddWithValue("$email", a.Email);
        cmd.Parameters.AddWithValue("$password", a.Password);
        cmd.Parameters.AddWithValue("$phone", (object?)a.Phone ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cookie", (object?)a.Cookie ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$note", (object?)a.Note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$proxyKey", (object?)a.ProxyKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pickupAddress", (object?)a.PickupAddress ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", a.Status.ToString());
    }

    private static Account Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Email = r.GetString(1),
        Password = r.GetString(2),
        Phone = r.IsDBNull(3) ? null : r.GetString(3),
        Cookie = r.IsDBNull(4) ? null : r.GetString(4),
        Note = r.IsDBNull(5) ? null : r.GetString(5),
        ProxyKey = r.IsDBNull(6) ? null : r.GetString(6),
        PickupAddress = r.IsDBNull(7) ? null : r.GetString(7),
        Status = DbSerialization.ParseEnum<AccountStatus>(r.GetString(8)),
        CreatedAt = DbSerialization.ParseDate(r.GetString(9)),
        UpdatedAt = DbSerialization.ParseDate(r.GetString(10))
    };
}
