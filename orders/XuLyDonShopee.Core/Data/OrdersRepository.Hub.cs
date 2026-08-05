using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Core.Data;

/// <summary>Phần OrdersRepository: mảng HUB đơn hàng — lấy/đóng cờ đơn đẩy hub (kèm cơ chế "thế hệ"
/// <c>hub_push_gen</c> chống đóng cờ oan), lấy/đóng cờ FILE PHIẾU, và các bộ đếm hàng chờ.</summary>
public partial class OrdersRepository
{
    /// <summary>
    /// Các đơn ỨNG VIÊN đẩy lên HUB đơn hàng: đơn của tài khoản CHƯA từng đẩy hub thành công
    /// (<c>hub_synced_at IS NULL</c>) — dựng lại <see cref="SyncedOrder"/> đầy đủ từ các cột bảng để client map
    /// 1-1 sang DTO hub (mẫu <see cref="GetForGsheetPush"/>). NULL = còn trong hàng đợi ngầm → hub offline thì
    /// lượt sync sau tự đẩy bù. Sắp theo id tăng (đơn cũ trước) để đẩy đúng thứ tự xuất hiện.
    /// <para>
    /// <b>CÓ GHI, không thuần đọc:</b> chụp luôn "thế hệ" dữ liệu của từng đơn được trả về
    /// (<c>hub_push_gen_sent = hub_push_gen</c>) trong CÙNG transaction với lượt đọc. Đơn nào bị đổi TRONG LÚC lô
    /// đang bay lên hub (arrange/ghi mã trả/sync lại) sẽ được +1 <c>hub_push_gen</c> ở các hàm ghi đó, nên
    /// <see cref="MarkHubSynced"/> nhận ra hai số đã lệch và KHÔNG đóng cờ đơn ấy — dữ liệu mới còn đường lên hub
    /// ở lượt đẩy sau.
    /// </para>
    /// </summary>
    public IReadOnlyList<SyncedOrder> GetForHubPush(long accountId)
    {
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();

        // CHỤP thế hệ trước khi đọc (cùng transaction) → mọi đơn trả về đều mang mốc so sánh cho MarkHubSynced.
        using (var claim = conn.CreateCommand())
        {
            claim.Transaction = tx;
            claim.CommandText =
                "UPDATE orders SET hub_push_gen_sent = hub_push_gen WHERE account_id = $a AND hub_synced_at IS NULL;";
            claim.Parameters.AddWithValue("$a", accountId);
            claim.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"SELECT order_sn, shopee_order_id, buyer_username, items_json, item_count, item_summary, sku,
       total_price, total_price_text, final_amount, final_amount_text, payment_method,
       status, status_description, cancel_reason, channel, carrier, tracking_number, shop_login, prepared_at,
       return_request_code, created_at
    FROM orders
    WHERE account_id = $a AND hub_synced_at IS NULL
    ORDER BY id;";
        cmd.Parameters.AddWithValue("$a", accountId);

        var list = new List<SyncedOrder>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                list.Add(new SyncedOrder
                {
                    OrderSn = reader.GetString(0),
                    ShopeeOrderId = reader.IsDBNull(1) ? null : reader.GetString(1),
                    BuyerUsername = reader.IsDBNull(2) ? null : reader.GetString(2),
                    ItemsJson = reader.IsDBNull(3) ? "[]" : reader.GetString(3),
                    ItemCount = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                    ItemSummary = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Sku = reader.IsDBNull(6) ? null : reader.GetString(6),
                    TotalPrice = reader.IsDBNull(7) ? null : reader.GetInt64(7),
                    TotalPriceText = reader.IsDBNull(8) ? null : reader.GetString(8),
                    FinalAmount = reader.IsDBNull(9) ? null : reader.GetInt64(9),
                    FinalAmountText = reader.IsDBNull(10) ? null : reader.GetString(10),
                    PaymentMethod = reader.IsDBNull(11) ? null : reader.GetString(11),
                    Status = reader.IsDBNull(12) ? null : reader.GetString(12),
                    StatusDescription = reader.IsDBNull(13) ? null : reader.GetString(13),
                    CancelReason = reader.IsDBNull(14) ? null : reader.GetString(14),
                    Channel = reader.IsDBNull(15) ? null : reader.GetString(15),
                    Carrier = reader.IsDBNull(16) ? null : reader.GetString(16),
                    TrackingNumber = reader.IsDBNull(17) ? null : reader.GetString(17),
                    ShopLogin = reader.IsDBNull(18) ? null : reader.GetString(18),
                    PreparedAt = reader.IsDBNull(19) ? null : DbSerialization.ParseDate(reader.GetString(19)),
                    ReturnRequestCode = reader.IsDBNull(20) ? null : reader.GetString(20),
                    CreatedAt = reader.IsDBNull(21) ? null : DbSerialization.ParseDate(reader.GetString(21)),
                });
            }
        }

        tx.Commit();
        return list;
    }

    /// <summary>
    /// Map <c>order_sn → shop_login</c> cho các đơn (theo mã) của MỘT tài khoản — dùng khi đẩy FILE PHIẾU lên hub
    /// NHÓM theo shop (lô phiếu chỉ mang <c>OrderSn</c> nên phải tra <c>shop_login</c> từ bảng). Đơn có <c>shop_login</c>
    /// NULL → giá trị <c>null</c> trong map (caller fallback về username subaccount); đơn không tồn tại → KHÔNG có
    /// trong map. Tham số hóa <c>IN (...)</c> từng mã; danh sách rỗng/null → dict RỖNG (không chạm DB — <c>IN ()</c>
    /// là lỗi cú pháp SQLite). So khớp mã đơn theo <see cref="StringComparer.Ordinal"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string?> GetShopLoginsByOrderSns(long accountId, IEnumerable<string> orderSns)
    {
        var sns = orderSns is null
            ? new List<string>()
            : orderSns.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (sns.Count == 0)
        {
            return map; // IN () lỗi cú pháp → trả rỗng, không chạm DB
        }

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        var names = new List<string>(sns.Count);
        for (var i = 0; i < sns.Count; i++)
        {
            var name = "$sn" + i;
            names.Add(name);
            cmd.Parameters.AddWithValue(name, sns[i]);
        }
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.CommandText =
            "SELECT order_sn, shop_login FROM orders WHERE account_id = $a AND order_sn IN ("
            + string.Join(",", names) + ");";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0))
            {
                continue;
            }
            map[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        }
        return map;
    }

    /// <summary>
    /// Đánh dấu các đơn ĐÃ được hub nhận OK (chống đẩy trùng lượt sync sau). Khóa theo
    /// <c>(account_id, order_sn)</c>; đơn không có mã (rỗng) bị bỏ qua. Cập nhật nhiều đơn trong một transaction.
    /// <para>
    /// Chỉ đóng cờ khi đơn CÒN đang chờ (<c>hub_synced_at IS NULL</c> — nên gọi lại lần 2 KHÔNG dời mốc đẩy lần đầu,
    /// y như <c>COALESCE</c> cũ) VÀ "thế hệ" dữ liệu vẫn ĐÚNG bản đã chụp lúc
    /// <see cref="GetForHubPush"/> (<c>hub_push_gen_sent = hub_push_gen</c>). Đơn bị đổi TRONG LÚC lô đang bay
    /// (arrange xong, ghi mã trả hàng, sync lại thấy "Đã hủy"…) đã được +1 <c>hub_push_gen</c> ở các hàm ghi đó →
    /// hai số lệch → KHÔNG đóng cờ → lượt đẩy sau mang dữ liệu MỚI lên hub. Không có bước này thì cờ bị niêm phong
    /// vô điều kiện và dữ liệu mới KHÔNG BAO GIỜ tới hub (đơn hủy kẹt "Chờ lấy hàng", mã trả bị nuốt), mà client
    /// lại DỌN đơn kết thúc vì tưởng đã đẩy xong.
    /// </para>
    /// <c>hub_push_gen_sent IS NULL</c> = đơn chưa từng qua <see cref="GetForHubPush"/> (không có bằng chứng đua) →
    /// vẫn đóng cờ như trước, để đường gọi thẳng không bị chặn oan.
    /// </summary>
    public void MarkHubSynced(long accountId, IEnumerable<string> orderSns, DateTime atUtc)
    {
        var atStr = DbSerialization.FormatDate(atUtc);
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();
        foreach (var sn in orderSns)
        {
            if (string.IsNullOrWhiteSpace(sn))
            {
                continue;
            }
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"UPDATE orders SET
    hub_synced_at = $at
    WHERE account_id = $a AND order_sn = $sn
      AND hub_synced_at IS NULL
      AND (hub_push_gen_sent IS NULL OR hub_push_gen_sent = hub_push_gen);";
            cmd.Parameters.AddWithValue("$at", atStr);
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$sn", sn);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>
    /// Các đơn ỨNG VIÊN đẩy FILE PHIẾU lên HUB: đơn ĐÃ lên hub (<c>hub_synced_at IS NOT NULL</c>) NHƯNG CHƯA đẩy
    /// phiếu (<c>hub_slip_synced_at IS NULL</c>) VÀ đã có mã vận đơn (<c>tracking_number</c> khác rỗng → phiếu đáng
    /// lẽ đã tạo). Trả <c>(OrderSn, TrackingNumber)</c>; việc CÓ file phiếu local hợp lệ hay không do App kiểm sau
    /// (đọc đĩa + magic %PDF-). Sắp theo id tăng (đơn cũ trước). NULL cột → còn trong hàng đợi → lượt sync sau đẩy bù.
    /// </summary>
    public IReadOnlyList<(string OrderSn, string TrackingNumber)> GetForHubSlipPush(long accountId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT order_sn, tracking_number
    FROM orders
    WHERE account_id = $a
      AND hub_synced_at IS NOT NULL
      AND hub_slip_synced_at IS NULL
      AND tracking_number IS NOT NULL AND TRIM(tracking_number) <> ''
    ORDER BY id;";
        cmd.Parameters.AddWithValue("$a", accountId);

        var list = new List<(string, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1))
            {
                continue;
            }
            list.Add((reader.GetString(0), reader.GetString(1)));
        }
        return list;
    }

    /// <summary>
    /// Đánh dấu các đơn ĐÃ được hub lưu FILE PHIẾU OK (chống đẩy trùng lượt sync sau). <c>hub_slip_synced_at</c>
    /// dùng <c>COALESCE(cũ, $at)</c> — GIỮ thời điểm đẩy LẦN ĐẦU, không đè. Khóa theo <c>(account_id, order_sn)</c>;
    /// đơn không có mã (rỗng) bị bỏ qua. Cập nhật nhiều đơn trong một transaction (mẫu <see cref="MarkHubSynced"/>).
    /// </summary>
    public void MarkHubSlipSynced(long accountId, IEnumerable<string> orderSns, DateTime atUtc)
    {
        var atStr = DbSerialization.FormatDate(atUtc);
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();
        foreach (var sn in orderSns)
        {
            if (string.IsNullOrWhiteSpace(sn))
            {
                continue;
            }
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"UPDATE orders SET
    hub_slip_synced_at = COALESCE(hub_slip_synced_at, $at)
    WHERE account_id = $a AND order_sn = $sn;";
            cmd.Parameters.AddWithValue("$at", atStr);
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$sn", sn);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>
    /// SỐ đơn còn CHỜ đẩy lên hub (<c>hub_synced_at IS NULL</c>) — đếm bằng SQL <c>COUNT</c> cho vòng chờ đẩy (chạy
    /// định kỳ trên MỌI tài khoản nên phải nhẹ, không nạp cả danh sách như <see cref="GetForHubPush"/>).
    /// </summary>
    public int CountForHubPush(long accountId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM orders WHERE account_id = $a AND hub_synced_at IS NULL;";
        cmd.Parameters.AddWithValue("$a", accountId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>SỐ đơn còn CHỜ đẩy FILE PHIẾU lên hub — cùng mệnh đề WHERE với <see cref="GetForHubSlipPush"/>
    /// (đã lên hub + chưa đẩy phiếu + có mã vận đơn). Đếm bằng SQL cho vòng chờ đẩy.</summary>
    public int CountForHubSlipPush(long accountId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT COUNT(*) FROM orders
    WHERE account_id = $a
      AND hub_synced_at IS NOT NULL
      AND hub_slip_synced_at IS NULL
      AND tracking_number IS NOT NULL AND TRIM(tracking_number) <> '';";
        cmd.Parameters.AddWithValue("$a", accountId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
