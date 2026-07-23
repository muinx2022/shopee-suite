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
        cmd.CommandText = @"SELECT Id, Email, Password, Phone, Cookie, Note, ProxyKey, PickupAddress, VerifyEmail, VerifyEmailPassword, Status, CreatedAt, UpdatedAt, verify_failed_at
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
        cmd.CommandText = @"SELECT Id, Email, Password, Phone, Cookie, Note, ProxyKey, PickupAddress, VerifyEmail, VerifyEmailPassword, Status, CreatedAt, UpdatedAt, verify_failed_at
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
        cmd.CommandText = @"INSERT INTO accounts (Email, Password, Phone, Cookie, Note, ProxyKey, PickupAddress, VerifyEmail, VerifyEmailPassword, Status, CreatedAt, UpdatedAt)
                            VALUES ($email, $password, $phone, $cookie, $note, $proxyKey, $pickupAddress, $verifyEmail, $verifyEmailPassword, $status, $createdAt, $updatedAt);
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
                                VerifyEmail = $verifyEmail, VerifyEmailPassword = $verifyEmailPassword,
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

    /// <summary>
    /// Đánh dấu tài khoản "chưa xác nhận được": ghi <c>verify_failed_at = <paramref name="at"/></c> (UTC).
    /// UPDATE gọn CHỈ cột này theo id — KHÔNG đụng các trường khác (form CRUD dùng <see cref="Update"/> riêng,
    /// không quản cột này). Gọi cuối lượt autorun khi phiên còn kẹt ở trang verify/login/captcha.
    /// </summary>
    public void MarkVerifyFailed(long id, DateTime at)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE accounts SET verify_failed_at = $at WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$at", DbSerialization.FormatDate(at));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Gỡ cờ "chưa xác nhận được" (đặt <c>verify_failed_at = NULL</c>) — gọi khi phiên đăng nhập được (autorun
    /// thấy LoggedIn / phiên đọc được số đơn lần đầu). Điều kiện <c>verify_failed_at IS NOT NULL</c> để trả về
    /// số dòng THỰC SỰ đổi (&gt;0 = vừa gỡ một cờ đang bật) — caller dựa vào đó quyết định có làm mới UI hay không.
    /// </summary>
    public int ClearVerifyFailed(long id)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE accounts SET verify_failed_at = NULL WHERE Id = $id AND verify_failed_at IS NOT NULL;";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery();
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
        cmd.Parameters.AddWithValue("$verifyEmail", a.VerifyEmail ?? "");
        cmd.Parameters.AddWithValue("$verifyEmailPassword", a.VerifyEmailPassword ?? "");
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
        VerifyEmail = r.IsDBNull(8) ? "" : r.GetString(8),
        VerifyEmailPassword = r.IsDBNull(9) ? "" : r.GetString(9),
        Status = DbSerialization.ParseEnum<AccountStatus>(r.GetString(10)),
        CreatedAt = DbSerialization.ParseDate(r.GetString(11)),
        UpdatedAt = DbSerialization.ParseDate(r.GetString(12)),
        VerifyFailedAt = r.IsDBNull(13) ? null : DbSerialization.ParseDate(r.GetString(13))
    };
}
