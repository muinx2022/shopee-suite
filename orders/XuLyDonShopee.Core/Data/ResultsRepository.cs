using Microsoft.Data.Sqlite;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Core.Data;

/// <summary>
/// Lưu/đọc "Kết quả chuẩn bị hàng" theo shop/ngày cho màn chi tiết tài khoản (tab "Shops"):
/// <list type="bullet">
/// <item><c>account_shops</c>: DANH SÁCH shop của mỗi tài khoản (khóa <c>(account_id, shop_login)</c>) — để cột
/// Shop hiện MỌI shop kể cả shop 0 đơn trong ngày. Ghi mỗi lượt đọc <c>/portal/shop</c> (callback shop-list).</item>
/// <item><c>prepare_daily</c>: SỐ đơn ĐÃ "chuẩn bị hàng" cộng dồn theo <c>(account_id, shop_login, day)</c> —
/// mỗi đơn arrange xong +1 (đếm theo ĐƠN, KHÔNG theo phiếu). <c>day</c> = <c>yyyy-MM-dd</c> giờ ĐỊA PHƯƠNG.</item>
/// </list>
/// <para>
/// KHÓA shop (<c>shop_login</c>) dùng chung một quy tắc ở CẢ hai bảng: <c>LoginName</c> (fallback <c>ShopName</c>)
/// — KHỚP với nhãn shop mà <c>OrdersBridgeSession</c> truyền cho callback đếm (biến <c>shopLogin</c> =
/// <c>LoginName</c> fallback <c>ShopName</c>) nên JOIN theo ngày ăn khớp kể cả khi shop thiếu tên đăng nhập.
/// </para>
/// </summary>
public class ResultsRepository
{
    private readonly Database _db;

    public ResultsRepository(Database db) => _db = db;

    /// <summary>
    /// UPSERT danh sách shop của một tài khoản (thêm shop mới, cập nhật <c>shop_name</c> + <c>sort_order</c> +
    /// <c>updated_at</c> cho shop đã có). <c>shop_login</c> = <see cref="ShopListItem.LoginName"/> (fallback
    /// <see cref="ShopListItem.ShopName"/> — CÙNG quy tắc với nhãn đếm ở <c>OrdersBridgeSession</c>);
    /// <c>shop_name</c> = ShopName (fallback login) để hiển thị. Shop KHÔNG có cả login lẫn tên → bỏ (không định
    /// danh được). Ghi trong một transaction; danh sách rỗng → không chạm DB.
    /// <para>
    /// <c>sort_order</c> = vị trí shop trong <paramref name="shops"/> (0, 1, 2…) — ĐÚNG thứ tự trang
    /// <c>/portal/shop</c> của subaccount trả về, để <see cref="GetShops"/> hiện y hệt thứ tự người dùng thấy trên
    /// Shopee. Ghi cả ở nhánh UPDATE: lượt đọc sau Shopee đổi thứ tự thì app đổi theo.
    /// </para>
    /// </summary>
    public void UpsertShops(long accountId, IEnumerable<ShopListItem> shops)
    {
        if (shops is null)
        {
            return;
        }

        var nowStr = DbSerialization.FormatDate(DateTime.UtcNow);
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();
        // Vị trí shop trong danh sách nguồn — chỉ tăng cho shop THỰC SỰ ghi được (shop bị bỏ vì không định danh
        // được KHÔNG chiếm số thứ tự, kẻo để lại lỗ hổng giữa dãy).
        var sortOrder = 0;
        foreach (var shop in shops)
        {
            if (shop is null)
            {
                continue;
            }

            // Khóa shop: LoginName ưu tiên, thiếu thì ShopName (khớp biến shopLogin bên OrdersBridgeSession).
            var login = !string.IsNullOrWhiteSpace(shop.LoginName)
                ? shop.LoginName.Trim()
                : (shop.ShopName ?? string.Empty).Trim();
            if (login.Length == 0)
            {
                continue; // không có định danh nào → không thể làm khóa
            }
            // Tên hiển thị: ShopName ưu tiên, thiếu thì dùng chính login.
            var name = !string.IsNullOrWhiteSpace(shop.ShopName) ? shop.ShopName.Trim() : login;

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO account_shops (account_id, shop_login, shop_name, sort_order, updated_at)
    VALUES ($a, $login, $name, $sort, $now)
    ON CONFLICT(account_id, shop_login) DO UPDATE SET shop_name = $name, sort_order = $sort, updated_at = $now;";
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$login", login);
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$sort", sortOrder);
            cmd.Parameters.AddWithValue("$now", nowStr);
            cmd.ExecuteNonQuery();
            sortOrder++;
        }
        tx.Commit();
    }

    /// <summary>
    /// MỌI shop đã lưu của một tài khoản (<c>shop_login</c> + <c>shop_name</c>), sắp theo ĐÚNG thứ tự trang
    /// <c>/portal/shop</c> của subaccount (<c>sort_order</c> do <see cref="UpsertShops"/> ghi). Dùng cho cột
    /// Shop tab "Shops" (hiện cả shop 0 đơn trong ngày). Tài khoản chưa có shop nào → list rỗng.
    /// </summary>
    public IReadOnlyList<(string ShopLogin, string? ShopName)> GetShops(long accountId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        // Shop ĐÃ biết thứ tự nguồn đứng trước theo đúng thứ tự đó; shop dữ liệu CŨ (sort_order NULL — chưa đọc lại
        // shop-list lần nào) xuống cuối, xếp theo tên hiển thị như trước.
        cmd.CommandText = @"SELECT shop_login, shop_name FROM account_shops
    WHERE account_id = $a
    ORDER BY CASE WHEN sort_order IS NULL THEN 1 ELSE 0 END, sort_order, COALESCE(shop_name, shop_login);";
        cmd.Parameters.AddWithValue("$a", accountId);

        var list = new List<(string, string?)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0))
            {
                continue;
            }
            list.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        }
        return list;
    }

    /// <summary>
    /// MỐC "số yêu cầu trả hàng" của lần check GẦN NHẤT ở shop này
    /// (<c>account_shops.return_count_last_tra_hang</c>). <c>null</c> = shop CHƯA từng check (kể cả khi shop chưa
    /// có dòng nào) → lượt này quét trang đầu một lượt rồi mới chốt mốc (xem <c>TraHangParser.QuyetDinhCheck</c>).
    /// Khóa shop dùng CHUNG quy tắc với <see cref="UpsertShops"/>: <c>LoginName</c> fallback <c>ShopName</c>.
    /// <paramref name="shopLogin"/> rỗng → null.
    /// <para><b>CỐ Ý không đọc cột cũ <c>return_count_last</c></b>: nó đếm trên tab "Tất cả" (gộp cả đơn hủy) nên
    /// so với số của tab "Đơn Trả hàng Hoàn tiền" là so hai đại lượng khác nhau. Cột mới bắt đầu NULL để mọi shop
    /// tự quét lại một lượt — xem chú thích ở <c>Database.Initialize</c>.</para>
    /// </summary>
    public int? GetReturnCount(long accountId, string shopLogin)
    {
        if (string.IsNullOrWhiteSpace(shopLogin))
        {
            return null;
        }

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT return_count_last_tra_hang FROM account_shops WHERE account_id = $a AND shop_login = $login;";
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$login", shopLogin.Trim());

        var res = cmd.ExecuteScalar();
        return res is null || res == DBNull.Value ? null : Convert.ToInt32(res);
    }

    /// <summary>
    /// Ghi MỐC "số yêu cầu trả hàng" vừa đọc được cho shop này. UPSERT (mẫu <see cref="UpsertShops"/>) để mốc vẫn
    /// lưu được cả khi shop chưa có dòng trong bảng — nhưng KHÔNG đụng <c>shop_name</c>/<c>sort_order</c> của dòng
    /// đã có (danh sách shop do <see cref="UpsertShops"/> làm chủ). <paramref name="shopLogin"/> rỗng → bỏ qua;
    /// số âm được kẹp về 0. Ghi vào <c>return_count_last_tra_hang</c> — xem <see cref="GetReturnCount"/> về việc
    /// cột cũ <c>return_count_last</c> KHÔNG còn được đụng tới.
    /// </summary>
    public void SetReturnCount(long accountId, string shopLogin, int count)
    {
        if (string.IsNullOrWhiteSpace(shopLogin))
        {
            return;
        }

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            @"INSERT INTO account_shops (account_id, shop_login, return_count_last_tra_hang, updated_at)
    VALUES ($a, $login, $c, $now)
    ON CONFLICT(account_id, shop_login) DO UPDATE SET return_count_last_tra_hang = $c;";
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$login", shopLogin.Trim());
        cmd.Parameters.AddWithValue("$c", Math.Max(0, count));
        cmd.Parameters.AddWithValue("$now", DbSerialization.FormatDate(DateTime.UtcNow));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Lượt check trả hàng gần nhất của shop này có BIẾT là còn sót không
    /// (<c>account_shops.tra_hang_con_sot</c>). <c>true</c> ⇒ lượt tới chạy "chế độ rút tồn đọng": vẫn lật trang
    /// dù trang đầu không còn mã mới (xem <c>ShopFlowRunner.CheckDonTraHangAsync</c>).
    /// <para>Shop chưa có dòng / cột NULL (DB đời cũ) → <c>false</c> = hành vi y hệt trước 11/08/2026.</para>
    /// </summary>
    public bool GetTraHangConSot(long accountId, string shopLogin)
    {
        if (string.IsNullOrWhiteSpace(shopLogin))
        {
            return false;
        }

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT tra_hang_con_sot FROM account_shops WHERE account_id = $a AND shop_login = $login;";
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$login", shopLogin.Trim());

        var res = cmd.ExecuteScalar();
        return res is not null && res != DBNull.Value && Convert.ToInt64(res) != 0;
    }

    /// <summary>
    /// Ghi cờ "còn sót" của bước check trả hàng cho shop này. UPSERT theo đúng mẫu <see cref="SetReturnCount"/>
    /// (ghi được cả khi shop chưa có dòng) và <b>KHÔNG đụng cột nào khác</b> — mốc số yêu cầu, tên shop, thứ tự
    /// đều do đường khác làm chủ. <paramref name="shopLogin"/> rỗng → bỏ qua.
    /// </summary>
    public void SetTraHangConSot(long accountId, string shopLogin, bool conSot)
    {
        if (string.IsNullOrWhiteSpace(shopLogin))
        {
            return;
        }

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            @"INSERT INTO account_shops (account_id, shop_login, tra_hang_con_sot, updated_at)
    VALUES ($a, $login, $c, $now)
    ON CONFLICT(account_id, shop_login) DO UPDATE SET tra_hang_con_sot = $c;";
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$login", shopLogin.Trim());
        cmd.Parameters.AddWithValue("$c", conSot ? 1 : 0);
        cmd.Parameters.AddWithValue("$now", DbSerialization.FormatDate(DateTime.UtcNow));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// +1 số đơn ĐÃ "chuẩn bị hàng" cho <c>(accountId, shopLogin, day)</c> — mỗi đơn arrange xong gọi một lần
    /// (đếm theo ĐƠN, không theo phiếu). Chưa có dòng ngày đó → tạo mới count=1; đã có → count+1. <paramref name="day"/>
    /// là chuỗi <c>yyyy-MM-dd</c> giờ địa phương. <paramref name="shopLogin"/>/<paramref name="day"/> rỗng → bỏ qua.
    /// </summary>
    public void IncrementPrepared(long accountId, string shopLogin, string day)
    {
        if (string.IsNullOrWhiteSpace(shopLogin) || string.IsNullOrWhiteSpace(day))
        {
            return;
        }

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO prepare_daily (account_id, shop_login, day, count)
    VALUES ($a, $login, $day, 1)
    ON CONFLICT(account_id, shop_login, day) DO UPDATE SET count = count + 1;";
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$login", shopLogin.Trim());
        cmd.Parameters.AddWithValue("$day", day);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Map <c>shop_login → count</c> số đơn chuẩn bị hàng của một tài khoản trong ĐÚNG một ngày (<paramref name="day"/> =
    /// <c>yyyy-MM-dd</c>). Ngày không có đơn nào → map rỗng. So khớp shop_login theo <see cref="StringComparer.Ordinal"/>
    /// (khớp cách lưu ở <see cref="UpsertShops"/>/<see cref="IncrementPrepared"/>).
    /// </summary>
    public Dictionary<string, int> GetPreparedByDay(long accountId, string day)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(day))
        {
            return map;
        }

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT shop_login, count FROM prepare_daily WHERE account_id = $a AND day = $day;";
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$day", day);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0))
            {
                continue;
            }
            map[reader.GetString(0)] = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
        }
        return map;
    }
}
