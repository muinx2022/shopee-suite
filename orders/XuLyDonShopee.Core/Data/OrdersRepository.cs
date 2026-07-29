using System.Globalization;
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
/// null nếu chưa đẩy) — để tự điền cột B khi vận đơn xuất hiện sau; <see cref="FinalAmount"/> =
/// "Số tiền cuối cùng" (<c>final_amount</c>, cột "Ước tính") — SỐ TIỀN đẩy lên sheet (null = chưa mở trang chi
/// tiết) và <see cref="GsheetDaCoUocTinh"/> = lần đẩy gần nhất có gửi kèm số ước tính chưa (0/1; null nếu chưa
/// đẩy) — để tự đẩy lại ghi đè đúng số khi ước tính xuất hiện sau. <see cref="Status"/>/
/// <see cref="StatusDescription"/>/<see cref="CancelReason"/> dùng phân loại hủy (<c>ShopeeShippingNav.LaDonHuy</c>).
/// <see cref="DaDemDaBan"/> = đã đếm "Đã bán" (<c>sold_counted_at IS NOT NULL</c>) và
/// <see cref="DaDayHub"/> = đã đẩy lên hub đơn hàng (<c>hub_synced_at IS NOT NULL</c>) — dùng để QUYẾT ĐỊNH có
/// được DỌN đơn kết thúc khỏi DB chưa (giữ lại đến khi mọi nghĩa vụ hoàn tất — xem <c>AccountSession.NenXoaDonKetThuc</c>).
/// <see cref="DaDayPhieuHub"/> = đã đẩy FILE PHIẾU lên hub (<c>hub_slip_synced_at IS NOT NULL</c>) — dùng để GIỮ
/// đơn kết thúc khi còn phiếu local hợp lệ CHƯA đẩy hub (hub đang bật).
/// <see cref="GsheetTab"/> = tab (sheet) đã ghi LẦN ĐẦU của đơn (<c>gsheet_tab</c>; null = chưa ghi/chưa nhớ) —
/// đơn đẩy LẠI phải về đúng tab này (không nhân đôi dòng khi tab đổi theo tháng).
/// <see cref="ItemsJson"/> = mảng sản phẩm đã quét (<c>items_json</c>) — nguồn suy cột "Phân loại" gửi lên sheet
/// (<c>XuLyDonShopee.Core.Services.PhanLoaiExtractor</c>); KHÔNG có cột "phân loại" riêng trong DB.
/// <see cref="ReturnRequestCode"/> = mã yêu cầu trả hàng khớp đơn (<c>return_request_code</c>; null = đơn chưa có
/// yêu cầu trả hàng) và <see cref="GsheetDaCoDonTraHang"/> = lần đẩy gần nhất có gửi kèm mã đó chưa (0/1; null =
/// chưa đẩy) — mẫu y hệt cặp vận đơn, để mã xuất hiện SAU vẫn được đẩy lại điền vào ô.
/// </summary>
public sealed record GsheetPendingOrder(
    string OrderSn,
    string? TrackingNumber,
    string? Sku,
    string? ItemsJson,
    long? TotalPrice,
    long? FinalAmount,
    string? Status,
    string? StatusDescription,
    string? CancelReason,
    bool DaGhiSheet,
    string? FileUrl,
    long? GsheetDaHuy,
    long? GsheetDaCoVanDon,
    long? GsheetDaCoUocTinh,
    bool DaDemDaBan,
    bool DaDayHub,
    bool DaDayPhieuHub,
    string? GsheetTab,
    string? ReturnRequestCode,
    long? GsheetDaCoDonTraHang);

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
                // hub_synced_at: RESET về NULL để lượt đẩy hub kế đẩy LẠI đơn kèm dữ liệu mới, khi một trong các
                // điều kiện sau đúng (hub chỉ lấy đơn hub_synced_at IS NULL — KHÔNG có re-push "vận đơn mới" như
                // GSheet; hub UpsertOrders idempotent nên đẩy lại chỉ cập nhật). Trong UPDATE của SQLite, cột ở vế
                // phải SET là giá trị CŨ → so cũ-với-tham-số-mới là chuẩn:
                //  - mã vận đơn HOẶC "Số tiền cuối cùng" VỪA xuất hiện (cột CŨ NULL → tham số MỚI có). final_amount
                //    PHẢI có nhánh riêng: đơn thường lên hub NGAY lượt sync đầu (chưa mở trang chi tiết → chưa có số
                //    tiền cuối cùng); lấy được ở lượt sau mà không reset cờ thì hub hiển thị "—" VĨNH VIỄN.
                //  - TRẠNG THÁI đơn đổi (status hoặc cancel_reason): đơn đã đẩy một lần rồi chuyển "Đã hủy"/"Đã giao"
                //    mà không reset cờ thì hub kẹt trạng thái CŨ VĨNH VIỄN (đơn kết thúc sau đó bị dọn khỏi client
                //    nên không còn đường sửa). CHỈ so status + cancel_reason: status_description hay dao động (đếm
                //    ngược, nhắc nhở…) nên so nó sẽ đẩy lại hub mỗi lượt sync, gây tải vô ích.
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
    tracking_number = COALESCE($tracking, tracking_number),
    hub_synced_at = CASE WHEN (tracking_number IS NULL AND $tracking IS NOT NULL)
                           OR (final_amount IS NULL AND $finalAmount IS NOT NULL)
                           OR (COALESCE(status, '') <> COALESCE($status, ''))
                           OR (COALESCE(cancel_reason, '') <> COALESCE($cancelReason, ''))
                         THEN NULL ELSE hub_synced_at END,
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
    public IReadOnlyList<GsheetPendingOrder> GetForGsheetPush(long accountId, string? shopId = null)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        // Lọc theo shop khi có shopId (mô hình nhiều-shop: chỉ đẩy đơn CỦA shop hiện tại, TenShop lấy theo shop đó
        // — không đẩy nhầm tên shop). shopId null → hành vi CŨ (mọi đơn của account).
        var shopFilter = string.IsNullOrEmpty(shopId) ? string.Empty : " AND shop_id = $shopId";
        // items_json + cặp cột "đơn trả hàng" thêm ở CUỐI danh sách cột để KHÔNG lệch chỉ số reader.Get*(i) sẵn có.
        cmd.CommandText = @"SELECT order_sn, tracking_number, sku, total_price, final_amount,
       status, status_description, cancel_reason,
       gsheet_synced_at, gsheet_file_url, gsheet_da_huy, gsheet_da_co_van_don, gsheet_da_co_uoc_tinh,
       sold_counted_at, hub_synced_at, hub_slip_synced_at, gsheet_tab, items_json,
       return_request_code, gsheet_da_co_don_tra_hang
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
                FileUrl: reader.IsDBNull(9) ? null : reader.GetString(9),
                GsheetDaHuy: reader.IsDBNull(10) ? null : reader.GetInt64(10),
                GsheetDaCoVanDon: reader.IsDBNull(11) ? null : reader.GetInt64(11),
                GsheetDaCoUocTinh: reader.IsDBNull(12) ? null : reader.GetInt64(12),
                DaDemDaBan: !reader.IsDBNull(13),
                DaDayHub: !reader.IsDBNull(14),
                DaDayPhieuHub: !reader.IsDBNull(15),
                GsheetTab: reader.IsDBNull(16) ? null : reader.GetString(16),
                ReturnRequestCode: reader.IsDBNull(18) ? null : reader.GetString(18),
                GsheetDaCoDonTraHang: reader.IsDBNull(19) ? null : reader.GetInt64(19)));
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
    /// </summary>
    public void MarkGsheetSynced(long accountId, string orderSn, string? fileUrl, bool daHuy, bool coVanDon, bool coUocTinh, bool coDonTraHang, string tab, DateTime at)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE orders SET
    gsheet_synced_at = COALESCE(gsheet_synced_at, $at),
    gsheet_file_url = COALESCE($url, gsheet_file_url),
    gsheet_da_huy = $daHuy,
    gsheet_da_co_van_don = $co,
    gsheet_da_co_uoc_tinh = $coUt,
    gsheet_da_co_don_tra_hang = $coTh,
    gsheet_tab = COALESCE(gsheet_tab, $tab)
    WHERE account_id = $a AND order_sn = $sn;";
        cmd.Parameters.AddWithValue("$at", DbSerialization.FormatDate(at));
        cmd.Parameters.AddWithValue("$url", (object?)fileUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$daHuy", daHuy ? 1 : 0);
        cmd.Parameters.AddWithValue("$co", coVanDon ? 1 : 0);
        cmd.Parameters.AddWithValue("$coUt", coUocTinh ? 1 : 0);
        cmd.Parameters.AddWithValue("$coTh", coDonTraHang ? 1 : 0);
        cmd.Parameters.AddWithValue("$tab", tab);
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$sn", orderSn);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Đánh dấu một đơn ĐÃ "chuẩn bị hàng" xong (arrange) lúc <paramref name="atUtc"/> — phiên cầu nối gọi ngay
    /// sau mỗi đơn. <c>prepared_at</c> dùng <c>COALESCE(prepared_at, $at)</c>: chỉ ghi LẦN ĐẦU, arrange lại /
    /// chạy lại KHÔNG dời thời điểm sang hôm khác (hub nhóm đếm theo NGÀY). <c>hub_synced_at</c> RESET về NULL để
    /// lượt đẩy hub kế mang <c>prepared_at</c> lên (hub chỉ lấy đơn <c>hub_synced_at IS NULL</c>). Khóa theo
    /// <c>(account_id, order_sn)</c>; mã đơn rỗng → bỏ qua, mã đơn không có trong DB → 0 dòng đổi (KHÔNG ném).
    /// </summary>
    public void MarkPrepared(long accountId, string orderSn, DateTime atUtc)
    {
        if (string.IsNullOrWhiteSpace(orderSn))
        {
            return;
        }

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE orders SET
    prepared_at = COALESCE(prepared_at, $at),
    hub_synced_at = NULL
    WHERE account_id = $a AND order_sn = $sn;";
        cmd.Parameters.AddWithValue("$at", DbSerialization.FormatDate(atUtc));
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$sn", orderSn);
        cmd.ExecuteNonQuery();
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

    /// <summary>Kết quả một lượt <see cref="SetReturnRequestCodes"/> — để log / notify đúng cặp vừa ghi.</summary>
    /// <param name="DaGhi">Số đơn VỪA ghi mã mới (khác mã cũ).</param>
    /// <param name="KhongDoi">Số đơn đã mang đúng mã đó rồi (không ghi lại, không đẩy lại).</param>
    /// <param name="KhongCoDon">Số mã yêu cầu KHÔNG khớp đơn nào trong DB (đơn cũ hơn thời gian giữ / shop khác).</param>
    /// <param name="CapDaGhi">Các cặp (order_sn, mã yêu cầu) vừa ghi thành công — dùng notify, không gửi cả list check.</param>
    public sealed record ReturnCodeSaveResult(
        int DaGhi,
        int KhongDoi,
        int KhongCoDon,
        IReadOnlyList<(string OrderSn, string Code)> CapDaGhi);

    /// <summary>
    /// Lưu MÃ YÊU CẦU TRẢ HÀNG vào đơn theo <c>order_sn</c> (bước check đơn trả hàng, cuối flow shop).
    /// <list type="bullet">
    /// <item>GHI ĐÈ khi mã khác mã đang có (yêu cầu trả hàng có thể được tạo lại), nhưng KHÔNG bao giờ ghi đè bằng
    /// RỖNG — cặp có mã rỗng bị bỏ qua.</item>
    /// <item>Mã KHÔNG đổi → không chạm dòng (khỏi đẩy lại GSheet/hub vô ích).</item>
    /// <item>Mã đơn không có trong DB → đếm vào <see cref="ReturnCodeSaveResult.KhongCoDon"/> để caller LOG; TUYỆT
    /// ĐỐI không tạo đơn mới (đơn đã bị dọn theo vòng đời, insert lại sẽ lặp ghi-xóa).</item>
    /// </list>
    /// Khi mã ĐỔI: <c>hub_synced_at</c> RESET về NULL và <c>gsheet_da_co_don_tra_hang</c> RESET về NULL để lượt đẩy
    /// KẾ mang mã mới lên hub + Google Sheet (đúng cơ chế cờ sẵn có của vận đơn/ước tính, không đẻ cơ chế mới).
    /// Cập nhật nhiều đơn trong một transaction (mẫu <see cref="MarkHubSynced"/>).
    /// </summary>
    public ReturnCodeSaveResult SetReturnRequestCodes(long accountId, IEnumerable<(string OrderSn, string Code)> pairs)
    {
        int daGhi = 0, khongDoi = 0, khongCoDon = 0;
        var capDaGhi = new List<(string OrderSn, string Code)>();
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();

        foreach (var (sn, code) in pairs ?? Array.Empty<(string, string)>())
        {
            if (string.IsNullOrWhiteSpace(sn) || string.IsNullOrWhiteSpace(code))
            {
                continue; // không có khóa / mã rỗng → bỏ (không ghi đè bằng rỗng)
            }

            var snTrim = sn.Trim();
            var codeTrim = code.Trim();

            using (var sel = conn.CreateCommand())
            {
                sel.Transaction = tx;
                sel.CommandText = "SELECT 1 FROM orders WHERE account_id = $a AND order_sn = $sn;";
                sel.Parameters.AddWithValue("$a", accountId);
                sel.Parameters.AddWithValue("$sn", snTrim);
                if (sel.ExecuteScalar() is null)
                {
                    khongCoDon++;
                    continue;
                }
            }

            using var upd = conn.CreateCommand();
            upd.Transaction = tx;
            // WHERE ... <> $code lo luôn phần "chỉ ghi khi KHÁC": 0 dòng đổi = mã cũ đã đúng.
            upd.CommandText = @"UPDATE orders SET
    return_request_code = $code,
    gsheet_da_co_don_tra_hang = NULL,
    hub_synced_at = NULL
    WHERE account_id = $a AND order_sn = $sn AND COALESCE(return_request_code, '') <> $code;";
            upd.Parameters.AddWithValue("$code", codeTrim);
            upd.Parameters.AddWithValue("$a", accountId);
            upd.Parameters.AddWithValue("$sn", snTrim);
            if (upd.ExecuteNonQuery() > 0)
            {
                daGhi++;
                capDaGhi.Add((snTrim, codeTrim));
            }
            else
            {
                khongDoi++;
            }
        }

        tx.Commit();
        return new ReturnCodeSaveResult(daGhi, khongDoi, khongCoDon, capDaGhi);
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
       status, status_description, cancel_reason, channel, carrier, tracking_number, shop_login, prepared_at,
       return_request_code
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
                ShopLogin = reader.IsDBNull(18) ? null : reader.GetString(18),
                PreparedAt = reader.IsDBNull(19) ? null : DbSerialization.ParseDate(reader.GetString(19)),
                ReturnRequestCode = reader.IsDBNull(20) ? null : reader.GetString(20),
            });
        }
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

    /// <summary>
    /// Thời điểm sync gần nhất (<c>MAX(synced_at)</c>, giờ UTC) của TỪNG tài khoản — MỘT query cho cả bảng,
    /// dùng cho gương danh bạ đẩy lên Hub (chạy định kỳ trên MỌI tài khoản nên không hỏi từng tài khoản một).
    /// Tài khoản chưa có đơn nào → KHÔNG có khóa trong map (bên gọi hiểu là "chưa sync lần nào").
    /// </summary>
    public IReadOnlyDictionary<long, DateTime> MaxSyncedAtByAccount()
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT account_id, MAX(synced_at) FROM orders GROUP BY account_id;";

        var map = new Dictionary<long, DateTime>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1))
            {
                continue;
            }
            // Dòng cũ có thể mang chuỗi ngày lạ (DB chép tay) → bỏ qua dòng đó thay vì ném cả lượt đẩy gương.
            if (DateTime.TryParse(reader.GetString(1), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var at))
            {
                map[reader.GetInt64(0)] = at;
            }
        }
        return map;
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
