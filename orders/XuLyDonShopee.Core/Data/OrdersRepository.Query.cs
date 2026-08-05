using System.Text;
using Microsoft.Data.Sqlite;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Core.Data;

/// <summary>Phần OrdersRepository: mảng TRUY VẤN cho màn "Đơn hàng" — lọc/đếm/phân trang, dựng WHERE
/// dùng chung, danh sách trạng thái + tên shop cho ComboBox, sửa tạm trạng thái, và helper map dòng.</summary>
public partial class OrdersRepository
{
    /// <summary>Số đơn đã lưu của một tài khoản (dùng cho màn xem — plan 2).</summary>
    public int CountByAccount(long accountId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM orders WHERE account_id = $account;";
        cmd.Parameters.AddWithValue("$account", accountId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// Đọc các đơn theo bộ lọc (màn "Đơn hàng"). Mọi tham số đều tùy chọn — bỏ trống là không lọc:
    /// <list type="bullet">
    /// <item><paramref name="accountId"/>: chỉ đơn của một tài khoản.</item>
    /// <item><paramref name="status"/>: KHỚP CHÍNH XÁC giá trị trạng thái (ComboBox nạp từ
    /// <see cref="AllStatuses"/> nên luôn là giá trị có thật; dùng "=" thay vì LIKE để "Đã hủy" không
    /// dính "Đã hủy một phần").</item>
    /// <item><paramref name="searchText"/>: LIKE <c>%từ%</c> trên mã đơn / người mua / tên sản phẩm; các
    /// ký tự đại diện của LIKE (<c>% _ \</c>) trong từ khóa được escape để tìm đúng nghĩa đen.</item>
    /// <item><paramref name="accountIds"/>: HỢP nhiều tài khoản (<c>account_id IN (...)</c>, tham số hóa từng
    /// id) — dùng cho chế độ lọc shop "gõ dở" ở màn Đơn hàng. Tập RỖNG → trả list rỗng ngay (không query;
    /// <c>IN ()</c> là lỗi cú pháp SQLite). KHÔNG dùng đồng thời với <paramref name="accountId"/>; nếu truyền
    /// cả hai thì <paramref name="accountIds"/> được ưu tiên.</item>
    /// <item><paramref name="limit"/>/<paramref name="offset"/>: phân trang (<c>LIMIT $limit OFFSET $offset</c>,
    /// offset null → 0). Bỏ trống <paramref name="limit"/> → trả TẤT CẢ (mọi caller/test cũ giữ nguyên hành vi).</item>
    /// <item><paramref name="shopLogin"/>/<paramref name="shopExact"/>: lọc theo TÊN shop (cột <c>shop_login</c>) —
    /// dùng cho màn Đơn hàng (đường CỘNG THÊM, độc lập với lọc account). <paramref name="shopExact"/> true → khớp
    /// CHÍNH XÁC (<c>= $shop</c>, như chọn gợi ý); false → LIKE <c>%từ%</c> (gõ dở). Bỏ trống → không lọc shop.</item>
    /// <item><paramref name="createdFromUtc"/>/<paramref name="createdBeforeUtc"/>: lọc theo <c>orders.created_at</c>
    /// (thời điểm đơn được ghi nhận LẦN ĐẦU trên máy), với biên <b>đóng-mở</b>
    /// <c>created_at &gt;= createdFromUtc</c> và <c>created_at &lt; createdBeforeUtc</c>. Bỏ trống một đầu mút →
    /// không chặn đầu đó.</item>
    /// </list>
    /// Sắp xếp đơn sync mới nhất lên đầu.
    /// </summary>
    public List<OrderRow> Query(long? accountId = null, string? status = null, string? searchText = null,
        IReadOnlyCollection<long>? accountIds = null, int? limit = null, int? offset = null,
        string? shopLogin = null, bool shopExact = false, DateTime? createdFromUtc = null,
        DateTime? createdBeforeUtc = null)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();

        // items_json + return_request_code thêm ở CUỐI danh sách cột để KHÔNG lệch chỉ số r.Get*(i) sẵn có trong MapRow.
        var sql = new StringBuilder(@"SELECT id, account_id, order_sn, buyer_username, item_count, item_summary, sku,
    total_price, total_price_text, final_amount, final_amount_text, payment_method, status, status_description, cancel_reason,
    channel, carrier, tracking_number, synced_at, shop_login, items_json, return_request_code
    FROM orders WHERE 1 = 1");

        if (!AppendFilter(cmd, sql, accountId, status, searchText, accountIds, shopLogin, shopExact,
                createdFromUtc, createdBeforeUtc))
        {
            return new List<OrderRow>(); // accountIds rỗng → không tài khoản nào khớp
        }

        sql.Append(" ORDER BY synced_at DESC, id DESC");

        if (limit is not null)
        {
            sql.Append(" LIMIT $limit OFFSET $offset");
            cmd.Parameters.AddWithValue("$limit", limit.Value);
            cmd.Parameters.AddWithValue("$offset", offset ?? 0);
        }

        sql.Append(';');
        cmd.CommandText = sql.ToString();

        var list = new List<OrderRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(MapRow(reader));
        }
        return list;
    }

    /// <summary>
    /// Đếm SỐ ĐƠN khớp bộ lọc (cùng mệnh đề WHERE với <see cref="Query"/>) — mẫu số cho phân trang màn
    /// "Đơn hàng". Xem <see cref="Query"/> về ý nghĩa từng tham số; <paramref name="accountIds"/> rỗng → 0.
    /// </summary>
    public int Count(long? accountId = null, string? status = null, string? searchText = null,
        IReadOnlyCollection<long>? accountIds = null, string? shopLogin = null, bool shopExact = false,
        DateTime? createdFromUtc = null, DateTime? createdBeforeUtc = null)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();

        var sql = new StringBuilder("SELECT COUNT(*) FROM orders WHERE 1 = 1");
        if (!AppendFilter(cmd, sql, accountId, status, searchText, accountIds, shopLogin, shopExact,
                createdFromUtc, createdBeforeUtc))
        {
            return 0; // accountIds rỗng → không tài khoản nào khớp
        }

        sql.Append(';');
        cmd.CommandText = sql.ToString();
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// Dựng phần WHERE + tham số CHUNG cho <see cref="Query"/>/<see cref="Count"/> theo bộ lọc tài khoản/
    /// trạng thái/tìm kiếm. Trả về <c>false</c> khi tập <paramref name="accountIds"/> RỖNG (không tài khoản
    /// nào) — caller phải short-circuit trả kết quả rỗng, KHÔNG query (<c>IN ()</c> là lỗi cú pháp SQLite).
    /// <paramref name="accountIds"/> (nếu khác null) được ưu tiên hơn <paramref name="accountId"/>.
    /// <paramref name="shopLogin"/>/<paramref name="shopExact"/> lọc theo cột <c>shop_login</c> (CỘNG THÊM, độc lập
    /// với lọc account): exact → <c>= $shop</c>; else → LIKE <c>%từ%</c>. LIKE không khớp thì tự 0 dòng (không cần
    /// short-circuit). Bỏ trống → không lọc shop. <paramref name="createdFromUtc"/>/<paramref name="createdBeforeUtc"/>
    /// lọc theo <c>created_at</c> với biên <c>[from, before)</c> để caller tự dựng range ngày cục bộ rồi đổi sang UTC.
    /// </summary>
    private static bool AppendFilter(SqliteCommand cmd, StringBuilder sql,
        long? accountId, string? status, string? searchText, IReadOnlyCollection<long>? accountIds,
        string? shopLogin, bool shopExact, DateTime? createdFromUtc, DateTime? createdBeforeUtc)
    {
        if (accountIds is not null)
        {
            if (accountIds.Count == 0)
            {
                return false; // IN () lỗi cú pháp → caller trả rỗng, không chạm DB
            }

            var names = new List<string>(accountIds.Count);
            var i = 0;
            foreach (var id in accountIds)
            {
                var name = "$acc" + i;
                names.Add(name);
                cmd.Parameters.AddWithValue(name, id);
                i++;
            }
            sql.Append(" AND account_id IN (").Append(string.Join(",", names)).Append(')');
        }
        else if (accountId is not null)
        {
            sql.Append(" AND account_id = $account");
            cmd.Parameters.AddWithValue("$account", accountId.Value);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            sql.Append(" AND status = $status");
            cmd.Parameters.AddWithValue("$status", status);
        }

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            sql.Append(@" AND (order_sn LIKE $q ESCAPE '\'
                           OR buyer_username LIKE $q ESCAPE '\'
                           OR item_summary LIKE $q ESCAPE '\')");
            cmd.Parameters.AddWithValue("$q", "%" + EscapeLike(searchText.Trim()) + "%");
        }

        if (!string.IsNullOrWhiteSpace(shopLogin))
        {
            if (shopExact)
            {
                sql.Append(" AND shop_login = $shop");
                cmd.Parameters.AddWithValue("$shop", shopLogin.Trim());
            }
            else
            {
                sql.Append(@" AND shop_login LIKE $shopLike ESCAPE '\'");
                cmd.Parameters.AddWithValue("$shopLike", "%" + EscapeLike(shopLogin.Trim()) + "%");
            }
        }

        if (createdFromUtc is not null)
        {
            sql.Append(" AND created_at >= $createdFromUtc");
            cmd.Parameters.AddWithValue("$createdFromUtc", DbSerialization.FormatDate(createdFromUtc.Value));
        }

        if (createdBeforeUtc is not null)
        {
            sql.Append(" AND created_at < $createdBeforeUtc");
            cmd.Parameters.AddWithValue("$createdBeforeUtc", DbSerialization.FormatDate(createdBeforeUtc.Value));
        }

        return true;
    }

    /// <summary>
    /// Danh sách trạng thái PHÂN BIỆT (khác null/rỗng) đang có trong bảng — nạp ComboBox lọc. Có thể giới
    /// hạn theo <paramref name="accountId"/> (đường cũ) HOẶC <paramref name="shopLogin"/> (khớp CHÍNH XÁC cột
    /// <c>shop_login</c> — dùng cho màn Đơn hàng lọc theo shop). Sắp xếp tăng dần.
    /// </summary>
    public List<string> AllStatuses(long? accountId = null, string? shopLogin = null)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();

        var sql = new StringBuilder(
            "SELECT DISTINCT status FROM orders WHERE status IS NOT NULL AND TRIM(status) <> ''");
        if (accountId is not null)
        {
            sql.Append(" AND account_id = $account");
            cmd.Parameters.AddWithValue("$account", accountId.Value);
        }
        if (!string.IsNullOrWhiteSpace(shopLogin))
        {
            sql.Append(" AND shop_login = $shop");
            cmd.Parameters.AddWithValue("$shop", shopLogin.Trim());
        }
        sql.Append(" ORDER BY status;");
        cmd.CommandText = sql.ToString();

        var list = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                list.Add(reader.GetString(0));
            }
        }
        return list;
    }

    /// <summary>
    /// Danh sách TÊN shop (cột <c>shop_login</c>) PHÂN BIỆT (khác null/rỗng) đang có trong bảng — nguồn cho
    /// ComboBox lọc shop ở màn Đơn hàng. Sắp xếp tăng dần.
    /// </summary>
    public List<string> AllShopLogins()
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT DISTINCT shop_login FROM orders WHERE shop_login IS NOT NULL AND TRIM(shop_login) <> '' ORDER BY shop_login;";

        var list = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                list.Add(reader.GetString(0));
            }
        }
        return list;
    }

    /// <summary>
    /// SỬA TẠM (local-only) trạng thái MỘT đơn: CHỈ ghi cột <c>status</c> theo khóa nghiệp vụ
    /// <c>(account_id, order_sn)</c> — KHÔNG đụng cột khác, KHÔNG đụng logic sync/gsheet/hub. Dùng cho thao
    /// tác đổi trạng thái thủ công ở màn "Đơn hàng" (double-click). LƯU Ý: đây là sửa CỤC BỘ, lần sync sau
    /// lấy trạng thái thật từ Shopee sẽ GHI ĐÈ giá trị này — CỐ Ý không thêm cờ giữ-vững.
    /// </summary>
    public void UpdateStatus(long accountId, string orderSn, string status)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE orders SET status = $status WHERE account_id = $account AND order_sn = $sn;";
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$account", accountId);
        cmd.Parameters.AddWithValue("$sn", orderSn);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Escape các ký tự đại diện của LIKE để tìm theo nghĩa đen (đi kèm <c>ESCAPE '\'</c>).</summary>
    private static string EscapeLike(string term)
        => term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>Map một dòng kết quả <see cref="Query"/> sang <see cref="OrderRow"/> (theo thứ tự cột SELECT).</summary>
    private static OrderRow MapRow(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        AccountId = r.GetInt64(1),
        OrderSn = r.GetString(2),
        BuyerUsername = r.IsDBNull(3) ? null : r.GetString(3),
        ItemCount = r.IsDBNull(4) ? 0 : r.GetInt32(4),
        ItemSummary = r.IsDBNull(5) ? null : r.GetString(5),
        Sku = r.IsDBNull(6) ? null : r.GetString(6),
        TotalPrice = r.IsDBNull(7) ? null : r.GetInt64(7),
        TotalPriceText = r.IsDBNull(8) ? null : r.GetString(8),
        FinalAmount = r.IsDBNull(9) ? null : r.GetInt64(9),
        FinalAmountText = r.IsDBNull(10) ? null : r.GetString(10),
        PaymentMethod = r.IsDBNull(11) ? null : r.GetString(11),
        Status = r.IsDBNull(12) ? null : r.GetString(12),
        StatusDescription = r.IsDBNull(13) ? null : r.GetString(13),
        CancelReason = r.IsDBNull(14) ? null : r.GetString(14),
        Channel = r.IsDBNull(15) ? null : r.GetString(15),
        Carrier = r.IsDBNull(16) ? null : r.GetString(16),
        TrackingNumber = r.IsDBNull(17) ? null : r.GetString(17),
        SyncedAt = r.IsDBNull(18) ? default : DbSerialization.ParseDate(r.GetString(18)),
        ShopLogin = r.IsDBNull(19) ? null : r.GetString(19),
        ItemsJson = r.IsDBNull(20) ? null : r.GetString(20),
        ReturnRequestCode = r.IsDBNull(21) ? null : r.GetString(21),
    };
}
