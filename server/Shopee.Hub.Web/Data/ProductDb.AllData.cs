using Npgsql;

namespace Shopee.Hub;

/// <summary>Bộ lọc trang "📦 Dữ liệu" (mọi shop): mỗi field null/blank/0 = KHÔNG lọc chiều đó.
/// <see cref="PriceMin"/>/<see cref="PriceMax"/> so trên SỐ tách từ text price_sale (dòng không parse được bị loại
/// khi có lọc giá). <see cref="SoldOnly"/> = chỉ dòng đã có bản ghi product_sold (sold_count &gt; 0).</summary>
public sealed record AllDataFilter(
    string? Acct, string? Sheet, string? Sku, long? PriceMin, long? PriceMax, bool SoldOnly);

/// <summary>1 dòng cho lưới "📦 Dữ liệu": khoá vị trí (AccountId, Sheet, RowNo) + ô hiển thị + số đã bán (0 nếu chưa
/// có bản ghi product_sold).</summary>
public sealed record AllDataRow(
    string AccountId, string Sheet, int RowNo, string Link, string PriceSale, string Sku, string ItemId,
    string NameOriginal, string NameRewritten, int SoldCount, DateTimeOffset UpdatedAt);

/// <summary>
/// Phần ProductDb cho trang web "📦 Dữ liệu" — truy vấn LIÊN-SHOP (mọi account_id/sheet) + đánh dấu "đã bán" +
/// xoá dòng theo khoá vị trí. SQL viết TAY như các partial khác. Số tham số CỐ ĐỊNH ($1..$7 cho filter, dạng
/// <c>($n = '' OR …)</c> như <see cref="SearchWhere"/>) để khỏi dựng SQL động.
/// </summary>
public sealed partial class ProductDb
{
    // product_rows r + lịch sử bán s (LEFT JOIN: dòng chưa bán → s.* NULL). USING gộp 3 khoá vị trí.
    private const string AllFrom = @"
FROM product_rows r
LEFT JOIN product_sold s USING (account_id, sheet, row_no)";

    // Điều kiện lọc CỐ ĐỊNH 7 tham số ($1 acct, $2 sheet, $3 sku thô, $4 sku pattern ILIKE, $5 giá min, $6 giá max,
    // $7 chỉ-đã-bán). Mỗi vế tự vô hiệu khi tham số "trống" ('' cho text, <=0 cho giá, false cho $7). Giá: tách chữ
    // số khỏi price_sale rồi ép bigint — dòng KHÔNG có chữ số → NULL → bị loại (NULL không thoả >= / <=) khi lọc giá.
    // '\D' để nguyên nhờ verbatim string (@"") — KHÔNG được viết chuỗi thường kẻo \D thành escape lỗi.
    private const string AllWhere = @"
WHERE ($1 = '' OR r.account_id = $1)
  AND ($2 = '' OR r.sheet = $2)
  AND ($3 = '' OR r.sku ILIKE $4)
  AND ($5 <= 0 OR NULLIF(regexp_replace(r.price_sale, '\D', '', 'g'), '')::bigint >= $5)
  AND ($6 <= 0 OR NULLIF(regexp_replace(r.price_sale, '\D', '', 'g'), '')::bigint <= $6)
  AND (NOT $7 OR COALESCE(s.sold_count, 0) > 0)";

    // ── Đếm dòng khớp lọc (cho phân trang) ──
    public async Task<int> CountAllAsync(AllDataFilter f, CancellationToken ct)
    {
        const string sql = "SELECT count(*)" + AllFrom + AllWhere + ";";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        BindAll(cmd, f);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    // ── Đọc 1 trang dòng khớp lọc (ORDER BY khoá vị trí, LIMIT/OFFSET) ──
    public async Task<List<AllDataRow>> QueryAllAsync(AllDataFilter f, int offset, int limit, CancellationToken ct)
    {
        const string sql = @"
SELECT r.account_id, r.sheet, r.row_no, r.link, r.price_sale, r.sku, r.item_id,
       r.name_original, r.name_rewritten, COALESCE(s.sold_count, 0) AS sold_count, r.updated_at"
            + AllFrom + AllWhere + @"
ORDER BY r.account_id, r.sheet, r.row_no
LIMIT $8 OFFSET $9;";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        BindAll(cmd, f);                        // $1..$7
        cmd.Parameters.AddWithValue(limit);     // $8
        cmd.Parameters.AddWithValue(offset);    // $9
        var list = new List<AllDataRow>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            list.Add(new AllDataRow(
                AccountId: rd.GetString(0),
                Sheet: rd.GetString(1),
                RowNo: rd.GetInt32(2),
                Link: S(rd, 3),
                PriceSale: S(rd, 4),
                Sku: S(rd, 5),
                ItemId: S(rd, 6),
                NameOriginal: S(rd, 7),
                NameRewritten: S(rd, 8),
                SoldCount: rd.GetInt32(9),
                UpdatedAt: rd.GetFieldValue<DateTimeOffset>(10)));
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

    /// <summary>Bind $1..$7 cho <see cref="AllWhere"/>: acct/sheet/sku thô (check '' → bỏ lọc), pattern ILIKE
    /// (<see cref="EscapeLike"/>), giá min/max (null/≤0 → bỏ), cờ chỉ-đã-bán.</summary>
    private static void BindAll(NpgsqlCommand cmd, AllDataFilter f)
    {
        var sku = (f.Sku ?? "").Trim();
        cmd.Parameters.AddWithValue(f.Acct ?? "");                    // $1
        cmd.Parameters.AddWithValue(f.Sheet ?? "");                   // $2
        cmd.Parameters.AddWithValue(sku);                            // $3 (rỗng → bỏ lọc sku)
        cmd.Parameters.AddWithValue("%" + EscapeLike(sku) + "%");    // $4 pattern ILIKE
        cmd.Parameters.AddWithValue(f.PriceMin ?? 0L);               // $5 (≤0 → bỏ lọc min)
        cmd.Parameters.AddWithValue(f.PriceMax ?? 0L);               // $6 (≤0 → bỏ lọc max)
        cmd.Parameters.AddWithValue(f.SoldOnly);                     // $7
    }
}
