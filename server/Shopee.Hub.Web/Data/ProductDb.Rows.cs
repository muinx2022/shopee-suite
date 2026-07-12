using Npgsql;
using Shopee.Core.Coordination;

namespace Shopee.Hub;

/// <summary>
/// Phần ProductDb: truy vấn/ghi dòng sản phẩm — SQL viết TAY (như HubDatabase). Mở connection PER-CALL từ
/// pool <c>_dataSource</c>. "Blank" NHẤT QUÁN = NULL hoặc trim rỗng → <c>NULLIF(btrim(col),'') IS NOT NULL</c>.
/// Hai kiểu đánh số (khớp ScrapeWorkbook của Excel): dense = ROW_NUMBER() trên MỌI dòng của (acct, sheet) —
/// dòng thiếu link/tên VẪN chiếm chỗ dense, chỉ bị lọc khi TRẢ (xem GetLinksAsync); rowNo = số dòng thật.
/// </summary>
public sealed partial class ProductDb
{
    // ── Tóm tắt sheet của 1 tài khoản (LEFT JOIN metadata file nguồn) ──────────────
    // Rows = count(*) = TỔNG DỀN (mọi dòng có thật) — dùng cho TotalDataRows của scrape (khớp Excel: đếm mọi
    // dòng dữ liệu, kể cả dòng thiếu link/tên). DenseRows = số dòng HỢP LỆ (link + tên gốc non-blank) — chỉ còn
    // là thông tin (KHÔNG dùng làm tổng dòng scrape nữa).
    public async Task<List<ProductSheetInfo>> GetSheetsAsync(string acct, CancellationToken ct)
    {
        const string sql = @"
SELECT r.sheet,
       count(*)                                                                        AS rows,
       max(r.row_no)                                                                   AS last_row,
       count(*) FILTER (WHERE NULLIF(btrim(r.link),'') IS NOT NULL
                          AND NULLIF(btrim(r.name_original),'') IS NOT NULL)           AS dense_rows,
       count(*) FILTER (WHERE NULLIF(btrim(r.name_rewritten),'') IS NOT NULL)          AS rewritten_count,
       s.source_file,
       s.imported_at
FROM product_rows r
LEFT JOIN product_sheets s ON s.account_id = r.account_id AND s.sheet = r.sheet
WHERE r.account_id = $1
GROUP BY r.sheet, s.source_file, s.imported_at
ORDER BY r.sheet;";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(acct);
        var list = new List<ProductSheetInfo>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            list.Add(new ProductSheetInfo(
                Sheet: rd.GetString(0),
                Rows: (int)rd.GetInt64(1),
                LastRow: rd.IsDBNull(2) ? 0 : rd.GetInt32(2),
                DenseRows: (int)rd.GetInt64(3),
                RewrittenCount: (int)rd.GetInt64(4),
                SourceFile: rd.IsDBNull(5) ? null : rd.GetString(5),
                ImportedAt: rd.IsDBNull(6) ? null : rd.GetFieldValue<DateTimeOffset>(6)));
        return list;
    }

    // ── Danh sách row_no (tăng dần) của 1 sheet — "bản đồ dòng" cho trang Thống kê (SheetMapService nhánh Hub) ─
    // Thứ tự = ánh xạ dense p ↔ row_no[p-1], KHỚP ROW_NUMBER() OVER (ORDER BY row_no) của GetLinksAsync: MỌI dòng
    // có thật chiếm chỗ (kể cả thiếu link/tên) → giữ đúng ngữ nghĩa dense của scrape khi acc chuyển excel→hub.
    public async Task<List<int>> GetRowNosAsync(string acct, string sheet, CancellationToken ct)
    {
        const string sql = "SELECT row_no FROM product_rows WHERE account_id = $1 AND sheet = $2 ORDER BY row_no;";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(acct);
        cmd.Parameters.AddWithValue(sheet);
        var list = new List<int>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) list.Add(rd.GetInt32(0));
        return list;
    }

    // ── Link để scrape theo chỉ-số-dồn [fromDense..toDense] (toDense<=0 = đến hết) ─
    // NGỮ NGHĨA DENSE KHỚP ScrapeWorkbook (Excel): dense = ROW_NUMBER trên MỌI dòng của (acct, sheet) — dòng
    // THIẾU link/tên VẪN chiếm chỗ dense (như ws.RowsUsed() của Excel), chỉ bị LỌC ở WHERE NGOÀI subquery (sau khi
    // đã đánh số) nên không được TRẢ về — y hệt skipName/skipLink của FetchLinks. Nhờ vậy tiến độ scrape (đánh số
    // dense trong scrape-progress.json + ledger) GIỮ NGUYÊN hiệu lực khi acc chuyển excel→hub.
    public async Task<List<ProductLinkRow>> GetLinksAsync(string acct, string sheet, int fromDense, int toDense, CancellationToken ct)
    {
        const string sql = @"
SELECT dense, row_no, link, sku, name_original FROM (
  SELECT row_no, link, sku, name_original,
         ROW_NUMBER() OVER (ORDER BY row_no) AS dense
  FROM product_rows
  WHERE account_id = $1 AND sheet = $2
) t
WHERE dense >= $3 AND ($4 <= 0 OR dense <= $4)
  AND NULLIF(btrim(link),'')          IS NOT NULL
  AND NULLIF(btrim(name_original),'') IS NOT NULL
ORDER BY dense;";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(acct);
        cmd.Parameters.AddWithValue(sheet);
        cmd.Parameters.AddWithValue((long)fromDense);
        cmd.Parameters.AddWithValue((long)toDense);
        var list = new List<ProductLinkRow>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            list.Add(new ProductLinkRow((int)rd.GetInt64(0), rd.GetInt32(1), S(rd, 2), S(rd, 3), S(rd, 4)));
        return list;
    }

    // ── Dòng đã có tên-sửa để update lên BigSeller (name_rewritten non-blank AND (item_id OR link)) ──
    public async Task<List<ProductRecordRow>> GetRecordMapAsync(string acct, string sheet, int fromRow, int toRow, CancellationToken ct)
    {
        const string sql = @"
SELECT row_no, item_id, link, sku, name_rewritten, price_sale
FROM product_rows
WHERE account_id = $1 AND sheet = $2
  AND row_no >= $3 AND ($4 <= 0 OR row_no <= $4)
  AND NULLIF(btrim(name_rewritten),'') IS NOT NULL
  AND (NULLIF(btrim(item_id),'') IS NOT NULL OR NULLIF(btrim(link),'') IS NOT NULL)
ORDER BY row_no;";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(acct);
        cmd.Parameters.AddWithValue(sheet);
        cmd.Parameters.AddWithValue(fromRow);
        cmd.Parameters.AddWithValue(toRow);
        var list = new List<ProductRecordRow>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            list.Add(new ProductRecordRow(rd.GetInt32(0), S(rd, 1), S(rd, 2), S(rd, 3), S(rd, 4), S(rd, 5)));
        return list;
    }

    // ── Dòng để import (item_id non-blank HOẶC link non-blank) ─────────────────────
    public async Task<List<ProductImportIdRow>> GetImportIdsAsync(string acct, string sheet, int fromRow, int toRow, CancellationToken ct)
    {
        const string sql = @"
SELECT row_no, item_id, link
FROM product_rows
WHERE account_id = $1 AND sheet = $2
  AND row_no >= $3 AND ($4 <= 0 OR row_no <= $4)
  AND (NULLIF(btrim(item_id),'') IS NOT NULL OR NULLIF(btrim(link),'') IS NOT NULL)
ORDER BY row_no;";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(acct);
        cmd.Parameters.AddWithValue(sheet);
        cmd.Parameters.AddWithValue(fromRow);
        cmd.Parameters.AddWithValue(toRow);
        var list = new List<ProductImportIdRow>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            list.Add(new ProductImportIdRow(rd.GetInt32(0), S(rd, 1), S(rd, 2)));
        return list;
    }

    // ── Dòng CHỜ rewrite (tên gốc + SKU non-blank, tên-sửa blank) — khớp BuildPlan của ProductNameRewriteRunner ──
    public async Task<List<ProductRewritePendingRow>> GetRewritePendingAsync(string acct, string sheet, int fromRow, int toRow, CancellationToken ct)
    {
        const string sql = @"
SELECT row_no, sku, name_original
FROM product_rows
WHERE account_id = $1 AND sheet = $2
  AND row_no >= $3 AND ($4 <= 0 OR row_no <= $4)
  AND NULLIF(btrim(name_original),'')  IS NOT NULL
  AND NULLIF(btrim(sku),'')            IS NOT NULL
  AND NULLIF(btrim(name_rewritten),'') IS NULL
ORDER BY row_no;";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(acct);
        cmd.Parameters.AddWithValue(sheet);
        cmd.Parameters.AddWithValue(fromRow);
        cmd.Parameters.AddWithValue(toRow);
        var list = new List<ProductRewritePendingRow>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            list.Add(new ProductRewritePendingRow(rd.GetInt32(0), S(rd, 1), S(rd, 2)));
        return list;
    }

    // ── Ghi tên-sửa (batch, IDEMPOTENT). rowNo không tồn tại → gom vào missing ─────
    public async Task<ProductRewrittenResponse> SetRewrittenAsync(ProductRewrittenRequest req, string updatedBy, CancellationToken ct)
    {
        const string sql = @"
UPDATE product_rows
SET name_rewritten = $4, rewritten_at = now(), updated_at = now(), updated_by = $5
WHERE account_id = $1 AND sheet = $2 AND row_no = $3;";
        var updated = 0;
        var missing = new List<int>();
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var item in req.Items ?? [])
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue(req.Acct);
            cmd.Parameters.AddWithValue(req.Sheet);
            cmd.Parameters.AddWithValue(item.RowNo);
            cmd.Parameters.AddWithValue((object?)item.NameRewritten ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)updatedBy ?? DBNull.Value);
            var n = await cmd.ExecuteNonQueryAsync(ct);
            if (n > 0) updated += n; else missing.Add(item.RowNo);
        }
        await tx.CommitAsync(ct);
        return new ProductRewrittenResponse(updated, missing.ToArray());
    }

    // ── Nối N dòng vào cuối sheet: row_no = COALESCE(max,1)+1 tuần tự (bảng trống → bắt đầu dòng 2) ──
    public async Task<ProductAppendResponse> AppendRowsAsync(ProductAppendRequest req, string updatedBy, CancellationToken ct)
    {
        var rows = req.Rows ?? [];
        if (rows.Count == 0) return new ProductAppendResponse(0, 0, 0);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        int baseRow;
        await using (var q = new NpgsqlCommand(
            "SELECT COALESCE(max(row_no),1)+1 FROM product_rows WHERE account_id=$1 AND sheet=$2;", conn, tx))
        {
            q.Parameters.AddWithValue(req.Acct);
            q.Parameters.AddWithValue(req.Sheet);
            baseRow = Convert.ToInt32(await q.ExecuteScalarAsync(ct));
        }

        for (var i = 0; i < rows.Count; i++)
        {
            await using var cmd = new NpgsqlCommand(InsertRowSql, conn, tx);
            BindRow(cmd, req.Acct, req.Sheet, baseRow + i, rows[i], updatedBy);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await TouchSheetAsync(conn, tx, req.Acct, req.Sheet, sourceFile: null, setImported: false, ct);
        await tx.CommitAsync(ct);
        return new ProductAppendResponse(rows.Count, baseRow, baseRow + rows.Count - 1);
    }

    // ══ CRUD dòng cho trang web Hub (Fleet → tab 📋 Dữ liệu). Blazor gọi THẲNG in-process (không qua API HTTP). ══

    /// <summary>Mệnh đề WHERE chung cho đếm/tìm: khớp (acct, sheet) và ($3 rỗng → MỌI dòng) HOẶC ILIKE $4 trên 5
    /// cột tìm (sku, item_id, name_original, name_rewritten, link). Ký tự đặc biệt của search escape ở
    /// <see cref="EscapeLike"/>; bind qua <see cref="BindSearch"/>.</summary>
    private const string SearchWhere = @"
WHERE account_id = $1 AND sheet = $2
  AND ($3 = '' OR sku ILIKE $4 OR item_id ILIKE $4 OR name_original ILIKE $4
                 OR name_rewritten ILIKE $4 OR link ILIKE $4)";

    // ── Đếm dòng khớp tìm (search rỗng = mọi dòng) — cho phân trang ──
    public async Task<int> CountRowsAsync(string acct, string sheet, string search, CancellationToken ct)
    {
        const string sql = "SELECT count(*) FROM product_rows" + SearchWhere + ";";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        BindSearch(cmd, acct, sheet, search);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    // ── Đọc 1 trang dòng (ORDER BY row_no, LIMIT/OFFSET) khớp tìm — cho lưới CRUD ──
    public async Task<List<(int RowNo, ProductRowData Data)>> GetRowsPageAsync(
        string acct, string sheet, string search, int offset, int limit, CancellationToken ct)
    {
        const string sql = @"
SELECT row_no, link, price_original, price_sale, sku, item_id, name_original, name_rewritten,
       category, shop_name, rating, sold_month, likes, reviews, region, image, meta_shop_id, meta_item_id
FROM product_rows" + SearchWhere + @"
ORDER BY row_no
LIMIT $5 OFFSET $6;";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        BindSearch(cmd, acct, sheet, search);          // $1..$4
        cmd.Parameters.AddWithValue(limit);            // $5
        cmd.Parameters.AddWithValue(offset);           // $6
        var list = new List<(int RowNo, ProductRowData Data)>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var data = new ProductRowData(
                Link: S(rd, 1), PriceOriginal: S(rd, 2), PriceSale: S(rd, 3), Sku: S(rd, 4), ItemId: S(rd, 5),
                NameOriginal: S(rd, 6), NameRewritten: S(rd, 7), Category: S(rd, 8), ShopName: S(rd, 9),
                Rating: S(rd, 10), SoldMonth: S(rd, 11), Likes: S(rd, 12), Reviews: S(rd, 13), Region: S(rd, 14),
                Image: S(rd, 15), MetaShopId: S(rd, 16), MetaItemId: S(rd, 17));
            list.Add((rd.GetInt32(0), data));
        }
        return list;
    }

    /// <summary>UPDATE 17 cột dữ liệu + updated_at(now)/updated_by. <c>rewritten_at</c> đổi theo bước đổi tên-sửa
    /// (CASE so OLD name_rewritten với $10 mới — trong Postgres, biểu thức SET đọc GIÁ TRỊ CŨ của dòng): blank→
    /// non-blank = now(); non-blank→blank = NULL; còn lại giữ nguyên. $1..$21 TRÙNG thứ tự <see cref="BindRow"/>
    /// nên dùng lại BindRow để bind.</summary>
    private const string UpdateRowSql = @"
UPDATE product_rows SET
  link=$4, price_original=$5, price_sale=$6, sku=$7, item_id=$8,
  name_original=$9, name_rewritten=$10, category=$11, shop_name=$12, rating=$13,
  sold_month=$14, likes=$15, reviews=$16, region=$17, image=$18,
  meta_shop_id=$19, meta_item_id=$20,
  rewritten_at = CASE
    WHEN NULLIF(btrim(name_rewritten),'') IS NULL     AND NULLIF(btrim($10),'') IS NOT NULL THEN now()
    WHEN NULLIF(btrim(name_rewritten),'') IS NOT NULL AND NULLIF(btrim($10),'') IS NULL     THEN NULL
    ELSE rewritten_at
  END,
  updated_at = now(), updated_by = $21
WHERE account_id=$1 AND sheet=$2 AND row_no=$3;";

    // ── Sửa 1 dòng — trả false nếu row_no không tồn tại (0 dòng) ──
    public async Task<bool> UpdateRowAsync(string acct, string sheet, int rowNo, ProductRowData d, string updatedBy, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(UpdateRowSql, conn);
        BindRow(cmd, acct, sheet, rowNo, d, updatedBy);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // ── Xoá N dòng theo row_no (ANY($3)) — trả số dòng product_rows đã xoá ──
    public async Task<int> DeleteRowsAsync(string acct, string sheet, int[] rowNos, CancellationToken ct)
    {
        // KHÔNG đánh lại row_no cho các dòng còn lại → chấp nhận LỖ HỔNG số dòng: dense (scrape) tính động bằng
        // ROW_NUMBER() nên không vỡ; row_no (import/update/rewrite + ledger) giữ nguyên ý nghĩa dòng cũ.
        if (rowNos is null || rowNos.Length == 0) return 0;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        int deleted;
        await using (var cmd = new NpgsqlCommand(
            "DELETE FROM product_rows WHERE account_id=$1 AND sheet=$2 AND row_no = ANY($3);", conn, tx))
        {
            cmd.Parameters.AddWithValue(acct);
            cmd.Parameters.AddWithValue(sheet);
            cmd.Parameters.AddWithValue(rowNos);
            deleted = await cmd.ExecuteNonQueryAsync(ct);
        }
        // Xoá DÒNG chủ động → xoá KÈM lịch sử "đã bán" cùng vị trí (khác re-import ghi đè: re-import giữ lịch sử).
        await using (var cmd = new NpgsqlCommand(
            "DELETE FROM product_sold WHERE account_id=$1 AND sheet=$2 AND row_no = ANY($3);", conn, tx))
        {
            cmd.Parameters.AddWithValue(acct);
            cmd.Parameters.AddWithValue(sheet);
            cmd.Parameters.AddWithValue(rowNos);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return deleted;
    }

    // ── Chèn 1 dòng vào CUỐI sheet (row_no = COALESCE(max,1)+1, như AppendRowsAsync) — trả row_no vừa cấp ──
    public async Task<int> InsertRowAtEndAsync(string acct, string sheet, ProductRowData d, string updatedBy, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        int rowNo;
        await using (var q = new NpgsqlCommand(
            "SELECT COALESCE(max(row_no),1)+1 FROM product_rows WHERE account_id=$1 AND sheet=$2;", conn, tx))
        {
            q.Parameters.AddWithValue(acct);
            q.Parameters.AddWithValue(sheet);
            rowNo = Convert.ToInt32(await q.ExecuteScalarAsync(ct));
        }

        await using (var cmd = new NpgsqlCommand(InsertRowSql, conn, tx))
        {
            BindRow(cmd, acct, sheet, rowNo, d, updatedBy);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await TouchSheetAsync(conn, tx, acct, sheet, sourceFile: null, setImported: false, ct);
        await tx.CommitAsync(ct);
        return rowNo;
    }

    // ── Helper dùng chung ──────────────────────────────────────────────────────────

    /// <summary>Cột INSERT/UPSERT của 1 dòng — 3 khoá + 17 ô dữ liệu + updated_at(now) + updated_by.
    /// $1..$21 (updated_at = now() nên KHÔNG là tham số).</summary>
    internal const string InsertRowSql = @"
INSERT INTO product_rows
 (account_id, sheet, row_no, link, price_original, price_sale, sku, item_id,
  name_original, name_rewritten, category, shop_name, rating, sold_month, likes,
  reviews, region, image, meta_shop_id, meta_item_id, updated_at, updated_by)
VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20, now(), $21)";

    /// <summary>Hậu tố UPSERT: đè TOÀN BỘ 17 cột dữ liệu + updated_at/updated_by (rewritten_at do endpoint
    /// rewrite quản, KHÔNG đụng ở đây).</summary>
    internal const string OnConflictUpdateSql = @"
ON CONFLICT (account_id, sheet, row_no) DO UPDATE SET
  link=EXCLUDED.link, price_original=EXCLUDED.price_original, price_sale=EXCLUDED.price_sale,
  sku=EXCLUDED.sku, item_id=EXCLUDED.item_id, name_original=EXCLUDED.name_original,
  name_rewritten=EXCLUDED.name_rewritten, category=EXCLUDED.category, shop_name=EXCLUDED.shop_name,
  rating=EXCLUDED.rating, sold_month=EXCLUDED.sold_month, likes=EXCLUDED.likes,
  reviews=EXCLUDED.reviews, region=EXCLUDED.region, image=EXCLUDED.image,
  meta_shop_id=EXCLUDED.meta_shop_id, meta_item_id=EXCLUDED.meta_item_id,
  updated_at=now(), updated_by=EXCLUDED.updated_by";

    /// <summary>Gắn 21 tham số cho <see cref="InsertRowSql"/> (theo ĐÚNG thứ tự $1..$21). Ô rỗng → NULL.</summary>
    internal static void BindRow(NpgsqlCommand cmd, string acct, string sheet, int rowNo, ProductRowData d, string updatedBy)
    {
        cmd.Parameters.AddWithValue(acct);                 // $1
        cmd.Parameters.AddWithValue(sheet);                // $2
        cmd.Parameters.AddWithValue(rowNo);                // $3
        cmd.Parameters.AddWithValue(N(d.Link));            // $4
        cmd.Parameters.AddWithValue(N(d.PriceOriginal));   // $5
        cmd.Parameters.AddWithValue(N(d.PriceSale));       // $6
        cmd.Parameters.AddWithValue(N(d.Sku));             // $7
        cmd.Parameters.AddWithValue(N(d.ItemId));          // $8
        cmd.Parameters.AddWithValue(N(d.NameOriginal));    // $9
        cmd.Parameters.AddWithValue(N(d.NameRewritten));   // $10
        cmd.Parameters.AddWithValue(N(d.Category));        // $11
        cmd.Parameters.AddWithValue(N(d.ShopName));        // $12
        cmd.Parameters.AddWithValue(N(d.Rating));          // $13
        cmd.Parameters.AddWithValue(N(d.SoldMonth));       // $14
        cmd.Parameters.AddWithValue(N(d.Likes));           // $15
        cmd.Parameters.AddWithValue(N(d.Reviews));         // $16
        cmd.Parameters.AddWithValue(N(d.Region));          // $17
        cmd.Parameters.AddWithValue(N(d.Image));           // $18
        cmd.Parameters.AddWithValue(N(d.MetaShopId));      // $19
        cmd.Parameters.AddWithValue(N(d.MetaItemId));      // $20
        cmd.Parameters.AddWithValue((object?)updatedBy ?? DBNull.Value);  // $21
    }

    /// <summary>Ghi/chạm product_sheets. setImported=true → đặt source_file + imported_at (import); ngược lại
    /// chỉ chạm updated_at (append) và GIỮ source_file/imported_at cũ.</summary>
    internal static async Task TouchSheetAsync(NpgsqlConnection conn, NpgsqlTransaction tx,
        string acct, string sheet, string? sourceFile, bool setImported, CancellationToken ct)
    {
        var sql = setImported
            ? @"INSERT INTO product_sheets(account_id, sheet, source_file, imported_at, updated_at)
                VALUES($1,$2,$3,now(),now())
                ON CONFLICT(account_id,sheet) DO UPDATE SET source_file=EXCLUDED.source_file, imported_at=now(), updated_at=now();"
            : @"INSERT INTO product_sheets(account_id, sheet, updated_at)
                VALUES($1,$2,now())
                ON CONFLICT(account_id,sheet) DO UPDATE SET updated_at=now();";
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue(acct);
        cmd.Parameters.AddWithValue(sheet);
        if (setImported) cmd.Parameters.AddWithValue((object?)sourceFile ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Bind $1..$4 cho <see cref="SearchWhere"/>: acct, sheet, search THÔ (để check rỗng), pattern ILIKE.</summary>
    private static void BindSearch(NpgsqlCommand cmd, string acct, string sheet, string search)
    {
        var s = search ?? "";
        cmd.Parameters.AddWithValue(acct);                       // $1
        cmd.Parameters.AddWithValue(sheet);                      // $2
        cmd.Parameters.AddWithValue(s);                          // $3 (rỗng → mọi dòng)
        cmd.Parameters.AddWithValue("%" + EscapeLike(s) + "%");  // $4 pattern ILIKE
    }

    /// <summary>Escape ký tự đặc biệt của LIKE/ILIKE (\ % _) → literal (escape mặc định Postgres = \). Thay \ TRƯỚC
    /// để không nhân đôi các \ vừa chèn cho % và _.</summary>
    private static string EscapeLike(string s) => s
        .Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>Text hoặc NULL (ô rỗng → NULL cho bảng gọn; check blank vẫn qua NULLIF(btrim…)).</summary>
    private static object N(string? s) => string.IsNullOrEmpty(s) ? DBNull.Value : s;

    /// <summary>Đọc cột text nullable → chuỗi rỗng khi NULL.</summary>
    private static string S(NpgsqlDataReader rd, int i) => rd.IsDBNull(i) ? "" : rd.GetString(i);
}
