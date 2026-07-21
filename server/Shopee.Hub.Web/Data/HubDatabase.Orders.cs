using Microsoft.Data.Sqlite;
using Shopee.Core.Coordination;

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
    public DateTimeOffset SyncedAt { get; init; }
}

/// <summary>Kết quả upsert lô đơn: số thêm mới + số cập nhật + DANH SÁCH đơn VỪA THÊM MỚI (cho notify "đơn
/// mới"). InsertedItems chỉ dùng nội bộ hub (không đi qua dây — endpoint chỉ trả <see cref="OrdersPushResult"/>).</summary>
public sealed record UpsertOrdersResult(int Added, int Updated, List<OrderPushItem> InsertedItems);

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
  synced_at TEXT);
CREATE UNIQUE INDEX IF NOT EXISTS ux_orders_shop_sn ON orders(shop_id, order_sn);
CREATE INDEX IF NOT EXISTS ix_orders_shop ON orders(shop_id);
CREATE INDEX IF NOT EXISTS ix_orders_status ON orders(status);");

    /// <summary>Upsert 1 lô đơn của 1 shop (khoá shop_id+order_sn). Bỏ đơn thiếu order_sn. Trả (thêm mới, cập
    /// nhật, DANH SÁCH đơn vừa thêm mới) — danh sách phục vụ notify "đơn mới".</summary>
    public UpsertOrdersResult UpsertOrders(long shopId, IEnumerable<OrderPushItem> orders)
    {
        var added = 0; var updated = 0;
        var inserted = new List<OrderPushItem>();
        lock (_gate)
        {
            var now = Iso(DateTimeOffset.UtcNow);
            using var tx = _conn.BeginTransaction();
            foreach (var o in orders)
            {
                if (o is null || string.IsNullOrWhiteSpace(o.OrderSn)) continue;
                bool exists;
                using (var chk = _conn.CreateCommand())
                {
                    chk.Transaction = tx;
                    chk.CommandText = "SELECT 1 FROM orders WHERE shop_id=$s AND order_sn=$sn";
                    chk.Parameters.AddWithValue("$s", shopId);
                    chk.Parameters.AddWithValue("$sn", o.OrderSn);
                    exists = chk.ExecuteScalar() is not null;
                }

                using var c = _conn.CreateCommand();
                c.Transaction = tx;
                c.CommandText = @"
INSERT INTO orders(shop_id,order_sn,shopee_order_id,buyer_username,items_json,item_count,item_summary,sku,
  total_price,total_price_text,final_amount,final_amount_text,payment_method,status,status_description,
  cancel_reason,channel,carrier,tracking_number,synced_at)
VALUES($s,$sn,$soi,$bu,$ij,$ic,$is,$sku,$tp,$tpt,$fa,$fat,$pm,$st,$sd,$cr,$ch,$ca,$tn,$sa)
ON CONFLICT(shop_id,order_sn) DO UPDATE SET
  shopee_order_id=$soi, buyer_username=$bu, items_json=$ij, item_count=$ic, item_summary=$is, sku=$sku,
  total_price=$tp, total_price_text=$tpt, final_amount=$fa, final_amount_text=$fat, payment_method=$pm,
  status=$st, status_description=$sd, cancel_reason=$cr, channel=$ch, carrier=$ca, tracking_number=$tn,
  synced_at=$sa;";
                c.Parameters.AddWithValue("$s", shopId);
                c.Parameters.AddWithValue("$sn", o.OrderSn);
                c.Parameters.AddWithValue("$soi", (object?)o.ShopeeOrderId ?? DBNull.Value);
                c.Parameters.AddWithValue("$bu", (object?)o.BuyerUsername ?? DBNull.Value);
                c.Parameters.AddWithValue("$ij", o.ItemsJson ?? "[]");
                c.Parameters.AddWithValue("$ic", o.ItemCount);
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
                c.ExecuteNonQuery();
                if (exists) updated++;
                else { added++; inserted.Add(o); }
            }
            tx.Commit();
        }
        return new UpsertOrdersResult(added, updated, inserted);
    }

    /// <summary>Đọc đơn có lọc (shop/trạng thái/tìm kiếm) + phân trang. search khớp order_sn/buyer/tên SP/SKU.</summary>
    public List<OrderRecord> QueryOrders(long? shopId, string? status, string? search, int limit, int offset)
    {
        lock (_gate)
        {
            var list = new List<OrderRecord>();
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT id,shop_id,order_sn,shopee_order_id,buyer_username,item_count,item_summary,sku,"
                + "total_price,total_price_text,final_amount,final_amount_text,payment_method,status,status_description,"
                + "cancel_reason,channel,carrier,tracking_number,synced_at FROM orders"
                + WhereClause(c, shopId, status, search)
                + " ORDER BY synced_at DESC, id DESC LIMIT $lim OFFSET $off";
            c.Parameters.AddWithValue("$lim", Math.Clamp(limit, 1, 1000));
            c.Parameters.AddWithValue("$off", Math.Max(0, offset));
            using var rd = c.ExecuteReader();
            while (rd.Read()) list.Add(ReadOrderRow(rd));
            return list;
        }
    }

    /// <summary>Đếm tổng đơn khớp bộ lọc (cho phân trang).</summary>
    public int CountOrders(long? shopId, string? status, string? search)
    {
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT COUNT(*) FROM orders" + WhereClause(c, shopId, status, search);
            return Convert.ToInt32(c.ExecuteScalar());
        }
    }

    /// <summary>Danh sách các trạng thái phân biệt (cho dropdown lọc).</summary>
    public List<string> DistinctOrderStatuses()
    {
        lock (_gate)
        {
            var list = new List<string>();
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT DISTINCT status FROM orders WHERE status IS NOT NULL AND status<>'' ORDER BY status";
            using var rd = c.ExecuteReader();
            while (rd.Read()) list.Add(S(rd, 0));
            return list;
        }
    }

    private static string WhereClause(SqliteCommand c, long? shopId, string? status, string? search)
    {
        var conds = new List<string>();
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
    };
}
