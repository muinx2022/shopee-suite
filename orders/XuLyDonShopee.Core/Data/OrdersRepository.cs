using System.Text;
using Microsoft.Data.Sqlite;
using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Core.Data;

/// <summary>
/// DTO một đơn ỨNG VIÊN đẩy lên Google Sheet (đọc từ bảng <c>orders</c> qua
/// <see cref="OrdersRepository.GetForGsheetPush"/> — superset MỌI đơn của tài khoản; việc CHỌN đơn nào gửi
/// do <c>AccountSession</c> quyết bằng C#). <see cref="DaGhiSheet"/> = đã có <c>gsheet_synced_at</c> (đơn
/// từng được ghi dòng); <see cref="FileUrl"/> = <c>gsheet_file_url</c> đã lưu (null nếu chưa upload phiếu);
/// <see cref="GsheetDaHuy"/> = trạng thái hủy ĐÃ ĐẨY lần trước (0/1; null nếu chưa đẩy) — để phát hiện
/// trạng thái hủy thay đổi; <see cref="GsheetDaCoVanDon"/> = lần đẩy gần nhất có gửi mã vận đơn chưa (0/1;
/// null nếu chưa đẩy) — để tự điền cột B khi vận đơn xuất hiện sau. <see cref="Status"/>/
/// <see cref="StatusDescription"/>/<see cref="CancelReason"/> dùng phân loại hủy (<c>ShopeeShippingNav.LaDonHuy</c>).
/// <see cref="DaDemDaBan"/> = đã đếm "Đã bán" (<c>sold_counted_at IS NOT NULL</c>) và
/// <see cref="DaDayHub"/> = đã đẩy lên hub đơn hàng (<c>hub_synced_at IS NOT NULL</c>) — dùng để QUYẾT ĐỊNH có
/// được DỌN đơn kết thúc khỏi DB chưa (giữ lại đến khi mọi nghĩa vụ hoàn tất — xem <c>AccountSession.NenXoaDonKetThuc</c>).
/// <see cref="DaDayPhieuHub"/> = đã đẩy FILE PHIẾU lên hub (<c>hub_slip_synced_at IS NOT NULL</c>) — dùng để GIỮ
/// đơn kết thúc khi còn phiếu local hợp lệ CHƯA đẩy hub (hub đang bật).
/// </summary>
public sealed record GsheetPendingOrder(
    string OrderSn,
    string? TrackingNumber,
    string? Sku,
    long? TotalPrice,
    string? Status,
    string? StatusDescription,
    string? CancelReason,
    bool DaGhiSheet,
    string? FileUrl,
    long? GsheetDaHuy,
    long? GsheetDaCoVanDon,
    bool DaDemDaBan,
    bool DaDayHub,
    bool DaDayPhieuHub);

/// <summary>
/// Kết quả phát hiện đơn CHUYỂN sang "đã giao" giữa 2 lần sync (<see cref="OrdersRepository.DetectNewlyDelivered"/>),
/// dùng để +1 "Đã bán" theo SKU trên kho hub. Tách 3 nhóm để caller xử đúng thứ tự idempotent:
/// <list type="bullet">
/// <item><see cref="SkusToIncrement"/>: các SKU cần +1 lên hub (mỗi đơn chuyển-sang-đã-giao CÓ SKU đóng góp 1 phần
/// tử; đơn trùng SKU → SKU lặp → +N). Đơn không SKU KHÔNG nằm đây.</item>
/// <item><see cref="PendingMarkOrderSns"/>: các <c>order_sn</c> ứng với <see cref="SkusToIncrement"/> — chỉ đánh cờ
/// <c>sold_counted_at</c> SAU KHI hub +1 OK (kẻo hub lỗi thì mất đếm).</item>
/// <item><see cref="ImmediateMarkOrderSns"/>: các <c>order_sn</c> đánh cờ NGAY (KHÔNG +1) — gồm đơn grandfather
/// (đã-giao-sẵn: mới toanh đã delivered / đơn cũ status đã delivered) VÀ đơn chuyển-sang-đã-giao nhưng KHÔNG có SKU.</item>
/// </list>
/// </summary>
public sealed record SoldTransitionResult(
    IReadOnlyList<string> SkusToIncrement,
    IReadOnlyList<string> PendingMarkOrderSns,
    IReadOnlyList<string> ImmediateMarkOrderSns);

/// <summary>
/// Lưu/đọc đơn hàng đã sync trong bảng <c>orders</c>. Khóa nghiệp vụ là cặp
/// <c>(account_id, order_sn)</c> (UNIQUE) → mỗi đơn của một tài khoản chỉ một dòng; sync lại thì
/// CẬP NHẬT chứ không thêm trùng.
/// </summary>
public class OrdersRepository
{
    private readonly Database _db;

    public OrdersRepository(Database db) => _db = db;

    /// <summary>
    /// Upsert (thêm mới hoặc cập nhật) nhiều đơn của MỘT tài khoản trong một transaction. Đơn đã có
    /// (khớp <c>(account_id, order_sn)</c>) → cập nhật mọi cột dữ liệu + <c>updated_at</c>/<c>synced_at</c>,
    /// GIỮ <c>created_at</c>; đơn mới → thêm với <c>created_at = updated_at = synced_at</c>. Đơn không có
    /// mã (<see cref="SyncedOrder.OrderSn"/> rỗng) bị BỎ QUA (không thể làm khóa). Trả về số đơn thêm mới,
    /// số đơn cập nhật, và <c>InsertedOrders</c> — danh sách các đơn (chính các <see cref="SyncedOrder"/>
    /// đầu vào) được INSERT trong lượt này (đơn cập nhật KHÔNG có mặt) để tầng App báo "đơn MỚI" (Slack/
    /// Discord/Telegram) đúng những đơn vừa xuất hiện.
    /// </summary>
    public (int Inserted, int Updated, IReadOnlyList<SyncedOrder> InsertedOrders) UpsertMany(
        long accountId, IEnumerable<SyncedOrder> orders, DateTime syncedAt)
    {
        var syncedAtStr = DbSerialization.FormatDate(syncedAt);
        var inserted = 0;
        var updated = 0;
        var insertedOrders = new List<SyncedOrder>();

        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();

        foreach (var o in orders)
        {
            if (string.IsNullOrWhiteSpace(o.OrderSn))
            {
                continue; // không có mã đơn → không thể làm khóa upsert
            }

            // Có sẵn chưa? (khóa nghiệp vụ account_id + order_sn)
            long? existingId = null;
            using (var sel = conn.CreateCommand())
            {
                sel.Transaction = tx;
                sel.CommandText = "SELECT id FROM orders WHERE account_id = $account AND order_sn = $sn;";
                sel.Parameters.AddWithValue("$account", accountId);
                sel.Parameters.AddWithValue("$sn", o.OrderSn);
                var res = sel.ExecuteScalar();
                if (res is not null && res != DBNull.Value)
                {
                    existingId = (long)res;
                }
            }

            if (existingId is null)
            {
                using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = @"INSERT INTO orders
    (account_id, order_sn, shopee_order_id, buyer_username, items_json, item_count, item_summary, sku,
     total_price, total_price_text, final_amount, final_amount_text, payment_method, status, status_description, cancel_reason,
     channel, carrier, tracking_number, synced_at, created_at, updated_at)
    VALUES
    ($account, $sn, $shopeeId, $buyer, $items, $itemCount, $itemSummary, $sku,
     $totalPrice, $totalText, $finalAmount, $finalText, $payment, $status, $statusDesc, $cancelReason,
     $channel, $carrier, $tracking, $synced, $synced, $synced);";
                ins.Parameters.AddWithValue("$account", accountId);
                ins.Parameters.AddWithValue("$sn", o.OrderSn);
                BindData(ins, o);
                ins.Parameters.AddWithValue("$synced", syncedAtStr);
                ins.ExecuteNonQuery();
                inserted++;
                insertedOrders.Add(o); // đơn MỚI trong lượt này → App báo "đơn mới"
            }
            else
            {
                using var upd = conn.CreateCommand();
                upd.Transaction = tx;
                // final_amount/final_amount_text dùng COALESCE($moi, cot_cu): lần sync này KHÔNG lấy được (mở
                // chi tiết bị bỏ qua / đơn "Đã hủy" / lỗi) thì $finalAmount là NULL → GIỮ số đã lấy ở lần trước,
                // KHÔNG ghi đè NULL làm mất dữ liệu. Lần sau lấy được → cập nhật đè bình thường.
                upd.CommandText = @"UPDATE orders SET
    shopee_order_id = $shopeeId, buyer_username = $buyer, items_json = $items, item_count = $itemCount,
    item_summary = $itemSummary, sku = $sku,
    total_price = $totalPrice, total_price_text = $totalText,
    final_amount = COALESCE($finalAmount, final_amount),
    final_amount_text = COALESCE($finalText, final_amount_text),
    payment_method = $payment, status = $status, status_description = $statusDesc, cancel_reason = $cancelReason,
    channel = $channel, carrier = $carrier, tracking_number = $tracking,
    synced_at = $synced, updated_at = $synced
    WHERE id = $id;";
                BindData(upd, o);
                upd.Parameters.AddWithValue("$synced", syncedAtStr);
                upd.Parameters.AddWithValue("$id", existingId.Value);
                upd.ExecuteNonQuery();
                updated++;
            }
        }

        tx.Commit();
        return (inserted, updated, insertedOrders);
    }

    /// <summary>
    /// Tập <c>order_sn</c> của một tài khoản ĐÃ CÓ <c>final_amount</c> (khác NULL). App truyền tập này vào
    /// <c>SyncAllOrdersAsync</c> để BỎ QUA việc mở trang chi tiết lấy "Số tiền cuối cùng" cho đơn đã có —
    /// tối ưu tốc độ (lần đầu lâu, các lần sau nhanh). So khớp mã đơn theo <see cref="StringComparer.Ordinal"/>.
    /// </summary>
    public HashSet<string> GetOrderSnsWithFinalAmount(long accountId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT order_sn FROM orders WHERE account_id = $account AND final_amount IS NOT NULL;";
        cmd.Parameters.AddWithValue("$account", accountId);

        var set = new HashSet<string>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                set.Add(reader.GetString(0));
            }
        }
        return set;
    }

    /// <summary>
    /// Tập <c>order_sn</c> HIỆN CÓ trong DB của một tài khoản. App dùng để lọc INSERT lúc sync: đơn ĐÃ theo dõi
    /// (mã đã nằm trong tập này) luôn được cập nhật, đơn MỚI chỉ nhận khi ở trạng thái Chuẩn bị hàng. So khớp mã
    /// đơn theo <see cref="StringComparer.Ordinal"/>.
    /// </summary>
    public IReadOnlySet<string> GetOrderSns(long accountId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT order_sn FROM orders WHERE account_id = $account;";
        cmd.Parameters.AddWithValue("$account", accountId);

        var set = new HashSet<string>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                set.Add(reader.GetString(0));
            }
        }
        return set;
    }

    /// <summary>
    /// MỌI đơn của một tài khoản kèm <c>status</c> + <c>tracking_number</c> — để App lọc "đơn thiếu phiếu"
    /// (Chuẩn bị hàng + có vận đơn + file phiếu mất) rồi tải lại. Trả tuple gọn (KHÔNG cần cả hàng như
    /// <see cref="GetForGsheetPush"/>). Sắp theo id tăng (đơn cũ trước). Đơn không có mã đã không tồn tại trong DB.
    /// </summary>
    public IReadOnlyList<(string OrderSn, string? Status, string? TrackingNumber)> GetOrdersForSlipCheck(long accountId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT order_sn, status, tracking_number FROM orders WHERE account_id = $a ORDER BY id;";
        cmd.Parameters.AddWithValue("$a", accountId);

        var list = new List<(string, string?, string?)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0))
            {
                continue;
            }
            list.Add((
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }
        return list;
    }

    /// <summary>
    /// XÓA các đơn (theo <c>(account_id, order_sn)</c>) khỏi bảng <c>orders</c> trong MỘT transaction. Dùng để
    /// DỌN đơn KẾT THÚC (Đã giao / Đã hủy) khỏi app SAU khi mọi nghĩa vụ hoàn tất (GSheet đã ghi + "Đã bán" đã
    /// đếm + hub đã nhận). Trả về SỐ dòng thực xóa. Danh sách rỗng/null → trả 0 và KHÔNG mở connection. Đơn không
    /// có mã (rỗng) bị bỏ qua.
    /// </summary>
    public int DeleteOrders(long accountId, IReadOnlyCollection<string> orderSns)
    {
        if (orderSns is null || orderSns.Count == 0)
        {
            return 0;
        }

        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();
        var deleted = 0;
        foreach (var sn in orderSns)
        {
            if (string.IsNullOrWhiteSpace(sn))
            {
                continue;
            }
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM orders WHERE account_id = $a AND order_sn = $sn;";
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$sn", sn);
            deleted += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return deleted;
    }

    /// <summary>
    /// SUPERSET các đơn ỨNG VIÊN đẩy lên Google Sheet: <b>MỌI đơn của tài khoản</b> (KHÔNG lọc mã vận đơn nữa —
    /// đơn "Chờ lấy hàng" chưa có vận đơn vẫn cần ghi dòng TRẮNG), KÈM các cột trạng thái + cờ gsheet để
    /// <c>AccountSession</c> quyết bằng C# đơn nào cần gửi (mới / thiếu link phiếu / trạng thái hủy đổi / vận
    /// đơn vừa xuất hiện) và đơn nào bỏ qua (hủy mà chưa từng có vận đơn). Sắp theo id tăng (đơn cũ trước).
    /// </summary>
    public IReadOnlyList<GsheetPendingOrder> GetForGsheetPush(long accountId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT order_sn, tracking_number, sku, total_price,
       status, status_description, cancel_reason,
       gsheet_synced_at, gsheet_file_url, gsheet_da_huy, gsheet_da_co_van_don,
       sold_counted_at, hub_synced_at, hub_slip_synced_at
    FROM orders
    WHERE account_id = $a
    ORDER BY id;";
        cmd.Parameters.AddWithValue("$a", accountId);

        var list = new List<GsheetPendingOrder>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new GsheetPendingOrder(
                OrderSn: reader.GetString(0),
                TrackingNumber: reader.IsDBNull(1) ? null : reader.GetString(1),
                Sku: reader.IsDBNull(2) ? null : reader.GetString(2),
                TotalPrice: reader.IsDBNull(3) ? null : reader.GetInt64(3),
                Status: reader.IsDBNull(4) ? null : reader.GetString(4),
                StatusDescription: reader.IsDBNull(5) ? null : reader.GetString(5),
                CancelReason: reader.IsDBNull(6) ? null : reader.GetString(6),
                DaGhiSheet: !reader.IsDBNull(7),
                FileUrl: reader.IsDBNull(8) ? null : reader.GetString(8),
                GsheetDaHuy: reader.IsDBNull(9) ? null : reader.GetInt64(9),
                GsheetDaCoVanDon: reader.IsDBNull(10) ? null : reader.GetInt64(10),
                DaDemDaBan: !reader.IsDBNull(11),
                DaDayHub: !reader.IsDBNull(12),
                DaDayPhieuHub: !reader.IsDBNull(13)));
        }
        return list;
    }

    /// <summary>
    /// Đánh dấu một đơn ĐÃ ghi lên Google Sheet. <c>gsheet_synced_at</c> dùng <c>COALESCE(cũ, $at)</c> —
    /// GIỮ thời điểm ghi LẦN ĐẦU, không đè khi gọi lại để bổ sung file. <c>gsheet_file_url</c> dùng
    /// <c>COALESCE($url, cũ)</c> — <paramref name="fileUrl"/> null KHÔNG xóa link đã có (chỉ điền khi có link
    /// mới). <c>gsheet_da_huy</c> = <paramref name="daHuy"/> và <c>gsheet_da_co_van_don</c> =
    /// <paramref name="coVanDon"/> GHI ĐÈ LUÔN (là trạng thái VỪA đẩy — để lần sau phát hiện đổi trạng thái hủy /
    /// vận đơn vừa xuất hiện). Khóa theo <c>(account_id, order_sn)</c>.
    /// </summary>
    public void MarkGsheetSynced(long accountId, string orderSn, string? fileUrl, bool daHuy, bool coVanDon, DateTime at)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE orders SET
    gsheet_synced_at = COALESCE(gsheet_synced_at, $at),
    gsheet_file_url = COALESCE($url, gsheet_file_url),
    gsheet_da_huy = $daHuy,
    gsheet_da_co_van_don = $co
    WHERE account_id = $a AND order_sn = $sn;";
        cmd.Parameters.AddWithValue("$at", DbSerialization.FormatDate(at));
        cmd.Parameters.AddWithValue("$url", (object?)fileUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$daHuy", daHuy ? 1 : 0);
        cmd.Parameters.AddWithValue("$co", coVanDon ? 1 : 0);
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$sn", orderSn);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Các đơn ỨNG VIÊN đẩy lên HUB đơn hàng: đơn của tài khoản CHƯA từng đẩy hub thành công
    /// (<c>hub_synced_at IS NULL</c>) — dựng lại <see cref="SyncedOrder"/> đầy đủ từ các cột bảng để client map
    /// 1-1 sang DTO hub (mẫu <see cref="GetForGsheetPush"/>). NULL = còn trong hàng đợi ngầm → hub offline thì
    /// lượt sync sau tự đẩy bù. Sắp theo id tăng (đơn cũ trước) để đẩy đúng thứ tự xuất hiện.
    /// </summary>
    public IReadOnlyList<SyncedOrder> GetForHubPush(long accountId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT order_sn, shopee_order_id, buyer_username, items_json, item_count, item_summary, sku,
       total_price, total_price_text, final_amount, final_amount_text, payment_method,
       status, status_description, cancel_reason, channel, carrier, tracking_number
    FROM orders
    WHERE account_id = $a AND hub_synced_at IS NULL
    ORDER BY id;";
        cmd.Parameters.AddWithValue("$a", accountId);

        var list = new List<SyncedOrder>();
        using var reader = cmd.ExecuteReader();
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
            });
        }
        return list;
    }

    /// <summary>
    /// Đánh dấu các đơn ĐÃ được hub nhận OK (chống đẩy trùng lượt sync sau). <c>hub_synced_at</c> dùng
    /// <c>COALESCE(cũ, $at)</c> — GIỮ thời điểm đẩy LẦN ĐẦU, không đè khi gọi lại. Khóa theo
    /// <c>(account_id, order_sn)</c>; đơn không có mã (rỗng) bị bỏ qua. Cập nhật nhiều đơn trong một transaction.
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
    hub_synced_at = COALESCE(hub_synced_at, $at)
    WHERE account_id = $a AND order_sn = $sn;";
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
    /// Phát hiện đơn CHUYỂN sang "đã giao" (để +1 "Đã bán" theo SKU trên hub), <b>KHÔNG đếm bù</b> (no backfill).
    /// <b>PHẢI gọi TRƯỚC <see cref="UpsertMany"/></b> của cùng lượt sync — đọc trạng thái CŨ trong DB (cột
    /// <c>status</c>) trước khi UpsertMany ghi đè; chạy tuần tự cùng thread nên tương đương "cùng transaction"
    /// (mỗi tài khoản một phiên, không có ghi đồng thời). Với mỗi đơn scan có <see cref="ShopeeShippingNav.LaDaGiaoDaBan"/>:
    /// <list type="bullet">
    /// <item>Đã tồn tại trong DB + <c>sold_counted_at</c> ĐÃ set → bỏ qua (đã đếm, idempotent).</item>
    /// <item>Đã tồn tại + cờ NULL + status CŨ KHÔNG delivered → <b>chuyển sang đã-giao</b>: có SKU → gom SKU vào
    /// <see cref="SoldTransitionResult.SkusToIncrement"/> + mã đơn vào <see cref="SoldTransitionResult.PendingMarkOrderSns"/>
    /// (đánh cờ SAU hub +1 OK); không SKU → chỉ đánh cờ NGAY (ImmediateMark, không +1 được).</item>
    /// <item>Đã tồn tại + cờ NULL + status CŨ ĐÃ delivered (đơn cũ có từ trước tính năng) → <b>grandfather</b>:
    /// ImmediateMark, KHÔNG +1.</item>
    /// <item>MỚI toanh (chưa có trong DB) + đã delivered ngay → <b>grandfather</b>: ImmediateMark, KHÔNG +1.</item>
    /// </list>
    /// Đơn không có mã / trùng mã trong lô → bỏ qua (không thể làm khóa / tránh xử lý trùng).
    /// </summary>
    public SoldTransitionResult DetectNewlyDelivered(long accountId, IEnumerable<SyncedOrder> scanned)
    {
        // Trạng thái + cờ đếm HIỆN TẠI trong DB (trước upsert) cho account này: order_sn → (status cũ, đã-đếm-chưa).
        var existing = new Dictionary<string, (string? Status, bool Counted)>(StringComparer.Ordinal);
        using (var conn = _db.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT order_sn, status, sold_counted_at FROM orders WHERE account_id = $a;";
            cmd.Parameters.AddWithValue("$a", accountId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var sn = reader.GetString(0);
                var status = reader.IsDBNull(1) ? null : reader.GetString(1);
                var counted = !reader.IsDBNull(2);
                existing[sn] = (status, counted);
            }
        }

        var skus = new List<string>();
        var pendingMark = new List<string>();
        var immediateMark = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal); // 1 mã đơn xử 1 lần dù lô có trùng

        foreach (var o in scanned)
        {
            if (string.IsNullOrWhiteSpace(o.OrderSn) || !seen.Add(o.OrderSn))
            {
                continue;
            }
            if (!ShopeeShippingNav.LaDaGiaoDaBan(o.Status))
            {
                continue; // trạng thái MỚI không delivered → không phải "đã bán"
            }

            if (existing.TryGetValue(o.OrderSn, out var e))
            {
                if (e.Counted)
                {
                    continue; // đã đếm rồi (cờ set) → bỏ qua
                }
                if (ShopeeShippingNav.LaDaGiaoDaBan(e.Status))
                {
                    // status CŨ đã delivered (đơn cũ từ trước tính năng) → grandfather, KHÔNG +1.
                    immediateMark.Add(o.OrderSn);
                }
                else
                {
                    // Chuyển chưa-giao → đã-giao. Có SKU → +1 (đánh cờ sau hub OK); không SKU → đánh cờ ngay.
                    var sku = o.Sku?.Trim();
                    if (!string.IsNullOrEmpty(sku))
                    {
                        skus.Add(sku);
                        pendingMark.Add(o.OrderSn);
                    }
                    else
                    {
                        immediateMark.Add(o.OrderSn);
                    }
                }
            }
            else
            {
                // Đơn mới toanh, đã delivered ngay lần đầu thấy → grandfather, KHÔNG +1.
                immediateMark.Add(o.OrderSn);
            }
        }

        return new SoldTransitionResult(skus, pendingMark, immediateMark);
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
    /// </list>
    /// Sắp xếp đơn sync mới nhất lên đầu.
    /// </summary>
    public List<OrderRow> Query(long? accountId = null, string? status = null, string? searchText = null,
        IReadOnlyCollection<long>? accountIds = null, int? limit = null, int? offset = null)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();

        var sql = new StringBuilder(@"SELECT id, account_id, order_sn, buyer_username, item_count, item_summary, sku,
    total_price, total_price_text, final_amount, final_amount_text, payment_method, status, status_description, cancel_reason,
    channel, carrier, tracking_number, synced_at
    FROM orders WHERE 1 = 1");

        if (!AppendFilter(cmd, sql, accountId, status, searchText, accountIds))
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
        IReadOnlyCollection<long>? accountIds = null)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();

        var sql = new StringBuilder("SELECT COUNT(*) FROM orders WHERE 1 = 1");
        if (!AppendFilter(cmd, sql, accountId, status, searchText, accountIds))
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
    /// </summary>
    private static bool AppendFilter(SqliteCommand cmd, StringBuilder sql,
        long? accountId, string? status, string? searchText, IReadOnlyCollection<long>? accountIds)
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

        return true;
    }

    /// <summary>
    /// Danh sách trạng thái PHÂN BIỆT (khác null/rỗng) đang có trong bảng — nạp ComboBox lọc. Có thể giới
    /// hạn theo <paramref name="accountId"/>. Sắp xếp tăng dần.
    /// </summary>
    public List<string> AllStatuses(long? accountId = null)
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
    };

    /// <summary>Gắn các cột DỮ LIỆU (không gồm account_id/order_sn/khóa/thời gian) vào lệnh. Null → DBNull.</summary>
    private static void BindData(SqliteCommand cmd, SyncedOrder o)
    {
        cmd.Parameters.AddWithValue("$shopeeId", (object?)o.ShopeeOrderId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$buyer", (object?)o.BuyerUsername ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$items", (object?)o.ItemsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$itemCount", o.ItemCount);
        cmd.Parameters.AddWithValue("$itemSummary", (object?)o.ItemSummary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sku", (object?)o.Sku ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$totalPrice", (object?)o.TotalPrice ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$totalText", (object?)o.TotalPriceText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$finalAmount", (object?)o.FinalAmount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$finalText", (object?)o.FinalAmountText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$payment", (object?)o.PaymentMethod ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", (object?)o.Status ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$statusDesc", (object?)o.StatusDescription ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cancelReason", (object?)o.CancelReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$channel", (object?)o.Channel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$carrier", (object?)o.Carrier ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tracking", (object?)o.TrackingNumber ?? DBNull.Value);
    }
}
