namespace XuLyDonShopee.Core.Data;

/// <summary>
/// Một dòng cảnh báo lỗi địa chỉ lấy hàng (tab Kết quả) — <see cref="DismissedAt"/> null = đang hiện.
/// <para><see cref="HubRev"/> = số hiệu bản ghi Hub mà máy này đã nhận; <see cref="ChoDay"/> = thay đổi tại chỗ
/// chưa đẩy được lên Hub (Hub không được đè dòng này).</para>
/// </summary>
public sealed record PickupAddressAlert(
    long AccountId,
    string ShopLogin,
    string Province,
    DateTime CreatedAt,
    DateTime? DismissedAt,
    long HubRev,
    bool ChoDay);

/// <summary>
/// Kho banner "Cảnh báo: Lỗi địa chỉ. Shop …" trên tab Kết quả — bảng <c>pickup_address_alerts</c>.
/// Active = <c>dismissed_at IS NULL</c>.
/// <para>
/// TÁCH RÕ hai nguồn ghi, đừng gộp lại: <see cref="GhiPhatHienTaiCho"/> / <see cref="DismissTaiCho"/> là thay
/// đổi SINH RA TẠI MÁY NÀY (bật cờ <c>cho_day</c>, phải đẩy lên Hub mới thôi), còn <see cref="ApDungTuHub"/>
/// là NHẬN trạng thái Hub (hạ cờ, lưu <c>hub_rev</c>). Gộp chung chính là gốc của lỗi cũ: đường nhận-từ-Hub
/// ghi đè mốc thời gian của đường phát-hiện-tại-chỗ mỗi 60 giây.
/// </para>
/// </summary>
public sealed class PickupAddressAlertsRepository
{
    private readonly Database _db;

    public PickupAddressAlertsRepository(Database db) => _db = db;

    /// <summary>Vòng shop phát hiện lỗi địa chỉ → hiện banner + ĐÁNH DẤU chờ đẩy lên Hub.</summary>
    public void GhiPhatHienTaiCho(long accountId, string shopLogin, string? province)
        => Ghi(accountId, shopLogin, province, dismissed: false, choDay: true, hubRev: null);

    /// <summary>User bấm X (địa chỉ đã fix) → đóng banner + ĐÁNH DẤU chờ đẩy lên Hub.</summary>
    public void DismissTaiCho(long accountId, string shopLogin)
        => Ghi(accountId, shopLogin, province: null, dismissed: true, choDay: true, hubRev: null);

    /// <summary>Nhận trạng thái từ Hub (rev mới hơn) → theo Hub, hạ cờ chờ đẩy, nhớ <paramref name="hubRev"/>.</summary>
    public void ApDungTuHub(long accountId, string shopLogin, string? province, bool dismissed, long hubRev)
        => Ghi(accountId, shopLogin, province, dismissed, choDay: false, hubRev: hubRev);

    /// <summary>
    /// Đẩy lên Hub thành công → hạ cờ chờ đẩy và nhớ rev Hub vừa cấp. Không đụng trạng thái hiện/ẩn.
    /// <para><paramref name="daDayDismiss"/> = lượt đẩy vừa rồi mang trạng thái ĐÓNG hay MỞ. Chỉ hạ cờ khi
    /// trạng thái local HIỆN TẠI vẫn đúng bằng trạng thái đã đẩy. Bắt buộc phải khớp: user bấm X trong lúc
    /// lượt upsert đang bay thì ack của upsert KHÔNG được xoá dấu của lần bấm X — nếu xoá, POST dismiss mà
    /// hỏng là mất luôn lần bấm X (local đóng, Hub mở, hai bên đứng yên vì rev đã bằng nhau).</para>
    /// <para>Rev 0 (Hub đời cũ không trả rev) vẫn hạ cờ — thay đổi ĐÃ lên Hub — nhưng giữ <c>hub_rev</c> đang
    /// có, lượt GET sau mang rev thật về. Nếu đòi <c>$r >= hub_rev</c> thì dòng từng sync sẽ đẩy lại vô hạn.</para>
    /// </summary>
    public void DanhDauDaDay(long accountId, string shopLogin, long hubRev, bool daDayDismiss)
    {
        var login = (shopLogin ?? string.Empty).Trim();
        if (login.Length == 0)
        {
            return;
        }

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE pickup_address_alerts
SET cho_day = 0, hub_rev = MAX(hub_rev, $r)
WHERE account_id = $a AND shop_login = $s
  AND (CASE WHEN dismissed_at IS NULL THEN 0 ELSE 1 END) = $q;";
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$s", login);
        cmd.Parameters.AddWithValue("$r", hubRev);
        cmd.Parameters.AddWithValue("$q", daDayDismiss ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Danh sách đang hiện (<c>dismissed_at</c> null), mới nhất trước.</summary>
    public IReadOnlyList<PickupAddressAlert> ListActive(long accountId)
        => Doc(accountId, "AND dismissed_at IS NULL");

    /// <summary>Mọi dòng của tài khoản (kể cả đã dismiss) — dùng merge Hub.</summary>
    public IReadOnlyList<PickupAddressAlert> ListAll(long accountId) => Doc(accountId, "");

    /// <summary>Các dòng còn THAY ĐỔI TẠI CHỖ chưa đẩy được lên Hub — hàng đợi outbox của mỗi lượt sync.</summary>
    public IReadOnlyList<PickupAddressAlert> ListChoDay(long accountId) => Doc(accountId, "AND cho_day = 1");

    private void Ghi(long accountId, string shopLogin, string? province, bool dismissed, bool choDay, long? hubRev)
    {
        var login = (shopLogin ?? string.Empty).Trim();
        if (login.Length == 0)
        {
            login = "(không rõ shop)";
        }

        // Thay đổi TẠI CHỖ (hubRev null) không được hạ hub_rev đang giữ — dòng có thể đã nhận rev cao hơn.
        // Nhận TỪ HUB thì ghi thẳng rev Hub vừa cấp.
        var datHubRev = hubRev is null ? "MAX(pickup_address_alerts.hub_rev, $r)" : "$r";

        var now = DbSerialization.FormatDate(DateTime.UtcNow);
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        // province rỗng (vd tombstone dismiss của Hub) → GIỮ tỉnh đang có, đừng xoá dữ liệu đã biết.
        // created_at chỉ làm mốc HIỂN THỊ/sắp xếp, không tham gia quyết định merge (xem xmldoc lớp).
        cmd.CommandText = $@"
INSERT INTO pickup_address_alerts(account_id, shop_login, province, created_at, dismissed_at, hub_rev, cho_day)
VALUES($a, $s, $p, $c, $d, $r, $q)
ON CONFLICT(account_id, shop_login) DO UPDATE SET
  province = CASE WHEN length($p) > 0 THEN $p ELSE pickup_address_alerts.province END,
  created_at = $c,
  dismissed_at = $d,
  hub_rev = {datHubRev},
  cho_day = $q;";
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$s", login);
        cmd.Parameters.AddWithValue("$p", (province ?? string.Empty).Trim());
        cmd.Parameters.AddWithValue("$c", now);
        cmd.Parameters.AddWithValue("$d", dismissed ? now : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$r", hubRev ?? 0L);
        cmd.Parameters.AddWithValue("$q", choDay ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    private IReadOnlyList<PickupAddressAlert> Doc(long accountId, string themDieuKien)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
SELECT account_id, shop_login, province, created_at, dismissed_at, hub_rev, cho_day
FROM pickup_address_alerts
WHERE account_id = $a {themDieuKien}
ORDER BY created_at DESC, shop_login COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("$a", accountId);

        var list = new List<PickupAddressAlert>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new PickupAddressAlert(
                r.GetInt64(0),
                r.GetString(1),
                r.IsDBNull(2) ? "" : r.GetString(2),
                DbSerialization.ParseDate(r.GetString(3)),
                r.IsDBNull(4) ? null : DbSerialization.ParseDate(r.GetString(4)),
                r.IsDBNull(5) ? 0L : r.GetInt64(5),
                !r.IsDBNull(6) && r.GetInt64(6) != 0));
        }
        return list;
    }
}
