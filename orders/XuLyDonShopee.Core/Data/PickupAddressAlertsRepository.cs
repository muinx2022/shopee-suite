namespace XuLyDonShopee.Core.Data;

/// <summary>Một dòng cảnh báo lỗi địa chỉ lấy hàng (tab Kết quả) — <see cref="DismissedAt"/> null = đang hiện.</summary>
public sealed record PickupAddressAlert(
    long AccountId,
    string ShopLogin,
    string Province,
    DateTime CreatedAt,
    DateTime? DismissedAt);

/// <summary>
/// Kho banner "Cảnh báo: Lỗi địa chỉ. Shop …" trên tab Kết quả — bảng <c>pickup_address_alerts</c>.
/// Active = <c>dismissed_at IS NULL</c>. Lỗi lại cùng shop sau khi đã X → upsert xóa dismiss (hiện lại).
/// </summary>
public sealed class PickupAddressAlertsRepository
{
    private readonly Database _db;

    public PickupAddressAlertsRepository(Database db) => _db = db;

    /// <summary>Ghi/hiện lại cảnh báo cho shop (xóa <c>dismissed_at</c>, cập nhật tỉnh + mốc tạo).</summary>
    public void Upsert(long accountId, string shopLogin, string? province)
    {
        var login = (shopLogin ?? string.Empty).Trim();
        if (login.Length == 0)
        {
            login = "(không rõ shop)";
        }

        var now = DbSerialization.FormatDate(DateTime.UtcNow);
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO pickup_address_alerts(account_id, shop_login, province, created_at, dismissed_at)
VALUES($a, $s, $p, $c, NULL)
ON CONFLICT(account_id, shop_login) DO UPDATE SET
  province = $p, created_at = $c, dismissed_at = NULL;";
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$s", login);
        cmd.Parameters.AddWithValue("$p", (province ?? string.Empty).Trim());
        cmd.Parameters.AddWithValue("$c", now);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Đánh dấu đã đóng (bấm X). Không xóa dòng — giữ lịch sử / chống upsert nhầm từ bản cũ.</summary>
    public void Dismiss(long accountId, string shopLogin)
    {
        var login = (shopLogin ?? string.Empty).Trim();
        if (login.Length == 0)
        {
            return;
        }

        var now = DbSerialization.FormatDate(DateTime.UtcNow);
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE pickup_address_alerts SET dismissed_at = $d
WHERE account_id = $a AND shop_login = $s AND dismissed_at IS NULL;";
        cmd.Parameters.AddWithValue("$d", now);
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$s", login);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Danh sách đang hiện (<c>dismissed_at</c> null), mới nhất trước.</summary>
    public IReadOnlyList<PickupAddressAlert> ListActive(long accountId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT account_id, shop_login, province, created_at, dismissed_at
FROM pickup_address_alerts
WHERE account_id = $a AND dismissed_at IS NULL
ORDER BY created_at DESC, shop_login COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("$a", accountId);

        var list = new List<PickupAddressAlert>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(ReadRow(r));
        }
        return list;
    }

    /// <summary>Mọi dòng của tài khoản (kể cả đã dismiss) — dùng merge Hub.</summary>
    public IReadOnlyList<PickupAddressAlert> ListAll(long accountId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT account_id, shop_login, province, created_at, dismissed_at
FROM pickup_address_alerts
WHERE account_id = $a
ORDER BY created_at DESC, shop_login COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("$a", accountId);

        var list = new List<PickupAddressAlert>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(ReadRow(r));
        }
        return list;
    }

    private static PickupAddressAlert ReadRow(Microsoft.Data.Sqlite.SqliteDataReader r)
    {
        var created = DbSerialization.ParseDate(r.GetString(3));
        DateTime? dismissed = r.IsDBNull(4) ? null : DbSerialization.ParseDate(r.GetString(4));
        return new PickupAddressAlert(
            r.GetInt64(0),
            r.GetString(1),
            r.IsDBNull(2) ? "" : r.GetString(2),
            created,
            dismissed);
    }
}
