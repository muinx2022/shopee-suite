namespace XuLyDonShopee.Core.Data;

/// <summary>Phần OrdersRepository: mảng ĐẾM "Đã bán" — hàng đợi đếm bù (đơn chưa đếm, có SKU) và
/// đánh dấu đơn đã được tính.</summary>
public partial class OrdersRepository
{
    /// <summary>
    /// <b>HÀNG ĐỢI ĐẾM "Đã bán"</b> (đường THỬ LẠI, độc lập với <see cref="DetectNewlyDelivered"/>): các đơn của
    /// tài khoản CHƯA đếm (<c>sold_counted_at IS NULL</c>) và CÓ SKU (khác rỗng). Sắp theo id tăng (đơn cũ trước).
    /// <para>
    /// <b>Vì sao cần:</b> <see cref="DetectNewlyDelivered"/> phát hiện đơn đã-giao bằng cách so trạng thái QUÉT với
    /// trạng thái ĐÃ LƯU. Nếu hub lỗi lúc +1 thì cờ <c>sold_counted_at</c> vẫn NULL <b>nhưng DB đã lưu trạng thái
    /// đã-giao</b> (UpsertMany chạy trước đó) → lượt sync sau KHÔNG còn thấy "chuyển trạng thái" nữa → <b>mất đếm
    /// vĩnh viễn</b>. Hàng đợi theo DB này là đường đếm bù.
    /// </para>
    /// Trả <c>(OrderSn, Sku, Status, StatusDescription, CancelReason)</c> — việc lọc "đã giao"
    /// (<c>ShopeeShippingNav.LaDaGiaoDaBan</c>) và loại đơn hủy (<c>LaDonHuy</c>) do CALLER làm bằng C# (SQL không
    /// biết các luật này).
    /// <para>
    /// <paramref name="updatedBeforeUtc"/> (tùy chọn): chỉ lấy đơn có <c>updated_at</c> ≤ mốc này — chốt chống ĐẾM
    /// ĐÔI với luồng phiên. Luồng phiên chạy <c>UpsertMany</c> (ghi trạng thái đã-giao) rồi mới
    /// <see cref="MarkSoldCounted"/> cho nhóm grandfather ngay sau đó; đọc hàng đợi TRÚNG khe hở vài mili-giây giữa
    /// hai bước sẽ thấy đơn grandfather như "chưa đếm" và +1 NHẦM. Người gọi nền (vòng chờ) truyền mốc lùi lại vài
    /// chục giây để chỉ nhặt đơn đã NGUỘI. Null = không lọc theo thời gian (dùng cho test / đường đồng bộ).
    /// </para>
    /// </summary>
    public IReadOnlyList<(string OrderSn, string Sku, string? Status, string? StatusDescription, string? CancelReason)>
        GetForSoldCountRetry(long accountId, DateTime? updatedBeforeUtc = null)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        // updated_at lưu dạng ISO-8601 round-trip ("o") theo giờ UTC → so sánh CHUỖI là so đúng thứ tự thời gian.
        var ageFilter = updatedBeforeUtc is null ? string.Empty : " AND updated_at IS NOT NULL AND updated_at <= $before";
        cmd.CommandText = @"SELECT order_sn, sku, status, status_description, cancel_reason
    FROM orders
    WHERE account_id = $a
      AND sold_counted_at IS NULL
      AND sku IS NOT NULL AND TRIM(sku) <> ''" + ageFilter + @"
    ORDER BY id;";
        cmd.Parameters.AddWithValue("$a", accountId);
        if (updatedBeforeUtc is not null)
        {
            cmd.Parameters.AddWithValue("$before", DbSerialization.FormatDate(updatedBeforeUtc.Value));
        }

        var list = new List<(string, string, string?, string?, string?)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1))
            {
                continue;
            }
            list.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return list;
    }

    /// <summary>
    /// Đánh dấu các đơn ĐÃ được tính "Đã bán" (chống đếm trùng lượt sync sau). <c>sold_counted_at</c> dùng
    /// <c>COALESCE(cũ, $at)</c> — GIỮ thời điểm đếm LẦN ĐẦU, không đè. Khóa theo <c>(account_id, order_sn)</c>;
    /// đơn không có mã (rỗng) bị bỏ qua. Cập nhật nhiều đơn trong một transaction (mẫu <see cref="MarkHubSynced"/>).
    /// Dùng cho CẢ grandfather (đánh ngay) LẪN đơn +1 (đánh SAU khi hub +1 OK).
    /// </summary>
    public void MarkSoldCounted(long accountId, IEnumerable<string> orderSns, DateTime atUtc)
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
    sold_counted_at = COALESCE(sold_counted_at, $at)
    WHERE account_id = $a AND order_sn = $sn;";
            cmd.Parameters.AddWithValue("$at", atStr);
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$sn", sn);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
