using Npgsql;
using Shopee.Core.Coordination;

namespace Shopee.Hub;

/// <summary>
/// Phần ProductDb: import/export workbook xlsx. Parse/ghi Excel nằm TRỌN ở <see cref="ProductXlsxCodec"/> (hàm
/// thuần, test không cần DB); ở đây CHỈ đổ codec ↔ SQL. Mỗi sheet 1 transaction (replace = xoá rồi chèn;
/// upsert = ON CONFLICT đè cột dữ liệu).
/// </summary>
public sealed partial class ProductDb
{
    // ── Import: body = bytes xlsx; đọc MỌI worksheet ───────────────────────────────
    public async Task<ProductImportResult> ImportXlsxAsync(
        string acct, string mode, string? sourceFile, byte[] xlsx,
        ProductXlsxCodec.ColumnOverrides overrides, string updatedBy, CancellationToken ct)
    {
        var replace = !string.Equals(mode, "upsert", StringComparison.OrdinalIgnoreCase);   // mặc định replace
        var insertSql = replace ? InsertRowSql : InsertRowSql + "\n" + OnConflictUpdateSql;

        List<(string Sheet, List<ProductXlsxCodec.ParsedRow> Rows)> parsed;
        using (var ms = new MemoryStream(xlsx))
            parsed = ProductXlsxCodec.Parse(ms, overrides);

        var result = new List<ProductSheetImport>();
        var total = 0;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        foreach (var (sheet, rows) in parsed)
        {
            await using var tx = await conn.BeginTransactionAsync(ct);

            if (replace)
            {
                await using var del = new NpgsqlCommand(
                    "DELETE FROM product_rows WHERE account_id=$1 AND sheet=$2;", conn, tx);
                del.Parameters.AddWithValue(acct);
                del.Parameters.AddWithValue(sheet);
                await del.ExecuteNonQueryAsync(ct);
            }

            foreach (var pr in rows)
            {
                await using var cmd = new NpgsqlCommand(insertSql, conn, tx);
                BindRow(cmd, acct, sheet, pr.RowNo, pr.Data, updatedBy);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await TouchSheetAsync(conn, tx, acct, sheet, sourceFile, setImported: true, ct);
            await tx.CommitAsync(ct);

            result.Add(new ProductSheetImport(sheet, rows.Count));
            total += rows.Count;
        }

        return new ProductImportResult(result, total);
    }

    // ── Export: sheet rỗng = mọi sheet của acc; đặt dữ liệu đúng row_no (lỗ hổng để trống) ──
    public async Task<(byte[] Bytes, string FileName)> ExportXlsxAsync(string acct, string? sheet, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Danh sách sheet cần xuất.
        var sheets = new List<string>();
        if (!string.IsNullOrEmpty(sheet))
            sheets.Add(sheet);
        else
        {
            await using var q = new NpgsqlCommand(
                "SELECT DISTINCT sheet FROM product_rows WHERE account_id=$1 ORDER BY sheet;", conn);
            q.Parameters.AddWithValue(acct);
            await using var rd = await q.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) sheets.Add(rd.GetString(0));
        }

        var data = new List<(string Sheet, IReadOnlyList<ProductXlsxCodec.ParsedRow> Rows)>();
        foreach (var sh in sheets)
            data.Add((sh, await ReadSheetRowsAsync(conn, acct, sh, ct)));

        var bytes = ProductXlsxCodec.Write(data);
        var fileName = await ResolveSourceFileAsync(conn, acct, sheet, ct) ?? $"{acct}.xlsx";
        return (bytes, fileName);
    }

    private static async Task<List<ProductXlsxCodec.ParsedRow>> ReadSheetRowsAsync(
        NpgsqlConnection conn, string acct, string sheet, CancellationToken ct)
    {
        const string sql = @"
SELECT row_no, link, price_original, price_sale, sku, item_id, name_original, name_rewritten,
       category, shop_name, rating, sold_month, likes, reviews, region, image, meta_shop_id, meta_item_id
FROM product_rows
WHERE account_id=$1 AND sheet=$2
ORDER BY row_no;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(acct);
        cmd.Parameters.AddWithValue(sheet);
        var rows = new List<ProductXlsxCodec.ParsedRow>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var data = new ProductRowData(
                Link: S(rd, 1), PriceOriginal: S(rd, 2), PriceSale: S(rd, 3), Sku: S(rd, 4), ItemId: S(rd, 5),
                NameOriginal: S(rd, 6), NameRewritten: S(rd, 7), Category: S(rd, 8), ShopName: S(rd, 9),
                Rating: S(rd, 10), SoldMonth: S(rd, 11), Likes: S(rd, 12), Reviews: S(rd, 13), Region: S(rd, 14),
                Image: S(rd, 15), MetaShopId: S(rd, 16), MetaItemId: S(rd, 17));
            rows.Add(new ProductXlsxCodec.ParsedRow(rd.GetInt32(0), data));
        }
        return rows;
    }

    private static async Task<string?> ResolveSourceFileAsync(NpgsqlConnection conn, string acct, string? sheet, CancellationToken ct)
    {
        const string sql = @"
SELECT source_file FROM product_sheets
WHERE account_id=$1 AND ($2='' OR sheet=$2) AND source_file IS NOT NULL
LIMIT 1;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(acct);
        cmd.Parameters.AddWithValue(sheet ?? "");
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is string s && !string.IsNullOrWhiteSpace(s) ? s : null;
    }
}
