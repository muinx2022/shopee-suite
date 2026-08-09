namespace XuLyDonShopee.Core.Data;

/// <summary>Phần OrdersRepository: mảng GOOGLE SHEET — lấy đơn ứng viên đẩy sheet, đánh dấu đã ghi,
/// nhớ tab đã ghi lần đầu và đếm đơn còn chờ ghi sheet.</summary>
public partial class OrdersRepository
{
    /// <summary>
    /// SUPERSET các đơn ỨNG VIÊN đẩy lên Google Sheet: <b>MỌI đơn của tài khoản</b> (KHÔNG lọc mã vận đơn nữa —
    /// đơn "Chờ lấy hàng" chưa có vận đơn vẫn cần ghi dòng TRẮNG), KÈM các cột trạng thái + cờ gsheet để
    /// <c>AccountSession</c> quyết bằng C# đơn nào cần gửi (mới / thiếu link phiếu / trạng thái hủy đổi / vận
    /// đơn vừa xuất hiện) và đơn nào bỏ qua (hủy mà chưa từng có vận đơn). Sắp theo id tăng (đơn cũ trước).
    /// </summary>
    public IReadOnlyList<GsheetPendingOrder> GetForGsheetPush(long accountId, string? shopId = null)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        // Lọc theo shop khi có shopId (mô hình nhiều-shop: chỉ đẩy đơn CỦA shop hiện tại, TenShop lấy theo shop đó
        // — không đẩy nhầm tên shop). shopId null → hành vi CŨ (mọi đơn của account).
        var shopFilter = string.IsNullOrEmpty(shopId) ? string.Empty : " AND shop_id = $shopId";
        // items_json + cặp cột "đơn trả hàng" + shop_login thêm ở CUỐI danh sách cột để KHÔNG lệch chỉ số
        // reader.Get*(i) sẵn có.
        cmd.CommandText = @"SELECT order_sn, tracking_number, sku, total_price, final_amount,
       status, status_description, cancel_reason,
       gsheet_synced_at, gsheet_file_url, gsheet_da_huy, gsheet_da_co_van_don, gsheet_da_co_uoc_tinh,
       sold_counted_at, hub_synced_at, hub_slip_synced_at, gsheet_tab, items_json,
       return_request_code, gsheet_da_co_don_tra_hang, shop_login, gsheet_push_gen
    FROM orders
    WHERE account_id = $a" + shopFilter + @"
    ORDER BY id;";
        cmd.Parameters.AddWithValue("$a", accountId);
        if (!string.IsNullOrEmpty(shopId))
        {
            cmd.Parameters.AddWithValue("$shopId", shopId);
        }

        var list = new List<GsheetPendingOrder>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new GsheetPendingOrder(
                OrderSn: reader.GetString(0),
                TrackingNumber: reader.IsDBNull(1) ? null : reader.GetString(1),
                Sku: reader.IsDBNull(2) ? null : reader.GetString(2),
                ItemsJson: reader.IsDBNull(17) ? null : reader.GetString(17),
                TotalPrice: reader.IsDBNull(3) ? null : reader.GetInt64(3),
                FinalAmount: reader.IsDBNull(4) ? null : reader.GetInt64(4),
                Status: reader.IsDBNull(5) ? null : reader.GetString(5),
                StatusDescription: reader.IsDBNull(6) ? null : reader.GetString(6),
                CancelReason: reader.IsDBNull(7) ? null : reader.GetString(7),
                DaGhiSheet: !reader.IsDBNull(8),
                // "Đã TỪNG có dòng" — bền qua nút "Đẩy lại" (nút đó xoá gsheet_synced_at nhưng GIỮ gsheet_tab).
                // OR với gsheet_synced_at là dây bảo hiểm cho đơn cũ hơn migration backfill cột tab.
                DaTungGhiSheet: !reader.IsDBNull(16) || !reader.IsDBNull(8),
                FileUrl: reader.IsDBNull(9) ? null : reader.GetString(9),
                GsheetDaHuy: reader.IsDBNull(10) ? null : reader.GetInt64(10),
                GsheetDaCoVanDon: reader.IsDBNull(11) ? null : reader.GetInt64(11),
                GsheetDaCoUocTinh: reader.IsDBNull(12) ? null : reader.GetInt64(12),
                DaDemDaBan: !reader.IsDBNull(13),
                DaDayHub: !reader.IsDBNull(14),
                DaDayPhieuHub: !reader.IsDBNull(15),
                GsheetTab: reader.IsDBNull(16) ? null : reader.GetString(16),
                GsheetPushGen: reader.IsDBNull(21) ? 0L : reader.GetInt64(21),
                ReturnRequestCode: reader.IsDBNull(18) ? null : reader.GetString(18),
                GsheetDaCoDonTraHang: reader.IsDBNull(19) ? null : reader.GetInt64(19),
                ShopLogin: reader.IsDBNull(20) ? null : reader.GetString(20)));
        }
        return list;
    }

    /// <summary>
    /// Đánh dấu một đơn ĐÃ ghi lên Google Sheet. <c>gsheet_synced_at</c> dùng <c>COALESCE(cũ, $at)</c> —
    /// GIỮ thời điểm ghi LẦN ĐẦU, không đè khi gọi lại để bổ sung file. <c>gsheet_file_url</c> dùng
    /// <c>COALESCE($url, cũ)</c> — <paramref name="fileUrl"/> null KHÔNG xóa link đã có (chỉ điền khi có link
    /// mới). <c>gsheet_da_huy</c> = <paramref name="daHuy"/>, <c>gsheet_da_co_van_don</c> =
    /// <paramref name="coVanDon"/>, <c>gsheet_da_co_uoc_tinh</c> = <paramref name="coUocTinh"/> và
    /// <c>gsheet_da_co_don_tra_hang</c> = <paramref name="coDonTraHang"/> GHI ĐÈ LUÔN
    /// (là trạng thái VỪA đẩy — để lần sau phát hiện đổi trạng thái hủy / vận đơn, số ước tính hoặc mã yêu cầu
    /// trả hàng vừa xuất hiện). <c>gsheet_tab</c> dùng <c>COALESCE(cũ, $tab)</c> — GIỮ tab đã ghi LẦN ĐẦU, KHÔNG
    /// đổi khi đẩy lại (đơn cập nhật luôn về đúng tab cũ dù tháng/override hiện tại đã khác). Khóa theo
    /// <c>(account_id, order_sn)</c>.
    /// <para>
    /// <b>CHỐT THẾ HỆ — CHỈ áp cho NHÓM CỜ, KHÔNG áp cho NHÓM DỮ LIỆU.</b> Hàm chạy HAI câu UPDATE trong một
    /// transaction, và việc tách đôi này là bắt buộc chứ không phải cho gọn:
    /// <list type="number">
    /// <item><b>Dữ liệu — LUÔN ghi:</b> <c>gsheet_tab</c> + <c>gsheet_file_url</c>. Hai cột này không phải "cờ đã
    /// đẩy" mà là SỰ THẬT vừa xảy ra ngoài đời: dòng đã nằm ở tab đó, file đã có link đó. Cả hai đều COALESCE
    /// (lần đầu thắng) nên ghi lại vô hại. Nhét chúng vào chung mệnh đề chốt thế hệ là tự tay dựng lại đúng hai
    /// lỗi mà <see cref="DatLaiCoDayLai"/> cố ý tránh: quên tab ⇒ <b>dòng bị ghi LẦN HAI ở tab tháng mới</b>
    /// (doanh thu đếm đôi, không sửa ngược được), quên link ⇒ upload lại phiếu đã có.</item>
    /// <item><b>Cờ đã đẩy — chốt thế hệ:</b> <c>gsheet_synced_at</c> + 4 cờ <c>gsheet_da_*</c>, chỉ đóng khi
    /// <c>gsheet_push_gen</c> CÒN BẰNG <paramref name="pushGen"/> (số đọc được lúc dựng lô, mang theo trong
    /// <c>GsheetPendingOrder.GsheetPushGen</c>). Người dùng bấm "Đẩy lại" giữa lúc một lượt đang bay thì
    /// <see cref="DatLaiCoDayLai"/> đã +1 thế hệ ⇒ lượt đang bay không đóng lại bộ cờ vừa mở. Cùng lớp lỗi
    /// "cờ đã đẩy kẹt" đã sửa hai lần: v1.6.3 và v1.7.16.</item>
    /// </list>
    /// </para>
    /// <returns>
    /// Số dòng ĐÓNG ĐƯỢC CỜ (0 hoặc 1). <b>0 = thế hệ đã lệch</b> (hoặc đơn không còn) ⇒ lượt đẩy này KHÔNG được
    /// coi đơn là xong. Caller BẮT BUỘC dùng giá trị này để quyết định <c>settled</c>: cộng vào <c>settled</c> khi
    /// cờ chưa đóng là đưa đơn vào diện DỌN với <c>gsheet_synced_at</c> vẫn NULL — đơn biến mất khỏi app và cú bấm
    /// "Đẩy lại" bốc hơi, đúng thứ chốt thế hệ sinh ra để chặn.
    /// </returns>
    public int MarkGsheetSynced(long accountId, string orderSn, string? fileUrl, bool daHuy, bool coVanDon, bool coUocTinh, bool coDonTraHang, string tab, DateTime at, long pushGen)
    {
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();

        // (1) NHÓM DỮ LIỆU — không chốt thế hệ (xem xmldoc).
        using (var duLieu = conn.CreateCommand())
        {
            duLieu.Transaction = tx;
            duLieu.CommandText = @"UPDATE orders SET
    gsheet_file_url = COALESCE($url, gsheet_file_url),
    gsheet_tab = COALESCE(gsheet_tab, $tab)
    WHERE account_id = $a AND order_sn = $sn;";
            duLieu.Parameters.AddWithValue("$url", (object?)fileUrl ?? DBNull.Value);
            duLieu.Parameters.AddWithValue("$tab", tab);
            duLieu.Parameters.AddWithValue("$a", accountId);
            duLieu.Parameters.AddWithValue("$sn", orderSn);
            duLieu.ExecuteNonQuery();
        }

        // (2) NHÓM CỜ ĐÃ ĐẨY — chốt thế hệ.
        int daDongCo;
        using (var co = conn.CreateCommand())
        {
            co.Transaction = tx;
            co.CommandText = @"UPDATE orders SET
    gsheet_synced_at = COALESCE(gsheet_synced_at, $at),
    gsheet_da_huy = $daHuy,
    gsheet_da_co_van_don = $co,
    gsheet_da_co_uoc_tinh = $coUt,
    gsheet_da_co_don_tra_hang = $coTh
    WHERE account_id = $a AND order_sn = $sn
      AND gsheet_push_gen = $gen;";
            co.Parameters.AddWithValue("$at", DbSerialization.FormatDate(at));
            co.Parameters.AddWithValue("$daHuy", daHuy ? 1 : 0);
            co.Parameters.AddWithValue("$co", coVanDon ? 1 : 0);
            co.Parameters.AddWithValue("$coUt", coUocTinh ? 1 : 0);
            co.Parameters.AddWithValue("$coTh", coDonTraHang ? 1 : 0);
            co.Parameters.AddWithValue("$a", accountId);
            co.Parameters.AddWithValue("$sn", orderSn);
            co.Parameters.AddWithValue("$gen", pushGen);
            daDongCo = co.ExecuteNonQuery();
        }

        tx.Commit();
        return daDongCo;
    }

    /// <summary>
    /// Map <c>order_sn → gsheet_tab</c> của các đơn CÒN trong app đã nhớ được tab (dùng cho lượt đẩy MÃ TRẢ HÀNG:
    /// mã của đơn còn sống thì về đúng tab đã ghi). Đơn đã bị DỌN không có ở đây — caller lùi về tab theo tháng
    /// hiện tại, mà tab chỉ là ĐIỂM VÀO vì script tra mã đơn trên MỌI tab.
    /// </summary>
    public Dictionary<string, string> GetGsheetTabs(long accountId)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT order_sn, gsheet_tab FROM orders WHERE account_id = $a AND gsheet_tab IS NOT NULL;";
        cmd.Parameters.AddWithValue("$a", accountId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1))
            {
                continue;
            }
            map[reader.GetString(0)] = reader.GetString(1);
        }
        return map;
    }

    /// <summary>
    /// SỐ đơn CHƯA từng ghi được dòng Google Sheet (<c>gsheet_synced_at IS NULL</c>) — dấu hiệu "còn hàng chờ ghi
    /// sheet" cho vòng chờ đẩy. Đây là ƯỚC LƯỢNG (việc chọn đơn nào GỬI do C# quyết — xem
    /// <c>HubOutbox.PushOrdersToGsheetAsync</c>): đơn hủy-chưa-có-vận-đơn không bao giờ được ghi nên vẫn nằm trong
    /// số này tới khi bị dọn. Chỉ dùng để quyết "có cần chạy lượt đẩy sheet không" + hiển thị, KHÔNG dùng làm nguồn đẩy.
    /// </summary>
    public int CountForGsheetPush(long accountId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM orders WHERE account_id = $a AND gsheet_synced_at IS NULL;";
        cmd.Parameters.AddWithValue("$a", accountId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
