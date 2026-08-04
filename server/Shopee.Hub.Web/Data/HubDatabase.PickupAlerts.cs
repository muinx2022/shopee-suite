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

    /// <summary>
    /// Ghi/hiện lại banner. <paramref name="occurredAtIso"/> = mốc sự kiện phía client (ISO);
    /// thiếu → dùng giờ nhận trên Hub. Nếu dòng đang dismiss và mốc sự kiện <c>&lt;</c> <c>dismissed_at</c>
    /// thì bỏ qua (không clear dismiss — chống upsert chậm tới sau khi user bấm X).
    /// </summary>
    public bool UpsertPickupAlert(
        string accountLogin, string shopLogin, string? province, string? machineId, string? occurredAtIso = null)
    {
        var acc = (accountLogin ?? "").Trim();
        var shop = (shopLogin ?? "").Trim();
        if (acc.Length == 0 || shop.Length == 0) return false;

        lock (_gate)
        {
            var now = Iso(DateTimeOffset.UtcNow);
            var occ = ChuanHoaIso(occurredAtIso) ?? now;
            using var c = _conn.CreateCommand();
            // ISO "o" so sánh lexicographic được khi cùng dạng UTC.
            c.CommandText = @"
INSERT INTO orders_pickup_alerts(account_login, shop_login, province, created_at, dismissed_at, updated_by_machine)
VALUES($a, $s, $p, $occ, NULL, $m)
ON CONFLICT(account_login, shop_login) DO UPDATE SET
  province=$p,
  updated_by_machine=$m,
  created_at=CASE
    WHEN orders_pickup_alerts.dismissed_at IS NOT NULL
         AND $occ < orders_pickup_alerts.dismissed_at
    THEN orders_pickup_alerts.created_at
    ELSE $occ
  END,
  dismissed_at=CASE
    WHEN orders_pickup_alerts.dismissed_at IS NOT NULL
         AND $occ < orders_pickup_alerts.dismissed_at
    THEN orders_pickup_alerts.dismissed_at
    ELSE NULL
  END;";
            c.Parameters.AddWithValue("$a", acc);
            c.Parameters.AddWithValue("$s", shop);
            c.Parameters.AddWithValue("$p", (province ?? "").Trim());
            c.Parameters.AddWithValue("$occ", occ);
            c.Parameters.AddWithValue("$m", (machineId ?? "").Trim());
            c.ExecuteNonQuery();
            return true;
        }
    }

    /// <summary>
    /// Đánh dấu đã đóng (bấm X): UPSERT tombstone — chưa có dòng vẫn INSERT với <c>dismissed_at</c>
    /// để máy khác kéo Hub biết đã đóng. <paramref name="occurredAtIso"/> thiếu → giờ Hub.
    /// <para>CỐ Ý ghi vô điều kiện, KHÔNG so <c>$d</c> với <c>created_at</c>: hai mốc đó đến từ hai máy có đồng
    /// hồ độc lập, nên hễ so là có ca máy phát hiện lỗi chạy nhanh giờ khiến lần bấm X của máy khác bị Hub từ
    /// chối vĩnh viễn — banner không bao giờ gỡ được. Bấm X phải LUÔN thắng; lỗi còn thật thì vòng shop kế
    /// upsert với mốc mới hơn <c>dismissed_at</c> nên banner tự hiện lại.</para>
    /// </summary>
    public bool DismissPickupAlert(
        string accountLogin, string shopLogin, string? machineId, string? occurredAtIso = null)
    {
        var acc = (accountLogin ?? "").Trim();
        var shop = (shopLogin ?? "").Trim();
        if (acc.Length == 0 || shop.Length == 0) return false;

        lock (_gate)
        {
            var now = Iso(DateTimeOffset.UtcNow);
            var d = ChuanHoaIso(occurredAtIso) ?? now;
            using var c = _conn.CreateCommand();
            c.CommandText = @"
INSERT INTO orders_pickup_alerts(account_login, shop_login, province, created_at, dismissed_at, updated_by_machine)
VALUES($a, $s, '', $d, $d, $m)
ON CONFLICT(account_login, shop_login) DO UPDATE SET
  dismissed_at=$d,
  updated_by_machine=$m;";
            c.Parameters.AddWithValue("$a", acc);
            c.Parameters.AddWithValue("$s", shop);
            c.Parameters.AddWithValue("$d", d);
            c.Parameters.AddWithValue("$m", (machineId ?? "").Trim());
            c.ExecuteNonQuery();
            return true;
        }
    }

    /// <summary>Mọi banner của tài khoản (kể cả đã dismiss) — client merge theo mốc thời gian.</summary>
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

    /// <summary>Chuẩn hoá chuỗi ISO client gửi; rỗng/không parse → null.</summary>
    private static string? ChuanHoaIso(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return null;
        return DateTimeOffset.TryParse(iso.Trim(), out var d) ? Iso(d.ToUniversalTime()) : null;
    }
}
