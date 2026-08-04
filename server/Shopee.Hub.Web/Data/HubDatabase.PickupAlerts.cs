namespace Shopee.Hub;

/// <summary>Một dòng banner lỗi địa chỉ trên Hub (đồng bộ đa máy theo tài khoản).</summary>
public sealed record OrdersPickupAlertRow(
    string AccountLogin,
    string ShopLogin,
    string Province,
    string CreatedAt,
    string? DismissedAt);

/// <summary>Phần HubDatabase: banner cảnh báo lỗi địa chỉ lấy hàng — khóa theo account_login + shop_login
/// (KHÔNG theo máy) để mọi máy chạy cùng subaccount thấy cùng banner tới khi bấm X.</summary>
public sealed partial class HubDatabase
{
    private void EnsurePickupAlertsSchema() => ExecRaw(@"
CREATE TABLE IF NOT EXISTS orders_pickup_alerts(
  account_login TEXT NOT NULL,
  shop_login TEXT NOT NULL,
  province TEXT DEFAULT '',
  created_at TEXT NOT NULL,
  dismissed_at TEXT,
  updated_by_machine TEXT DEFAULT '',
  PRIMARY KEY(account_login, shop_login));
CREATE INDEX IF NOT EXISTS ix_orders_pickup_alerts_account
  ON orders_pickup_alerts(account_login);");

    /// <summary>Ghi/hiện lại banner (xóa dismissed_at). Trả false nếu thiếu khóa.</summary>
    public bool UpsertPickupAlert(string accountLogin, string shopLogin, string? province, string? machineId)
    {
        var acc = (accountLogin ?? "").Trim();
        var shop = (shopLogin ?? "").Trim();
        if (acc.Length == 0 || shop.Length == 0) return false;

        lock (_gate)
        {
            var now = Iso(DateTimeOffset.UtcNow);
            using var c = _conn.CreateCommand();
            c.CommandText = @"
INSERT INTO orders_pickup_alerts(account_login, shop_login, province, created_at, dismissed_at, updated_by_machine)
VALUES($a, $s, $p, $c, NULL, $m)
ON CONFLICT(account_login, shop_login) DO UPDATE SET
  province=$p, created_at=$c, dismissed_at=NULL, updated_by_machine=$m;";
            c.Parameters.AddWithValue("$a", acc);
            c.Parameters.AddWithValue("$s", shop);
            c.Parameters.AddWithValue("$p", (province ?? "").Trim());
            c.Parameters.AddWithValue("$c", now);
            c.Parameters.AddWithValue("$m", (machineId ?? "").Trim());
            c.ExecuteNonQuery();
            return true;
        }
    }

    /// <summary>Đánh dấu đã đóng (bấm X). Trả false nếu thiếu khóa.</summary>
    public bool DismissPickupAlert(string accountLogin, string shopLogin, string? machineId)
    {
        var acc = (accountLogin ?? "").Trim();
        var shop = (shopLogin ?? "").Trim();
        if (acc.Length == 0 || shop.Length == 0) return false;

        lock (_gate)
        {
            var now = Iso(DateTimeOffset.UtcNow);
            using var c = _conn.CreateCommand();
            c.CommandText = @"
UPDATE orders_pickup_alerts
SET dismissed_at=$d, updated_by_machine=$m
WHERE account_login=$a AND shop_login=$s AND dismissed_at IS NULL;";
            c.Parameters.AddWithValue("$d", now);
            c.Parameters.AddWithValue("$m", (machineId ?? "").Trim());
            c.Parameters.AddWithValue("$a", acc);
            c.Parameters.AddWithValue("$s", shop);
            c.ExecuteNonQuery();
            return true;
        }
    }

    /// <summary>Mọi banner của tài khoản (kể cả đã dismiss) — client merge: Hub dismiss thắng.</summary>
    public List<OrdersPickupAlertRow> ListPickupAlerts(string accountLogin)
    {
        var acc = (accountLogin ?? "").Trim();
        if (acc.Length == 0) return [];

        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = @"
SELECT account_login, shop_login, province, created_at, dismissed_at
FROM orders_pickup_alerts
WHERE account_login=$a
ORDER BY created_at DESC, shop_login COLLATE NOCASE;";
            c.Parameters.AddWithValue("$a", acc);

            var list = new List<OrdersPickupAlertRow>();
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                list.Add(new OrdersPickupAlertRow(
                    r.GetString(0),
                    r.GetString(1),
                    r.IsDBNull(2) ? "" : r.GetString(2),
                    r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4)));
            }
            return list;
        }
    }
}
