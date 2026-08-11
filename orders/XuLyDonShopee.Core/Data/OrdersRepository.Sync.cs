using Microsoft.Data.Sqlite;
using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Core.Data;

/// <summary>Phần OrdersRepository: mảng SYNC — upsert đơn của một lượt quét, các tập mã đơn dùng để lọc
/// lúc sync, phát hiện đơn vừa chuyển "đã giao", và helper gắn cột dữ liệu vào lệnh.</summary>
public partial class OrdersRepository
{
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
        long accountId, IEnumerable<SyncedOrder> orders, DateTime syncedAt, string? shopId = null, string? shopLogin = null)
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

            // Có sẵn chưa? (khóa nghiệp vụ account_id + order_sn). Lấy luôn items_json + item_count ĐANG lưu:
            // nhánh UPDATE phải giữ được bản sản phẩm GIÀU hơn (xem chỗ dùng $items bên dưới).
            long? existingId = null;
            string? itemsCu = null;
            var itemCountCu = 0;
            using (var sel = conn.CreateCommand())
            {
                sel.Transaction = tx;
                sel.CommandText = "SELECT id, items_json, item_count FROM orders WHERE account_id = $account AND order_sn = $sn;";
                sel.Parameters.AddWithValue("$account", accountId);
                sel.Parameters.AddWithValue("$sn", o.OrderSn);
                using var res = sel.ExecuteReader();
                if (res.Read())
                {
                    existingId = res.GetInt64(0);
                    itemsCu = res.IsDBNull(1) ? null : res.GetString(1);
                    itemCountCu = res.IsDBNull(2) ? 0 : res.GetInt32(2);
                }
            }

            if (existingId is null)
            {
                using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = @"INSERT INTO orders
    (account_id, shop_id, shop_login, order_sn, shopee_order_id, buyer_username, items_json, item_count, item_summary, sku,
     total_price, total_price_text, final_amount, final_amount_text, payment_method, status, status_description, cancel_reason,
     channel, carrier, tracking_number, synced_at, created_at, updated_at)
    VALUES
    ($account, $shopId, $shopLogin, $sn, $shopeeId, $buyer, $items, $itemCount, $itemSummary, $sku,
     $totalPrice, $totalText, $finalAmount, $finalText, $payment, $status, $statusDesc, $cancelReason,
     $channel, $carrier, $tracking, $synced, $synced, $synced);";
                ins.Parameters.AddWithValue("$account", accountId);
                ins.Parameters.AddWithValue("$shopId", (object?)shopId ?? DBNull.Value);
                ins.Parameters.AddWithValue("$shopLogin", (object?)shopLogin ?? DBNull.Value);
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
                // shop_id dùng COALESCE($shopId, shop_id): lượt này không truyền shop (null) thì GIỮ shop đã gắn,
                // KHÔNG xóa. Đơn thuộc đúng MỘT shop nên gắn lại cùng giá trị là vô hại. shop_login mirror y hệt.
                // tracking_number dùng COALESCE($tracking, tracking_number): lượt sync này KHÔNG đọc được mã vận đơn
                // (đơn "Đã hủy" nên danh sách không hiện cột, hoặc lỗi đọc) thì GIỮ mã đã có, KHÔNG xóa về NULL —
                // mất vận đơn kéo theo đơn hủy rơi vào nhánh BỎ QUA của GSheet (không tô đỏ) và hub mất dữ liệu.
                // items_json/item_count: KHÔNG COALESCE được (bản nghèo vẫn khác NULL) nên chọn bằng C# ngay dưới —
                // bản quét trang DANH SÁCH ({name,variation,amount,image}) tuyệt đối không được đè bản đọc ở trang
                // CHI TIẾT (thêm sku/phanLoai/donGia/thanhTien): trang chi tiết chỉ mở lại cho đơn THIẾU ước tính
                // nên đơn đã có ước tính mà bị đè là mất SKU/phân loại VĨNH VIỄN.
                // hub_push_gen: +1 ĐÚNG các điều kiện reset cờ bên dưới — đơn bị đổi trong lúc một lô đang bay lên
                // hub sẽ có "thế hệ" mới hơn thế hệ đã chụp, nên MarkHubSynced KHÔNG đóng cờ oan (xem MarkHubSynced).
                // hub_synced_at: RESET về NULL để lượt đẩy hub kế đẩy LẠI đơn kèm dữ liệu mới, khi một trong các
                // điều kiện sau đúng (hub chỉ lấy đơn hub_synced_at IS NULL — KHÔNG có re-push "vận đơn mới" như
                // GSheet; hub UpsertOrders idempotent nên đẩy lại chỉ cập nhật). Trong UPDATE của SQLite, cột ở vế
                // phải SET là giá trị CŨ → so cũ-với-tham-số-mới là chuẩn:
                //  - mã vận đơn HOẶC "Số tiền cuối cùng" XUẤT HIỆN hoặc ĐỔI GIÁ TRỊ (T5, review 11/08: bản trước
                //    chỉ bắt NULL→có; Shopee đổi mã vận đơn A→B / điều chỉnh số tiền thì local nhận giá trị mới mà
                //    hub/sheet giữ giá trị CŨ vĩnh viễn sau khi đơn bị dọn). final_amount PHẢI có nhánh riêng: đơn
                //    thường lên hub NGAY lượt sync đầu (chưa mở trang chi tiết → chưa có số tiền cuối cùng).
                //  - TRẠNG THÁI đơn đổi (status hoặc cancel_reason): đơn đã đẩy một lần rồi chuyển "Đã hủy"/"Đã giao"
                //    mà không reset cờ thì hub kẹt trạng thái CŨ VĨNH VIỄN (đơn kết thúc sau đó bị dọn khỏi client
                //    nên không còn đường sửa). CHỈ so status + cancel_reason: status_description hay dao động (đếm
                //    ngược, nhắc nhở…) nên so nó sẽ đẩy lại hub mỗi lượt sync, gây tải vô ích.
                // gsheet_da_co_van_don: về NULL ("chưa gửi kèm" — đúng quy ước reset ở DatLaiCoDayLai) khi mã vận
                // đơn xuất hiện/đổi, để đường re-push "vận đơn mới" của GSheet ăn theo giá trị MỚI. Kèm
                // gsheet_push_gen + 1 — bất biến "MỞ CỜ NÀO THÌ +1 THẾ HỆ CỦA ĐÍCH ĐÓ" (xem DatLaiCoDayLai):
                // lượt đẩy sheet fire-and-forget chạy SONG SONG với vòng shop, thiếu vế này thì lô đang bay gọi
                // MarkGsheetSynced với thế hệ CŨ vẫn khớp và đóng lại đúng cái cờ vừa mở ⇒ vận đơn MỚI không bao
                // giờ lên sheet (phản biện đợt T1–T12, 11/08).
                upd.CommandText = @"UPDATE orders SET
    shop_id = COALESCE($shopId, shop_id),
    shop_login = COALESCE($shopLogin, shop_login),
    shopee_order_id = $shopeeId, buyer_username = $buyer, items_json = $items, item_count = $itemCount,
    item_summary = $itemSummary, sku = $sku,
    total_price = $totalPrice, total_price_text = $totalText,
    final_amount = COALESCE($finalAmount, final_amount),
    final_amount_text = COALESCE($finalText, final_amount_text),
    payment_method = $payment, status = $status, status_description = $statusDesc, cancel_reason = $cancelReason,
    channel = $channel, carrier = $carrier,
    gsheet_da_co_van_don = CASE WHEN ($tracking IS NOT NULL AND (tracking_number IS NULL OR tracking_number <> $tracking))
                                THEN NULL ELSE gsheet_da_co_van_don END,
    gsheet_push_gen = CASE WHEN ($tracking IS NOT NULL AND (tracking_number IS NULL OR tracking_number <> $tracking))
                           THEN gsheet_push_gen + 1 ELSE gsheet_push_gen END,
    hub_synced_at = CASE WHEN ($tracking IS NOT NULL AND (tracking_number IS NULL OR tracking_number <> $tracking))
                           OR ($finalAmount IS NOT NULL AND (final_amount IS NULL OR final_amount <> $finalAmount))
                           OR (COALESCE(status, '') <> COALESCE($status, ''))
                           OR (COALESCE(cancel_reason, '') <> COALESCE($cancelReason, ''))
                         THEN NULL ELSE hub_synced_at END,
    hub_push_gen = CASE WHEN ($tracking IS NOT NULL AND (tracking_number IS NULL OR tracking_number <> $tracking))
                          OR ($finalAmount IS NOT NULL AND (final_amount IS NULL OR final_amount <> $finalAmount))
                          OR (COALESCE(status, '') <> COALESCE($status, ''))
                          OR (COALESCE(cancel_reason, '') <> COALESCE($cancelReason, ''))
                        THEN hub_push_gen + 1 ELSE hub_push_gen END,
    tracking_number = COALESCE($tracking, tracking_number),
    synced_at = $synced, updated_at = $synced
    WHERE id = $id;";
                upd.Parameters.AddWithValue("$shopId", (object?)shopId ?? DBNull.Value);
                upd.Parameters.AddWithValue("$shopLogin", (object?)shopLogin ?? DBNull.Value);
                BindData(upd, o);
                // GIỮ bản sản phẩm giàu hơn; item_count phải ĐI THEO bản được giữ, kẻo còn lại con số nói dối.
                var itemsGiu = SanPhamDonParser.ChonItemsJson(itemsCu, o.ItemsJson);
                if (!string.Equals(itemsGiu, o.ItemsJson, StringComparison.Ordinal))
                {
                    upd.Parameters["$items"].Value = (object?)itemsGiu ?? DBNull.Value;
                    upd.Parameters["$itemCount"].Value = itemCountCu;
                }
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
