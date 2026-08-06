using Microsoft.Data.Sqlite;
using Shopee.Core.Coordination;
using XuLyDonShopee.Core.Services;

namespace Shopee.Hub;

/// <summary>Một dòng đơn ĐỌC ra từ bảng <c>orders</c> để hiển thị/xuất. MIRROR <c>SyncedOrder</c> của module
/// đơn Shopee (orders/XuLyDonShopee.Core/Models) để client map dễ.</summary>
public sealed class OrderRecord
{
    public long Id { get; init; }
    public long ShopId { get; init; }
    public string OrderSn { get; init; } = "";
    public string? ShopeeOrderId { get; init; }
    public string? BuyerUsername { get; init; }
    public int ItemCount { get; init; }
    public string? ItemSummary { get; init; }
    public string? Sku { get; init; }
    /// <summary>Mảng JSON các sản phẩm <c>{name, variation, amount, image}</c> client đẩy lên — nguồn suy cột
    /// "Phân loại" của trang /orders (<c>PhanLoaiExtractor</c>); KHÔNG có cột "phân loại" riêng trong DB.</summary>
    public string? ItemsJson { get; init; }
    public long? TotalPrice { get; init; }
    public string? TotalPriceText { get; init; }
    public long? FinalAmount { get; init; }
    public string? FinalAmountText { get; init; }
    public string? PaymentMethod { get; init; }
    public string? Status { get; init; }
    public string? StatusDescription { get; init; }
    public string? CancelReason { get; init; }
    public string? Channel { get; init; }
    public string? Carrier { get; init; }
    public string? TrackingNumber { get; init; }
    /// <summary>Mã yêu cầu trả hàng khớp đơn (cột "Đơn trả hàng") — client đọc ở trang "Trả hàng/Hoàn tiền/Hủy"
    /// cuối flow shop. NULL = đơn chưa có yêu cầu trả hàng.</summary>
    public string? ReturnRequestCode { get; init; }
    public DateTimeOffset SyncedAt { get; init; }
    /// <summary>Thời điểm hub NHẬN file phiếu PDF của đơn (POST /api/orders/slip). NULL = chưa có phiếu trên hub.</summary>
    public DateTimeOffset? SlipAt { get; init; }
}

/// <summary>Kết quả upsert lô đơn: số thêm mới + số cập nhật + đơn vừa thêm + đơn có mã trả hàng mới/đổi
/// (notify Hub). Các list chỉ dùng nội bộ hub.</summary>
public sealed record UpsertOrdersResult(
    int Added,
    int Updated,
    List<OrderPushItem> InsertedItems,
    List<OrderPushItem> ReturnCodeChangedItems);

/// <summary>Một dòng thống kê "chuẩn bị hàng" theo shop trong MỘT ngày (<see cref="HubDatabase.PrepareStatsByDay"/>)
/// — kiểu NỘI BỘ hub, endpoint map tường minh sang <c>PrepareStatItem</c> (DTO dùng chung với client).</summary>
public sealed record PrepareStatRow(string ShopUsername, int Count);

/// <summary>Tổng quan ĐƠN của 1 shop cho trang Giao việc (<see cref="HubDatabase.ShopOrderSummaries"/>): số đơn
/// trong khoảng đang ở trạng thái chờ, số đơn trong khoảng ĐÃ có phiếu trên hub, lần client đẩy đơn gần nhất.
/// Kiểu NỘI BỘ hub (chỉ trang Blazor dùng, không đi qua dây).</summary>
public sealed record ShopOrderSummary(long ShopId, int Waiting, int WithSlip, DateTimeOffset LastSynced);
public sealed record OrdersPageResult(int Total, List<OrderRecord> Items);

/// <summary>Một dòng đơn THÔ đọc cho thống kê dùng chung (<see cref="HubDatabase.GetSharedOrderStatistics"/>) — kiểu
/// NỘI BỘ hub, chỉ giữ các cột cần để gom số. Luật phân loại hủy / tiền của đơn để NGAY TRONG kiểu này để mọi chỗ
/// gom số (tổng, theo trạng thái, theo shop) dùng đúng một luật — cùng luật với client.</summary>
internal sealed record StatOrderRow(
    string Shop, string? Status, string? StatusDescription, string? CancelReason, string? Carrier, string? Channel,
    string? PaymentMethod, long? FinalAmount, long? TotalPrice, string? TrackingNumber, int ItemCount,
    DateTimeOffset SyncedAt)
{
    /// <summary>Đơn hủy → KHÔNG tính vào doanh thu và "đơn hiệu lực".</summary>
    public bool Cancelled => ShopeeShippingNav.LaDonHuy(Status, StatusDescription, CancelReason);

    /// <summary>Tiền của đơn: ưu tiên "số tiền cuối cùng", chưa có thì "tổng tiền"; âm/thiếu → 0.</summary>
    public long Revenue => Math.Max(0, FinalAmount ?? TotalPrice ?? 0);
}

/// <summary>Phần HubDatabase: nghiệp vụ ĐƠN HÀNG — bảng <c>orders</c> (UNIQUE shop_id+order_sn) + upsert/query/count.</summary>
public sealed partial class HubDatabase
{
    private void EnsureOrdersSchema() => ExecRaw(@"
CREATE TABLE IF NOT EXISTS orders(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  shop_id INTEGER NOT NULL,
  order_sn TEXT NOT NULL,
  shopee_order_id TEXT, buyer_username TEXT,
  items_json TEXT DEFAULT '[]', item_count INTEGER DEFAULT 0,
  item_summary TEXT, sku TEXT,
  total_price INTEGER, total_price_text TEXT,
  final_amount INTEGER, final_amount_text TEXT,
  payment_method TEXT, status TEXT, status_description TEXT, cancel_reason TEXT,
  channel TEXT, carrier TEXT, tracking_number TEXT,
  synced_at TEXT, slip_at TEXT,
  prepared_at TEXT, prepared_day TEXT, return_request_code TEXT, first_seen_at TEXT, return_code_at TEXT);
CREATE UNIQUE INDEX IF NOT EXISTS ux_orders_shop_sn ON orders(shop_id, order_sn);
CREATE INDEX IF NOT EXISTS ix_orders_shop ON orders(shop_id);
CREATE INDEX IF NOT EXISTS ix_orders_status ON orders(status);");

    /// <summary>Upsert 1 lô đơn của 1 shop (khoá shop_id+order_sn). Bỏ đơn thiếu order_sn. Trả (thêm mới, cập
    /// nhật, DANH SÁCH đơn vừa thêm mới) — danh sách phục vụ notify "đơn mới".</summary>
    public UpsertOrdersResult UpsertOrders(long shopId, IEnumerable<OrderPushItem> orders)
    {
        var added = 0; var updated = 0;
        var inserted = new List<OrderPushItem>();
        var returnChanged = new List<OrderPushItem>();
        lock (_gate)
        {
            var now = Iso(DateTimeOffset.UtcNow);
            using var tx = _conn.BeginTransaction();
            foreach (var o in orders)
            {
                if (o is null || string.IsNullOrWhiteSpace(o.OrderSn)) continue;
                bool exists;
                string? itemsCu = null;
                int itemCountCu = 0;
                string? rrcCu = null;
                using (var chk = _conn.CreateCommand())
                {
                    chk.Transaction = tx;
                    // Đọc items_json + item_count + return_request_code cũ để chọn bản giàu / phát hiện mã trả mới.
                    chk.CommandText = "SELECT items_json, item_count, return_request_code FROM orders WHERE shop_id=$s AND order_sn=$sn";
                    chk.Parameters.AddWithValue("$s", shopId);
                    chk.Parameters.AddWithValue("$sn", o.OrderSn);
                    using var rd = chk.ExecuteReader();
                    exists = rd.Read();
                    if (exists)
                    {
                        itemsCu = rd.IsDBNull(0) ? null : rd.GetString(0);
                        itemCountCu = rd.IsDBNull(1) ? 0 : rd.GetInt32(1);
                        rrcCu = rd.IsDBNull(2) ? null : rd.GetString(2);
                    }
                }

                // items_json: đơn mới → ghi thẳng; đơn đã có → ChonItemsJson (bản nghèo KHÔNG đè bản giàu),
                // item_count ĐI THEO bản được giữ (khỏi count nói dối). Giống client OrdersRepository.
                string? itemsGhi = o.ItemsJson ?? "[]";
                int itemCountGhi = o.ItemCount;
                if (exists)
                {
                    var chon = SanPhamDonParser.ChonItemsJson(itemsCu, o.ItemsJson);
                    if (!string.Equals(chon, o.ItemsJson, StringComparison.Ordinal))
                    {
                        itemsGhi = chon ?? "[]";
                        itemCountGhi = itemCountCu;
                    }
                }

                using var c = _conn.CreateCommand();
                c.Transaction = tx;
                // final_amount / final_amount_text / tracking_number dùng COALESCE($moi, cot_cu): đơn ĐẨY LẠI (vd
                // client đẩy lại vì trạng thái đổi sang "Đã hủy") có thể KHÔNG còn kèm số tiền cuối cùng / mã vận
                // đơn — giữ giá trị hub ĐANG CÓ thay vì xoá về NULL. Các cột còn lại (nhất là status /
                // status_description / cancel_reason) GHI ĐÈ thẳng: đó chính là dữ liệu cần cập nhật.
                // prepared_at / prepared_day cũng COALESCE: đơn đẩy lại không kèm thì GIỮ, và MÁY KHÁC đẩy lại
                // KHÔNG ghi đè ngày của máy đã thực sự chuẩn bị đơn (nguồn đếm /prepare-stats).
                // return_request_code COALESCE cùng lý do: chỉ MÁY chạy bước check trả hàng của shop đó mới có mã,
                // máy khác đẩy lại (không kèm) KHÔNG được xoá mã đang có.
                // first_seen_at CHỈ đặt lúc INSERT — lần ĐẦU đơn xuất hiện, mốc DUY NHẤT toàn hệ thống để thống kê
                // đếm đơn. Lấy created_at CLIENT gửi kèm (lúc đơn xuất hiện trên máy đó) và chỉ lùi về $sa (giờ hub
                // NHẬN) khi client cũ không gửi: đẩy bù sau lúc hub offline / gói bay qua nửa đêm thì hai mốc rơi
                // KHÁC NGÀY, làm số chung lệch số local. Nhánh DO UPDATE TUYỆT ĐỐI KHÔNG đụng cột này: đụng vào là
                // đơn cũ đồng bộ lại nhảy sang ngày hôm nay (đúng lỗi của synced_at).
                // return_code_at: mốc hub ghi nhận mã trả hàng MỚI/ĐỔI — đặt ĐÚNG các ca mà `returnChanged` bên
                // dưới coi là tin mới (nhánh UPDATE + mã khác rỗng + khác mã cũ, so sau TRIM y hệt C#), để số
                // "mã trả hàng mới hôm nay" của tin tổng kết ngày và số tin webhook đã bắn nói cùng một chuyện.
                // Nhánh INSERT để NULL: đơn lên hub lần đầu đã mang sẵn mã KHÔNG phải mã mới phát sinh (dựng lại
                // hub / client đẩy bù cả kho sẽ dồn cả đống mã cũ vào tổng kết hôm nay) — cùng lý do bỏ notify.
                c.CommandText = @"
INSERT INTO orders(shop_id,order_sn,shopee_order_id,buyer_username,items_json,item_count,item_summary,sku,
  total_price,total_price_text,final_amount,final_amount_text,payment_method,status,status_description,
  cancel_reason,channel,carrier,tracking_number,synced_at,prepared_at,prepared_day,return_request_code,first_seen_at,
  return_code_at)
VALUES($s,$sn,$soi,$bu,$ij,$ic,$is,$sku,$tp,$tpt,$fa,$fat,$pm,$st,$sd,$cr,$ch,$ca,$tn,$sa,$pa,$pd,$rrc,COALESCE($fsa,$sa),
  NULL)
ON CONFLICT(shop_id,order_sn) DO UPDATE SET
  shopee_order_id=$soi, buyer_username=$bu, items_json=$ij, item_count=$ic, item_summary=$is, sku=$sku,
  total_price=$tp, total_price_text=$tpt,
  final_amount=COALESCE($fa,final_amount), final_amount_text=COALESCE($fat,final_amount_text),
  payment_method=$pm,
  status=$st, status_description=$sd, cancel_reason=$cr, channel=$ch, carrier=$ca,
  tracking_number=COALESCE($tn,tracking_number),
  prepared_at=COALESCE($pa,prepared_at), prepared_day=COALESCE($pd,prepared_day),
  return_code_at=CASE WHEN TRIM(COALESCE($rrc,'')) <> ''
                       AND TRIM(COALESCE($rrc,'')) <> TRIM(COALESCE(return_request_code,''))
                      THEN $sa ELSE return_code_at END,
  return_request_code=COALESCE($rrc,return_request_code),
  synced_at=$sa;";
                c.Parameters.AddWithValue("$s", shopId);
                c.Parameters.AddWithValue("$sn", o.OrderSn);
                c.Parameters.AddWithValue("$soi", (object?)o.ShopeeOrderId ?? DBNull.Value);
                c.Parameters.AddWithValue("$bu", (object?)o.BuyerUsername ?? DBNull.Value);
                c.Parameters.AddWithValue("$ij", itemsGhi);
                c.Parameters.AddWithValue("$ic", itemCountGhi);
                c.Parameters.AddWithValue("$is", (object?)o.ItemSummary ?? DBNull.Value);
                c.Parameters.AddWithValue("$sku", (object?)o.Sku ?? DBNull.Value);
                c.Parameters.AddWithValue("$tp", (object?)o.TotalPrice ?? DBNull.Value);
                c.Parameters.AddWithValue("$tpt", (object?)o.TotalPriceText ?? DBNull.Value);
                c.Parameters.AddWithValue("$fa", (object?)o.FinalAmount ?? DBNull.Value);
                c.Parameters.AddWithValue("$fat", (object?)o.FinalAmountText ?? DBNull.Value);
                c.Parameters.AddWithValue("$pm", (object?)o.PaymentMethod ?? DBNull.Value);
                c.Parameters.AddWithValue("$st", (object?)o.Status ?? DBNull.Value);
                c.Parameters.AddWithValue("$sd", (object?)o.StatusDescription ?? DBNull.Value);
                c.Parameters.AddWithValue("$cr", (object?)o.CancelReason ?? DBNull.Value);
                c.Parameters.AddWithValue("$ch", (object?)o.Channel ?? DBNull.Value);
                c.Parameters.AddWithValue("$ca", (object?)o.Carrier ?? DBNull.Value);
                c.Parameters.AddWithValue("$tn", (object?)o.TrackingNumber ?? DBNull.Value);
                c.Parameters.AddWithValue("$sa", now);
                c.Parameters.AddWithValue("$pa", (object?)o.PreparedAt ?? DBNull.Value);
                c.Parameters.AddWithValue("$pd", (object?)o.PreparedDay ?? DBNull.Value);
                c.Parameters.AddWithValue("$rrc", (object?)o.ReturnRequestCode ?? DBNull.Value);
                c.Parameters.AddWithValue("$fsa", (object?)NormalizeIso(o.CreatedAt) ?? DBNull.Value);
                c.ExecuteNonQuery();
                if (exists) updated++;
                else { added++; inserted.Add(o); }

                // Mã trả ĐỔI trên đơn hub ĐÃ CÓ → Hub notify "đơn trả". CHỈ nhánh `exists`: đơn INSERT lần đầu mà
                // đã mang sẵn mã trả KHÔNG phải tin mới — dựng lại hub / shop mới vào / client đẩy bù cả kho sẽ
                // bắn một loạt tin CŨ. Hạn chế đã biết: đơn có mã TRƯỚC lần push đầu tiên thì hub không notify
                // (client lo phần đó — xem plan "mã trả hàng sống độc lập với đơn").
                var rrcMoi = o.ReturnRequestCode?.Trim();
                if (exists && !string.IsNullOrEmpty(rrcMoi)
                    && !string.Equals(rrcMoi, rrcCu?.Trim(), StringComparison.Ordinal))
                {
                    returnChanged.Add(o);
                }
            }
            tx.Commit();
        }
        return new UpsertOrdersResult(added, updated, inserted, returnChanged);
    }

    /// <summary>
    /// Chuẩn hoá mốc ISO-8601 CLIENT gửi lên về ĐÚNG dạng hub tự ghi (<see cref="Iso(DateTimeOffset)"/> → hậu tố
    /// <c>+00:00</c>, giờ UTC). BẮT BUỘC: mọi lượt lọc theo thời gian ở đây so CHUỖI, mà client gửi
    /// <c>DateTime.ToString("o")</c> cho mốc UTC là hậu tố <c>Z</c> — trộn hai dạng thì so sánh lệch ở đúng biên
    /// (<c>'+' &lt; 'Z'</c>). Chuỗi trống / không parse được → <c>null</c> (caller lùi về mốc mặc định).
    /// </summary>
    private static string? NormalizeIso(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var at)
            ? Iso(at.ToUniversalTime())
            : null;
    }

    /// <summary>Count + page tren cung read transaction de UI khong bi lech tong khi client dang push don.
    /// <paramref name="coMaTra"/> = chỉ đơn CÓ mã yêu cầu trả hàng (<c>return_request_code</c> khác NULL/rỗng) —
    /// toggle "Có mã trả" của trang /orders; false = không lọc theo mã trả.</summary>
    public OrdersPageResult QueryOrdersPage(long? shopId, string? status, string? search, int limit, int offset,
        bool coMaTra = false)
    {
        using var conn = OpenReadConnection();
        using var tx = conn.BeginTransaction(deferred: true);
        var total = CountOrdersCore(conn, tx, shopId, status, search, coMaTra);
        var items = QueryOrdersCore(conn, tx, shopId, status, search, limit, offset, coMaTra);
        tx.Commit();
        return new OrdersPageResult(total, items);
    }

    /// <summary>
    /// Các đơn khớp BỘ LỌC của trang /orders mà hub ĐANG CÓ file phiếu (<c>slip_at</c> khác NULL) — nguồn cho nút
    /// "⬇ ZIP phiếu" (H2.4). Trả <c>(shop_id, order_sn)</c> theo đúng thứ tự lưới (sync mới nhất trước) để tên
    /// entry trong file zip ổn định. <paramref name="limit"/> nên truyền TRẦN+1 để caller phát hiện "quá trần" mà
    /// không phải đếm cả bảng. Không dựng <see cref="OrderRecord"/> (chỉ cần 2 cột để mở file trên đĩa).
    /// </summary>
    public List<(long ShopId, string OrderSn)> OrdersWithSlip(
        long? shopId, string? status, string? search, bool coMaTra, int limit)
    {
        using var conn = OpenReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT shop_id, order_sn FROM orders"
            + WhereClause(cmd, shopId, status, search, coMaTra, slipOnly: true)
            + " ORDER BY synced_at DESC, id DESC LIMIT $lim";
        cmd.Parameters.AddWithValue("$lim", Math.Max(1, limit));

        var list = new List<(long, string)>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add((rd.GetInt64(0), S(rd, 1)));
        return list;
    }

    public Dictionary<long, int> OrderCountsByShop()
    {
        using var conn = OpenReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT shop_id, COUNT(*) FROM orders GROUP BY shop_id";
        using var rd = cmd.ExecuteReader();
        var result = new Dictionary<long, int>();
        while (rd.Read()) result[rd.GetInt64(0)] = checked((int)rd.GetInt64(1));
        return result;
    }

    /// <summary>
    /// Thống kê đơn DÙNG CHUNG trong khoảng <c>[fromUtc, toUtcExclusive)</c> — hai mốc UTC do CLIENT tính sẵn từ
    /// ngày địa phương của người dùng; hub KHÔNG suy diễn múi giờ (máy chủ chạy giờ UTC, mỗi client một múi giờ).
    /// <para>Lọc theo <c>first_seen_at</c> = lần ĐẦU đơn xuất hiện trên hub (đặt lúc INSERT, không đổi khi đồng bộ
    /// lại) — tương đương <c>created_at</c> của DB client, nên CÙNG khoảng ngày thì số của hub khớp số local.
    /// TUYỆT ĐỐI không lọc theo <c>synced_at</c>: cột đó bị ghi đè mỗi lần đẩy lại nên đơn cũ sẽ nhảy vào hôm nay.</para>
    /// Trả SỐ THÔ (không chuỗi hiển thị): định dạng ngày/tiền/tỉ lệ là việc của client.
    /// </summary>
    public SharedOrderStatistics GetSharedOrderStatistics(DateTime fromUtc, DateTime toUtcExclusive, string? shopLogin)
    {
        // Mốc so sánh phải dựng CÙNG cách các cột thời gian được GHI (Iso(DateTimeOffset) → hậu tố "+00:00"): so
        // chuỗi mà một bên "…Z" một bên "…+00:00" là lệch ở đúng biên ('+' < 'Z').
        var fromStr = Iso(new DateTimeOffset(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc), TimeSpan.Zero));
        var toStr = Iso(new DateTimeOffset(DateTime.SpecifyKind(toUtcExclusive, DateTimeKind.Utc), TimeSpan.Zero));

        // Đọc qua connection ĐỌC riêng (WAL) chứ KHÔNG giữ _gate: truy vấn này quét mọi đơn trong khoảng ngày,
        // giữ khóa toàn cục suốt lượt quét sẽ chặn heartbeat và mọi lượt client đẩy đơn.
        using (var conn = OpenReadConnection())
        {
            var stats = new SharedOrderStatistics();
            var rows = new List<StatOrderRow>();

            using (var c = conn.CreateCommand())
            {
                c.CommandText = @"SELECT COALESCE(s.username, ''), o.status, o.status_description, o.cancel_reason, o.carrier, o.channel,
       o.payment_method, o.final_amount, o.total_price, o.tracking_number, o.item_count, o.synced_at
FROM orders o JOIN shops s ON s.id = o.shop_id
WHERE o.first_seen_at >= $from AND o.first_seen_at < $to"
                    // COLLATE NOCASE: cùng luật với ux_shops_username / GetOrCreateShopByUsername — client gửi nhãn
                    // shop lệch HOA/thường so với bản lưu thì lọc phải vẫn ra đúng shop, không trả 0 một cách LẶNG.
                    + (string.IsNullOrWhiteSpace(shopLogin) ? "" : " AND s.username = $shop COLLATE NOCASE");
                c.Parameters.AddWithValue("$from", fromStr);
                c.Parameters.AddWithValue("$to", toStr);
                if (!string.IsNullOrWhiteSpace(shopLogin)) c.Parameters.AddWithValue("$shop", shopLogin.Trim());
                using var rd = c.ExecuteReader();
                while (rd.Read())
                {
                    rows.Add(new StatOrderRow(
                        S(rd, 0),
                        rd.IsDBNull(1) ? null : rd.GetString(1),
                        rd.IsDBNull(2) ? null : rd.GetString(2),
                        rd.IsDBNull(3) ? null : rd.GetString(3),
                        rd.IsDBNull(4) ? null : rd.GetString(4),
                        rd.IsDBNull(5) ? null : rd.GetString(5),
                        rd.IsDBNull(6) ? null : rd.GetString(6),
                        rd.IsDBNull(7) ? null : rd.GetInt64(7),
                        rd.IsDBNull(8) ? null : rd.GetInt64(8),
                        rd.IsDBNull(9) ? null : rd.GetString(9),
                        rd.IsDBNull(10) ? 0 : rd.GetInt32(10),
                        D(rd, 11)));
                }
            }

            var active = rows.Where(r => !r.Cancelled).ToList();
            stats.TotalOrders = rows.Count;
            stats.TotalItems = rows.Sum(r => Math.Max(0, r.ItemCount));
            stats.Cancelled = rows.Count - active.Count;
            stats.Delivered = active.Count(r => ShopeeShippingNav.LaDaGiaoDaBan(r.Status));
            stats.NeedsAction = active.Count(r => ShopeeShippingNav.LaChuanBiHang(r.Status));
            stats.Revenue = active.Sum(r => r.Revenue);
            stats.ActiveOrders = active.Count;
            stats.AverageOrder = active.Count == 0 ? 0 : stats.Revenue / active.Count;
            stats.WithTracking = rows.Count(r => !string.IsNullOrWhiteSpace(r.TrackingNumber));
            stats.WithFinalAmount = active.Count(r => r.FinalAmount is not null);
            stats.LastSyncedUtc = rows.Count == 0 ? null : rows.Max(r => r.SyncedAt);

            var total = rows.Count;
            // Doanh thu theo trạng thái: gom MỘT lượt trên đơn hiệu lực rồi tra bảng (trước đây mỗi nhóm quét lại
            // toàn bộ active → O(nhóm×n)).
            var revenueByStatus = active.GroupBy(r => Clean(r.Status, "Chưa rõ"), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Revenue), StringComparer.OrdinalIgnoreCase);
            stats.StatusRows = rows.GroupBy(r => Clean(r.Status, "Chưa rõ"), StringComparer.OrdinalIgnoreCase)
                .Select(g => new SharedOrderStatisticBreakdown { Label = g.Key, OrderCount = g.Count(), Value = revenueByStatus.TryGetValue(g.Key, out var rev) ? rev : 0, Percentage = total == 0 ? 0 : g.Count() * 100d / total })
                .OrderByDescending(x => x.OrderCount).ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase).ToList();
            stats.CarrierRows = rows.GroupBy(r => Clean(r.Carrier ?? r.Channel, "Chưa rõ"), StringComparer.OrdinalIgnoreCase)
                .Select(g => new SharedOrderStatisticBreakdown { Label = g.Key, OrderCount = g.Count(), Percentage = total == 0 ? 0 : g.Count() * 100d / total })
                .OrderByDescending(x => x.OrderCount).ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase).ToList();
            stats.PaymentRows = rows.GroupBy(r => Clean(r.PaymentMethod, "Chưa rõ"), StringComparer.OrdinalIgnoreCase)
                .Select(g => new SharedOrderStatisticBreakdown { Label = g.Key, OrderCount = g.Count(), Percentage = total == 0 ? 0 : g.Count() * 100d / total })
                .OrderByDescending(x => x.OrderCount).ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase).ToList();
            stats.ShopRows = rows.GroupBy(r => Clean(r.Shop, "(shop chưa xác định)"), StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var list = g.ToList();
                    var act = list.Where(r => !r.Cancelled).ToList();
                    var rev = act.Sum(r => r.Revenue);
                    var tracked = list.Count(r => !string.IsNullOrWhiteSpace(r.TrackingNumber));
                    return new SharedShopStatisticRow { Shop = g.Key, OrderCount = list.Count, ItemCount = list.Sum(r => Math.Max(0, r.ItemCount)), Revenue = rev, Average = act.Count == 0 ? 0 : rev / act.Count, TrackingRate = list.Count == 0 ? 0 : tracked * 100d / list.Count };
                })
                .OrderByDescending(x => x.OrderCount).ThenBy(x => x.Shop, StringComparer.OrdinalIgnoreCase).ToList();
            return stats;
        }

        static string Clean(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static int CountOrdersCore(SqliteConnection conn, SqliteTransaction? tx,
        long? shopId, string? status, string? search, bool coMaTra)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM orders" + WhereClause(cmd, shopId, status, search, coMaTra);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static List<OrderRecord> QueryOrdersCore(SqliteConnection conn, SqliteTransaction? tx,
        long? shopId, string? status, string? search, int limit, int offset, bool coMaTra)
    {
        var list = new List<OrderRecord>();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id,shop_id,order_sn,shopee_order_id,buyer_username,item_count,item_summary,sku,"
            + "total_price,total_price_text,final_amount,final_amount_text,payment_method,status,status_description,"
            + "cancel_reason,channel,carrier,tracking_number,synced_at,slip_at,items_json,return_request_code FROM orders"
            + WhereClause(cmd, shopId, status, search, coMaTra)
            + " ORDER BY synced_at DESC, id DESC LIMIT $lim OFFSET $off";
        cmd.Parameters.AddWithValue("$lim", Math.Clamp(limit, 1, 1000));
        cmd.Parameters.AddWithValue("$off", Math.Max(0, offset));
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add(ReadOrderRow(rd));
        return list;
    }

    /// <summary>
    /// Số đơn ĐÃ "chuẩn bị hàng" theo TỪNG shop trong ĐÚNG một ngày (<paramref name="day"/> = <c>yyyy-MM-dd</c>
    /// giờ địa phương của máy chuẩn bị đơn — client tính sẵn vào <c>prepared_day</c>). Đếm THẲNG từ bảng
    /// <c>orders</c> (khoá <c>shop_id+order_sn</c>) nên mỗi đơn chỉ được tính MỘT lần dù bao nhiêu máy cùng chạy.
    /// Shop chưa có <c>username</c> (không định danh được với client) → bỏ. Ngày không có đơn nào → list RỖNG.
    /// </summary>
    public List<PrepareStatRow> PrepareStatsByDay(string day)
    {
        // Connection ĐỌC riêng (WAL) chứ KHÔNG giữ _gate — mẫu GetSharedOrderStatistics: MỌI client hỏi hàm này 4
        // mốc/ngày và sau MỖI shop, giữ khoá toàn cục là chặn cả heartbeat lẫn lượt đẩy đơn.
        using var conn = OpenReadConnection();
        var list = new List<PrepareStatRow>();
        using var c = conn.CreateCommand();
        c.CommandText = "SELECT s.username, COUNT(*) FROM orders o JOIN shops s ON s.id = o.shop_id "
            + "WHERE o.prepared_day = $d GROUP BY s.username";
        c.Parameters.AddWithValue("$d", day);
        using var rd = c.ExecuteReader();
        while (rd.Read())
        {
            if (rd.IsDBNull(0)) continue;
            list.Add(new PrepareStatRow(rd.GetString(0), rd.GetInt32(1)));
        }
        return list;
    }

    /// <summary>
    /// Gom số đơn theo TỪNG shop trong khoảng UTC <c>[fromUtc, toUtcExclusive)</c> bằng MỘT lượt quét bảng
    /// <c>orders</c> — trang Giao việc bám nhịp fleet nên
    /// KHÔNG đếm lại từng shop một (mỗi lần một query riêng).
    /// <paramref name="waitingStatus"/> = trạng thái coi là "đang chờ" (vd "Chờ lấy hàng"). Shop chưa có đơn nào
    /// → KHÔNG có dòng (bên gọi tự hiển thị 0). Lọc bằng <c>first_seen_at</c>, tuyệt đối không dùng
    /// <c>synced_at</c> vì đồng bộ lại sẽ làm đơn cũ nhảy sang ngày mới. Hàm ĐỌC thuần, không đụng hàm đếm sẵn có.
    /// </summary>
    public List<ShopOrderSummary> ShopOrderSummaries(
        string waitingStatus,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtcExclusive)
    {
        // Connection ĐỌC riêng (WAL) — trang Giao việc gọi hàm này mỗi ≤10s theo nhịp fleet.
        using var conn = OpenReadConnection();
        var list = new List<ShopOrderSummary>();
        using var c = conn.CreateCommand();
        // MAX(synced_at) so sánh chuỗi ISO ("o") — mọi bản ghi đều do Iso(UtcNow) ghi nên cùng định dạng,
        // thứ tự từ điển = thứ tự thời gian.
        // Đặt khoảng ngày ở WHERE để SQLite dùng ix_orders_first_seen và không GROUP BY toàn bộ lịch sử.
        c.CommandText = "SELECT shop_id, "
            + "SUM(CASE WHEN status=$w THEN 1 ELSE 0 END), "
            + "SUM(CASE WHEN slip_at IS NOT NULL THEN 1 ELSE 0 END), "
            + "MAX(synced_at) FROM orders "
            + "WHERE first_seen_at >= $from AND first_seen_at < $to GROUP BY shop_id";
        c.Parameters.AddWithValue("$w", waitingStatus ?? "");
        c.Parameters.AddWithValue("$from", Iso(fromUtc.ToUniversalTime()));
        c.Parameters.AddWithValue("$to", Iso(toUtcExclusive.ToUniversalTime()));
        using var rd = c.ExecuteReader();
        while (rd.Read())
            list.Add(new ShopOrderSummary(
                rd.GetInt64(0),
                rd.IsDBNull(1) ? 0 : rd.GetInt32(1),
                rd.IsDBNull(2) ? 0 : rd.GetInt32(2),
                D(rd, 3)));
        return list;
    }

    /// <summary>
    /// Ghi mã yêu cầu trả hàng lấy từ <c>app-alert kind=don_tra</c> vào bảng <c>orders</c> — CÁI ĐƠN Ở ĐÂY ĐÃ BỊ
    /// APP DỌN nên không còn về hub qua <c>orders/push</c>, nhưng HUB VẪN GIỮ dòng đơn đó (hub không dọn theo app).
    /// Không có bước này thì <c>return_code_at</c> chỉ phủ phần thiểu số (đơn còn sống), làm dòng "mã trả hàng mới
    /// hôm nay" của tin tổng kết gần như luôn báo 0 và bộ lọc "Có mã trả" ở /orders bỏ sót.
    /// <para>Chỉ ghi khi mã THẬT SỰ mới/khác (so sau TRIM, đúng luật <c>returnChanged</c> của
    /// <see cref="UpsertOrders"/>) ⇒ gọi lại cùng lô KHÔNG cộng thêm số vào tin tổng kết. Khoá theo
    /// <c>order_sn</c> (mã đơn Shopee là duy nhất toàn sàn; app-alert không mang shop_id).</para>
    /// Trả về số đơn vừa ghi mã mới.
    /// </summary>
    public int ApplyReturnCodesFromAppAlert(IEnumerable<(string MaDon, string MaYeuCau)> cap)
    {
        var now = Iso(DateTimeOffset.UtcNow);
        var ghi = 0;
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var (maDon, maYeuCau) in cap)
            {
                var sn = (maDon ?? "").Trim();
                var code = (maYeuCau ?? "").Trim();
                if (sn.Length == 0 || code.Length == 0) continue;

                using var c = _conn.CreateCommand();
                c.Transaction = tx;
                c.CommandText = @"
UPDATE orders SET return_request_code = $code, return_code_at = $at
WHERE order_sn = $sn AND (return_request_code IS NULL OR TRIM(return_request_code) <> $code);";
                c.Parameters.AddWithValue("$code", code);
                c.Parameters.AddWithValue("$at", now);
                c.Parameters.AddWithValue("$sn", sn);
                ghi += c.ExecuteNonQuery();
            }
            tx.Commit();
        }
        return ghi;
    }

    /// <summary>
    /// SỐ ĐƠN được ghi nhận mã yêu cầu trả hàng MỚI/ĐỔI trong khoảng UTC <c>[fromUtc, toUtcExclusive)</c>
    /// (cột <c>return_code_at</c> — xem <see cref="UpsertOrders"/>). Nguồn dòng "Mã trả hàng mới hôm nay" của tin
    /// tổng kết ngày. Đơn có mã từ TRƯỚC bản vá thêm cột này mang NULL nên không rơi vào khoảng nào — cố ý.
    /// </summary>
    public int CountReturnCodesInRange(DateTimeOffset fromUtc, DateTimeOffset toUtcExclusive)
    {
        // Connection ĐỌC riêng (WAL) — chạy mỗi ngày một lần từ luồng nền, không đáng giữ khoá ghi toàn cục.
        using var conn = OpenReadConnection();
        using var c = conn.CreateCommand();
        c.CommandText = "SELECT COUNT(*) FROM orders WHERE return_code_at >= $from AND return_code_at < $to";
        c.Parameters.AddWithValue("$from", Iso(fromUtc.ToUniversalTime()));
        c.Parameters.AddWithValue("$to", Iso(toUtcExclusive.ToUniversalTime()));
        return Convert.ToInt32(c.ExecuteScalar());
    }

    /// <summary>Danh sách các trạng thái phân biệt (cho dropdown lọc).</summary>
    public List<string> DistinctOrderStatuses()
    {
        // Connection ĐỌC riêng (WAL) — nạp dropdown ở trang /orders, không đáng giữ khoá ghi toàn cục.
        using var conn = OpenReadConnection();
        var list = new List<string>();
        using var c = conn.CreateCommand();
        c.CommandText = "SELECT DISTINCT status FROM orders WHERE status IS NOT NULL AND status<>'' ORDER BY status";
        using var rd = c.ExecuteReader();
        while (rd.Read()) list.Add(S(rd, 0));
        return list;
    }

    /// <summary>Mệnh đề WHERE dùng CHUNG cho đếm/đọc trang/gom phiếu của trang /orders — một nguồn duy nhất để
    /// tổng, lưới và file ZIP luôn nói về cùng một tập đơn. <paramref name="coMaTra"/> = chỉ đơn có mã yêu cầu trả
    /// hàng; <paramref name="slipOnly"/> = chỉ đơn hub đã có file phiếu (dùng cho ZIP).</summary>
    private static string WhereClause(SqliteCommand c, long? shopId, string? status, string? search,
        bool coMaTra, bool slipOnly = false)
    {
        var conds = new List<string>();
        if (coMaTra) conds.Add("(return_request_code IS NOT NULL AND TRIM(return_request_code) <> '')");
        if (slipOnly) conds.Add("slip_at IS NOT NULL");
        if (shopId is { } sid)
        {
            conds.Add("shop_id=$s");
            c.Parameters.AddWithValue("$s", sid);
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            conds.Add("status=$st");
            c.Parameters.AddWithValue("$st", status);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            conds.Add("(order_sn LIKE $q OR buyer_username LIKE $q OR item_summary LIKE $q OR sku LIKE $q)");
            c.Parameters.AddWithValue("$q", "%" + search.Trim() + "%");
        }
        return conds.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conds);
    }

    private static OrderRecord ReadOrderRow(SqliteDataReader rd) => new()
    {
        Id = rd.GetInt64(0),
        ShopId = rd.GetInt64(1),
        OrderSn = S(rd, 2),
        ShopeeOrderId = rd.IsDBNull(3) ? null : rd.GetString(3),
        BuyerUsername = rd.IsDBNull(4) ? null : rd.GetString(4),
        ItemCount = rd.IsDBNull(5) ? 0 : rd.GetInt32(5),
        ItemSummary = rd.IsDBNull(6) ? null : rd.GetString(6),
        Sku = rd.IsDBNull(7) ? null : rd.GetString(7),
        TotalPrice = rd.IsDBNull(8) ? null : rd.GetInt64(8),
        TotalPriceText = rd.IsDBNull(9) ? null : rd.GetString(9),
        FinalAmount = rd.IsDBNull(10) ? null : rd.GetInt64(10),
        FinalAmountText = rd.IsDBNull(11) ? null : rd.GetString(11),
        PaymentMethod = rd.IsDBNull(12) ? null : rd.GetString(12),
        Status = rd.IsDBNull(13) ? null : rd.GetString(13),
        StatusDescription = rd.IsDBNull(14) ? null : rd.GetString(14),
        CancelReason = rd.IsDBNull(15) ? null : rd.GetString(15),
        Channel = rd.IsDBNull(16) ? null : rd.GetString(16),
        Carrier = rd.IsDBNull(17) ? null : rd.GetString(17),
        TrackingNumber = rd.IsDBNull(18) ? null : rd.GetString(18),
        SyncedAt = D(rd, 19),
        SlipAt = rd.IsDBNull(20) ? null : D(rd, 20),
        ItemsJson = rd.IsDBNull(21) ? null : rd.GetString(21),
        ReturnRequestCode = rd.IsDBNull(22) ? null : rd.GetString(22),
    };

    /// <summary>True nếu đã có đơn (shop_id + order_sn) trong bảng — dùng cho POST /api/orders/slip: đơn CHƯA có
    /// trên hub → báo per-item <c>missing</c> (client thử lại lượt sau, sau khi orders/push xong).</summary>
    public bool OrderExists(long shopId, string orderSn)
    {
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT 1 FROM orders WHERE shop_id=$s AND order_sn=$sn";
            c.Parameters.AddWithValue("$s", shopId);
            c.Parameters.AddWithValue("$sn", orderSn);
            return c.ExecuteScalar() is not null;
        }
    }

    /// <summary>Đặt <c>slip_at</c> cho đơn (shop_id + order_sn) = thời điểm hub nhận phiếu — bản mới thắng (đè
    /// luôn). Trả về SỐ dòng cập nhật (0 = đơn không tồn tại). Gọi sau khi đã ghi file phiếu ra đĩa.</summary>
    public int SetOrderSlipAt(long shopId, string orderSn, DateTimeOffset at)
    {
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "UPDATE orders SET slip_at=$at WHERE shop_id=$s AND order_sn=$sn";
            c.Parameters.AddWithValue("$at", Iso(at));
            c.Parameters.AddWithValue("$s", shopId);
            c.Parameters.AddWithValue("$sn", orderSn);
            return c.ExecuteNonQuery();
        }
    }

    /// <summary>Chuẩn hoá <c>order_sn</c> thành tên file phiếu AN TOÀN: CHỈ giữ <c>[A-Za-z0-9_-]</c> (ASCII), bỏ
    /// mọi ký tự khác → chống path-traversal (không <c>/ \ . ..</c> lọt vào tên file). Rỗng sau lọc → <c>null</c>
    /// (từ chối). Dùng CHUNG cho POST (lưu) và GET /slips (phục vụ) để cùng một order_sn ánh xạ đúng một file.</summary>
    public static string? SanitizeSlipFileName(string? orderSn)
    {
        if (string.IsNullOrWhiteSpace(orderSn)) return null;
        var sb = new System.Text.StringBuilder(orderSn.Length);
        foreach (var ch in orderSn.Trim())
            if (ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_')
                sb.Append(ch);
        return sb.Length == 0 ? null : sb.ToString();
    }
}
