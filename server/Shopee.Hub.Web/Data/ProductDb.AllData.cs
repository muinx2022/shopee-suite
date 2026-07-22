using Npgsql;
using Shopee.Core.Coordination;

namespace Shopee.Hub;

/// <summary>
/// Phần ProductDb cho trang web "📦 Dữ liệu" — truy vấn LIÊN-SHOP (mọi account_id/sheet) + đánh dấu "đã bán" +
/// xoá dòng theo khoá vị trí. SQL viết TAY như các partial khác. Số tham số CỐ ĐỊNH ($1..$10 cho filter, dạng
/// <c>($n = '' OR …)</c>) để khỏi dựng SQL động.
/// </summary>
public sealed partial class ProductDb
{
    // product_rows r + lịch sử bán s (LEFT JOIN: dòng chưa bán → s.* NULL). USING gộp 3 khoá vị trí.
    private const string AllFrom = @"
FROM product_rows r
LEFT JOIN product_sold s USING (account_id, sheet, row_no)";

    // Điều kiện lọc CỐ ĐỊNH 10 tham số ($1 acct, $2 sheet, $3 sku thô, $4 sku pattern ILIKE, $5 giá min, $6 giá max,
    // $7 chỉ-đã-bán, $8 chỉ-SKU-trùng-trong-shop, $9 text thô, $10 text pattern ILIKE). Mỗi vế tự vô hiệu khi tham số
    // "trống" ('' cho text, <=0 cho giá, false cho $7/$8). Giá: tách chữ số khỏi price_sale rồi ép bigint — dòng KHÔNG
    // có chữ số → NULL → bị loại (NULL không thoả >= / <=) khi lọc giá. '\D' để nguyên nhờ verbatim string (@"") —
    // KHÔNG được viết chuỗi thường kẻo \D thành escape lỗi. $8 (SKU trùng): SKU non-blank + tồn tại dòng KHÁC cùng
    // (acct, sheet) có btrim(sku) bằng. $9/$10 (tìm đa trường): chứa text trên BẤT KỲ sku/item_id/tên gốc/tên sửa/link
    // → phục vụ lưới per-shop (tab 📋 Dữ liệu Fleet) qua CHUNG đường AllData thay cho SearchWhere cũ.
    private const string AllWhere = @"
WHERE ($1 = '' OR r.account_id = $1)
  AND ($2 = '' OR r.sheet = $2)
  AND ($3 = '' OR r.sku ILIKE $4)
  AND ($5 <= 0 OR NULLIF(regexp_replace(r.price_sale, '\D', '', 'g'), '')::bigint >= $5)
  AND ($6 <= 0 OR NULLIF(regexp_replace(r.price_sale, '\D', '', 'g'), '')::bigint <= $6)
  AND (NOT $7 OR COALESCE(s.sold_count, 0) > 0)
  AND (NOT $8 OR (NULLIF(btrim(r.sku),'') IS NOT NULL AND EXISTS (
        SELECT 1 FROM product_rows r2
        WHERE r2.account_id = r.account_id AND r2.sheet = r.sheet
          AND btrim(r2.sku) = btrim(r.sku) AND r2.row_no <> r.row_no)))
  AND ($9 = '' OR r.sku ILIKE $10 OR r.item_id ILIKE $10 OR r.name_original ILIKE $10
                 OR r.name_rewritten ILIKE $10 OR r.link ILIKE $10)";

    // ── Đếm dòng khớp lọc (cho phân trang) ──
    public async Task<int> CountAllAsync(AllDataFilter f, CancellationToken ct)
    {
        const string sql = "SELECT count(*)" + AllFrom + AllWhere + ";";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        BindAll(cmd, f);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    // ── Đọc 1 trang dòng khớp lọc (ĐỦ 17 cột để sửa inline; LIMIT/OFFSET) ──
    // ORDER BY: khi $8 (SKU trùng) bật → gom theo btrim(sku) cho các dòng trùng đứng cạnh nhau; $8 tắt → CASE trả NULL
    // (mọi dòng bằng nhau ở vế đó) nên rơi về sắp theo row_no như cũ.
    public async Task<List<AllDataRow>> QueryAllAsync(AllDataFilter f, int offset, int limit, CancellationToken ct)
    {
        const string sql = @"
SELECT r.account_id, r.sheet, r.row_no,
       r.link, r.price_original, r.price_sale, r.sku, r.item_id, r.name_original, r.name_rewritten,
       r.category, r.shop_name, r.rating, r.sold_month, r.likes, r.reviews, r.region, r.image,
       r.meta_shop_id, r.meta_item_id,
       COALESCE(s.sold_count, 0) AS sold_count, r.updated_at"
            + AllFrom + AllWhere + @"
ORDER BY r.account_id, r.sheet, CASE WHEN $8 THEN btrim(r.sku) END, r.row_no
LIMIT $11 OFFSET $12;";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        BindAll(cmd, f);                        // $1..$10
        cmd.Parameters.AddWithValue(limit);     // $11
        cmd.Parameters.AddWithValue(offset);    // $12
        var list = new List<AllDataRow>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var data = new ProductRowData(
                Link: S(rd, 3), PriceOriginal: S(rd, 4), PriceSale: S(rd, 5), Sku: S(rd, 6), ItemId: S(rd, 7),
                NameOriginal: S(rd, 8), NameRewritten: S(rd, 9), Category: S(rd, 10), ShopName: S(rd, 11),
                Rating: S(rd, 12), SoldMonth: S(rd, 13), Likes: S(rd, 14), Reviews: S(rd, 15), Region: S(rd, 16),
                Image: S(rd, 17), MetaShopId: S(rd, 18), MetaItemId: S(rd, 19));
            list.Add(new AllDataRow(
                AccountId: rd.GetString(0),
                Sheet: rd.GetString(1),
                RowNo: rd.GetInt32(2),
                Data: data,
                SoldCount: rd.GetInt32(20),
                UpdatedAt: rd.GetFieldValue<DateTimeOffset>(21)));
        }
        return list;
    }

    /// <summary>Đánh dấu "đã bán" cho các khoá vị trí (1 transaction). Mỗi khoá: chưa có → chèn sold_count=1 +
    /// first/last_sold_at=now; đã có → sold_count+1 + last_sold_at=now (GIỮ NGUYÊN first_sold_at). Trả số khoá xử lý.</summary>
    public async Task<int> MarkSoldAsync(List<(string Acct, string Sheet, int RowNo)> keys, CancellationToken ct)
    {
        if (keys is null || keys.Count == 0) return 0;
        const string sql = @"
INSERT INTO product_sold (account_id, sheet, row_no, sold_count, first_sold_at, last_sold_at)
VALUES ($1, $2, $3, 1, now(), now())
ON CONFLICT (account_id, sheet, row_no) DO UPDATE SET
  sold_count = product_sold.sold_count + 1,
  last_sold_at = now();";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var n = 0;
        foreach (var (acct, sheet, rowNo) in keys)
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue(acct);
            cmd.Parameters.AddWithValue(sheet);
            cmd.Parameters.AddWithValue(rowNo);
            await cmd.ExecuteNonQueryAsync(ct);
            n++;
        }
        await tx.CommitAsync(ct);
        return n;
    }

    /// <summary>+1 "đã bán" cho MỌI dòng có <c>btrim(sku)</c> KHỚP TUYỆT ĐỐI (KHÔNG ILIKE) — LIÊN-SHOP (mọi
    /// account_id/sheet; chưa phân shop). Mỗi phần tử trong <paramref name="skus"/> = 1 đơn "đã giao" nên +1 cho
    /// từng dòng khớp (đơn TRÙNG SKU → SKU lặp trong danh sách → +N tổng, đúng "+1 mỗi đơn"). SKU blank → bỏ qua.
    /// Mỗi khoá vị trí: chưa có bản ghi product_sold → chèn sold_count=1 + first/last_sold_at=now; đã có → sold_count+1
    /// + last_sold_at=now (GIỮ first_sold_at). 1 transaction. Trả TỔNG số dòng product_sold đã +1 (log/telemetry;
    /// SKU không khớp dòng nào → +0, KHÔNG lỗi). Sau này scope per-shop = thêm điều kiện (account_id, sheet).</summary>
    public async Task<int> MarkSoldBySkuAsync(IReadOnlyList<string> skus, CancellationToken ct)
    {
        if (skus is null || skus.Count == 0) return 0;
        const string sql = @"
INSERT INTO product_sold (account_id, sheet, row_no, sold_count, first_sold_at, last_sold_at)
SELECT account_id, sheet, row_no, 1, now(), now() FROM product_rows WHERE btrim(sku) = $1
ON CONFLICT (account_id, sheet, row_no) DO UPDATE SET
  sold_count = product_sold.sold_count + 1,
  last_sold_at = now();";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var affected = 0;
        foreach (var raw in skus)
        {
            var sku = (raw ?? "").Trim();
            if (sku.Length == 0) continue;                 // đơn không có SKU → bỏ qua (không +1)
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue(sku);
            affected += await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return affected;
    }

    /// <summary>Đặt "đã bán" về 0 cho các khoá vị trí (1 transaction) — gộp theo (acct, sheet) rồi
    /// <c>DELETE FROM product_sold WHERE … row_no = ANY(…)</c> (như nhánh product_sold của <see cref="DeleteRowsByKeysAsync"/>,
    /// nhưng GIỮ product_rows). Xoá bản ghi = xoá cả lịch sử first/last_sold_at → lưới đọc COALESCE(sold_count,0)=0.
    /// Trả TỔNG số bản ghi product_sold đã xoá (dòng CHƯA TỪNG bán không có bản ghi → không tính vào tổng).</summary>
    public async Task<int> ResetSoldAsync(List<(string Acct, string Sheet, int RowNo)> keys, CancellationToken ct)
    {
        if (keys is null || keys.Count == 0) return 0;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var deleted = 0;
        foreach (var g in keys.GroupBy(k => (k.Acct, k.Sheet)))
        {
            var rowNos = g.Select(k => k.RowNo).ToArray();
            await using var cmd = new NpgsqlCommand(
                "DELETE FROM product_sold WHERE account_id=$1 AND sheet=$2 AND row_no = ANY($3);", conn, tx);
            cmd.Parameters.AddWithValue(g.Key.Acct);
            cmd.Parameters.AddWithValue(g.Key.Sheet);
            cmd.Parameters.AddWithValue(rowNos);
            deleted += await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return deleted;
    }

    /// <summary>Xoá các dòng theo khoá vị trí (1 transaction) — gộp theo (acct, sheet) rồi xoá <c>row_no = ANY(…)</c>
    /// trên CẢ product_rows LẪN product_sold (xoá dòng chủ động → xoá kèm lịch sử bán). Trả tổng dòng product_rows đã xoá.</summary>
    public async Task<int> DeleteRowsByKeysAsync(List<(string Acct, string Sheet, int RowNo)> keys, CancellationToken ct)
    {
        if (keys is null || keys.Count == 0) return 0;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var deleted = 0;
        foreach (var g in keys.GroupBy(k => (k.Acct, k.Sheet)))
        {
            var rowNos = g.Select(k => k.RowNo).ToArray();
            await using (var cmd = new NpgsqlCommand(
                "DELETE FROM product_rows WHERE account_id=$1 AND sheet=$2 AND row_no = ANY($3);", conn, tx))
            {
                cmd.Parameters.AddWithValue(g.Key.Acct);
                cmd.Parameters.AddWithValue(g.Key.Sheet);
                cmd.Parameters.AddWithValue(rowNos);
                deleted += await cmd.ExecuteNonQueryAsync(ct);
            }
            await using (var cmd = new NpgsqlCommand(
                "DELETE FROM product_sold WHERE account_id=$1 AND sheet=$2 AND row_no = ANY($3);", conn, tx))
            {
                cmd.Parameters.AddWithValue(g.Key.Acct);
                cmd.Parameters.AddWithValue(g.Key.Sheet);
                cmd.Parameters.AddWithValue(rowNos);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        await tx.CommitAsync(ct);
        return deleted;
    }

    /// <summary>Bind $1..$10 cho <see cref="AllWhere"/>: acct/sheet/sku thô (check '' → bỏ lọc), pattern ILIKE
    /// (<see cref="EscapeLike"/>), giá min/max (null/≤0 → bỏ), cờ chỉ-đã-bán, cờ chỉ-SKU-trùng, text thô + pattern
    /// ILIKE tìm đa trường (rỗng → bỏ lọc).</summary>
    private static void BindAll(NpgsqlCommand cmd, AllDataFilter f)
    {
        var sku = (f.Sku ?? "").Trim();
        var text = (f.Text ?? "").Trim();
        cmd.Parameters.AddWithValue(f.Acct ?? "");                    // $1
        cmd.Parameters.AddWithValue(f.Sheet ?? "");                   // $2
        cmd.Parameters.AddWithValue(sku);                            // $3 (rỗng → bỏ lọc sku)
        cmd.Parameters.AddWithValue("%" + EscapeLike(sku) + "%");    // $4 pattern ILIKE
        cmd.Parameters.AddWithValue(f.PriceMin ?? 0L);               // $5 (≤0 → bỏ lọc min)
        cmd.Parameters.AddWithValue(f.PriceMax ?? 0L);               // $6 (≤0 → bỏ lọc max)
        cmd.Parameters.AddWithValue(f.SoldOnly);                     // $7
        cmd.Parameters.AddWithValue(f.DupSkuOnly);                   // $8
        cmd.Parameters.AddWithValue(text);                          // $9 (rỗng → bỏ lọc tìm đa trường)
        cmd.Parameters.AddWithValue("%" + EscapeLike(text) + "%");  // $10 pattern ILIKE tìm đa trường
    }
}
