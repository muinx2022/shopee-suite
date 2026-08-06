namespace Shopee.Hub;

/// <summary>Một dòng banner lỗi địa chỉ trên Hub (đồng bộ đa máy theo tài khoản).
/// <paramref name="Rev"/> = số hiệu bản ghi, Hub tăng sau MỖI lần ghi — client merge theo số này.</summary>
public sealed record OrdersPickupAlertRow(
    string AccountLogin,
    string ShopLogin,
    string Province,
    string CreatedAt,
    string? DismissedAt,
    long Rev);

/// <summary>Một banner lỗi địa chỉ ĐANG MỞ (chưa ai bấm X) — dòng cho trang web của Hub. Khác
/// <see cref="OrdersPickupAlertRow"/> (bản cho client, gồm cả tombstone): ở đây bỏ <c>dismissed_at</c> (luôn NULL)
/// và thêm <paramref name="UpdatedByMachine"/> = máy ghi lần cuối, để admin biết máy nào đang kêu.
/// <para><paramref name="CreatedAt"/> do CHÍNH HUB ghi (<c>Iso(UtcNow)</c> lúc nhận upsert), không phải mốc của
/// máy client → hiển thị "bao lâu trước" trên web là so đồng hồ hub với đồng hồ hub. Vẫn CHỈ để xem: không luật
/// nào của banner được quyết định bằng mốc thời gian (xem xmldoc lớp bên dưới).</para></summary>
public sealed record PickupAlertActiveRow(
    string AccountLogin,
    string ShopLogin,
    string Province,
    DateTimeOffset CreatedAt,
    string UpdatedByMachine,
    long Rev);

/// <summary>
/// Phần HubDatabase: banner cảnh báo lỗi địa chỉ lấy hàng — khóa theo account_login + shop_login
/// (KHÔNG theo máy) để mọi máy chạy cùng subaccount thấy cùng banner tới khi bấm X.
/// <para>
/// Trạng thái chốt bằng <c>rev</c> (bộ đếm của RIÊNG Hub), KHÔNG bằng mốc thời gian. Lý do: mốc do client gửi
/// đến từ các máy có đồng hồ độc lập; hai lần vá trước đem mốc máy này so mốc máy kia đều đẻ ca hỏng nặng
/// (banner kẹt không gỡ được / Hub từ chối bấm X vĩnh viễn). <c>created_at</c> và <c>dismissed_at</c> giữ lại
/// CHỈ để đọc và chẩn đoán — tuyệt đối không dùng để quyết định.
/// </para>
/// </summary>
public sealed partial class HubDatabase
{
    private void EnsurePickupAlertsSchema()
    {
        ExecRaw(@"
CREATE TABLE IF NOT EXISTS orders_pickup_alerts(
  account_login TEXT NOT NULL,
  shop_login TEXT NOT NULL,
  province TEXT DEFAULT '',
  created_at TEXT NOT NULL,
  dismissed_at TEXT,
  updated_by_machine TEXT DEFAULT '',
  rev INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY(account_login, shop_login));
CREATE INDEX IF NOT EXISTS ix_orders_pickup_alerts_account
  ON orders_pickup_alerts(account_login);");

        // DB đã tồn tại trước khi có rev → thêm cột.
        AddColumnIfMissing("orders_pickup_alerts", "rev", "INTEGER NOT NULL DEFAULT 0");

        // Nâng dòng đời cũ lên rev 1: client mới khởi tạo hub_rev = 0, nên dòng còn rev 0 sẽ KHÔNG BAO GIỜ
        // thoả "rev Hub > rev đã nhận" ⇒ tombstone/banner cũ không lan được sang máy nào. Idempotent: sau lượt
        // này không còn dòng rev 0 (mọi lượt ghi đều đặt rev ≥ 1).
        ExecRaw("UPDATE orders_pickup_alerts SET rev = 1 WHERE rev = 0;");
    }

    /// <summary>
    /// Ghi/hiện lại banner (xoá <c>dismissed_at</c>) và tăng <c>rev</c>. Trả rev MỚI, 0 = thiếu khoá.
    /// Ghi vô điều kiện — người ghi sau thắng; hội tụ do mọi máy đều đọc lại cùng một <c>rev</c>.
    /// </summary>
    public long UpsertPickupAlert(string accountLogin, string shopLogin, string? province, string? machineId)
    {
        var acc = (accountLogin ?? "").Trim();
        var shop = (shopLogin ?? "").Trim();
        if (acc.Length == 0 || shop.Length == 0) return 0;

        lock (_gate)
        {
            var now = Iso(DateTimeOffset.UtcNow);
            using var c = _conn.CreateCommand();
            c.CommandText = @"
INSERT INTO orders_pickup_alerts(account_login, shop_login, province, created_at, dismissed_at, updated_by_machine, rev)
VALUES($a, $s, $p, $now, NULL, $m, 1)
ON CONFLICT(account_login, shop_login) DO UPDATE SET
  province=$p,
  created_at=$now,
  dismissed_at=NULL,
  updated_by_machine=$m,
  rev=orders_pickup_alerts.rev+1
RETURNING rev;";
            c.Parameters.AddWithValue("$a", acc);
            c.Parameters.AddWithValue("$s", shop);
            c.Parameters.AddWithValue("$p", (province ?? "").Trim());
            c.Parameters.AddWithValue("$now", now);
            c.Parameters.AddWithValue("$m", (machineId ?? "").Trim());
            return Convert.ToInt64(c.ExecuteScalar() ?? 0L);
        }
    }

    /// <summary>
    /// Đánh dấu đã đóng (bấm X) và tăng <c>rev</c>. Chưa có dòng vẫn INSERT tombstone để máy khác kéo Hub biết
    /// đã đóng. Trả rev MỚI, 0 = thiếu khoá.
    /// </summary>
    public long DismissPickupAlert(string accountLogin, string shopLogin, string? machineId)
    {
        var acc = (accountLogin ?? "").Trim();
        var shop = (shopLogin ?? "").Trim();
        if (acc.Length == 0 || shop.Length == 0) return 0;

        lock (_gate)
        {
            var now = Iso(DateTimeOffset.UtcNow);
            using var c = _conn.CreateCommand();
            c.CommandText = @"
INSERT INTO orders_pickup_alerts(account_login, shop_login, province, created_at, dismissed_at, updated_by_machine, rev)
VALUES($a, $s, '', $now, $now, $m, 1)
ON CONFLICT(account_login, shop_login) DO UPDATE SET
  dismissed_at=$now,
  updated_by_machine=$m,
  rev=orders_pickup_alerts.rev+1
RETURNING rev;";
            c.Parameters.AddWithValue("$a", acc);
            c.Parameters.AddWithValue("$s", shop);
            c.Parameters.AddWithValue("$now", now);
            c.Parameters.AddWithValue("$m", (machineId ?? "").Trim());
            return Convert.ToInt64(c.ExecuteScalar() ?? 0L);
        }
    }

    /// <summary>Mọi banner của tài khoản (kể cả đã dismiss) — client merge theo <c>rev</c>.</summary>
    public List<OrdersPickupAlertRow> ListPickupAlerts(string accountLogin)
    {
        var acc = (accountLogin ?? "").Trim();
        if (acc.Length == 0) return [];

        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = @"
SELECT account_login, shop_login, province, created_at, dismissed_at, rev
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
                    r.IsDBNull(4) ? null : r.GetString(4),
                    r.IsDBNull(5) ? 0L : r.GetInt64(5)));
            }
            return list;
        }
    }

    /// <summary>
    /// MỌI banner đang MỞ của MỌI tài khoản (<c>dismissed_at IS NULL</c>) — nguồn của section "Cảnh báo địa chỉ"
    /// trên trang chủ web. Hàm ĐỌC thuần: không sweep, không hạn tuổi, không so mốc thời gian — một dòng còn ở
    /// đây đúng bằng "chưa máy nào (và chưa admin nào) bấm X". Sắp mới→cũ theo <c>created_at</c> chỉ để hiển thị.
    /// </summary>
    public List<PickupAlertActiveRow> ActivePickupAlerts()
    {
        // Connection ĐỌC riêng (WAL): trang chủ gọi hàm này theo nhịp làm mới, không đáng giữ khoá ghi toàn cục.
        using var conn = OpenReadConnection();
        var list = new List<PickupAlertActiveRow>();
        using var c = conn.CreateCommand();
        c.CommandText = @"
SELECT account_login, shop_login, province, created_at, updated_by_machine, rev
FROM orders_pickup_alerts
WHERE dismissed_at IS NULL
ORDER BY created_at DESC, account_login COLLATE NOCASE, shop_login COLLATE NOCASE;";
        using var rd = c.ExecuteReader();
        while (rd.Read())
        {
            list.Add(new PickupAlertActiveRow(
                S(rd, 0), S(rd, 1), S(rd, 2), D(rd, 3), S(rd, 4), rd.IsDBNull(5) ? 0L : rd.GetInt64(5)));
        }
        return list;
    }
}
