using Npgsql;
using Shopee.Core.Coordination;
using UpdateProduct;   // BigSellerText.TruncateProductNamePreservingSku (file LINK từ suite\ — xem csproj)

namespace Shopee.Hub;

/// <summary>
/// Phần ProductDb: quy tắc SKU per-shop. SKU = 'B' + 5 số ngẫu nhiên, DUY NHẤT trong (account_id, sheet) — trùng
/// GIỮA các shop là hợp lệ (cùng catalog nhiều shop). SKU ghép vào ĐUÔI name_rewritten (runner tạo bằng
/// <see cref="BigSellerText.TruncateProductNamePreservingSku"/>) nên đổi SKU của dòng có tên-sửa phải vá đuôi.
/// Sinh mã app-side (Random OK — code runtime server, không phải workflow script). SQL viết TAY như các partial khác.
/// </summary>
public sealed partial class ProductDb
{
    private const int SkuSpace = 100_000;          // B00000..B99999
    private const int RewrittenMaxLen = 120;       // trần tên-sửa (khớp ProductNameRewriteRunner)

    // ── Sinh mã (pure) ─────────────────────────────────────────────────────────────

    /// <summary>Sinh <paramref name="count"/> mã SKU 'B'+5 số, KHÔNG trùng <paramref name="existing"/> lẫn nhau.
    /// PURE (không đụng DB) → test độc lập được. Không đủ chỗ trong không gian 100k mã → ném.</summary>
    public static List<string> NewSkuCodes(IEnumerable<string> existing, int count)
    {
        var set = new HashSet<string>(
            (existing ?? Enumerable.Empty<string>()).Select(s => (s ?? "").Trim()).Where(s => s.Length > 0),
            StringComparer.Ordinal);
        return MakeUniqueSkus(set, count);
    }

    /// <summary>Lõi sinh mã: thêm <paramref name="count"/> mã mới vào (và trả từ) — MUTATE <paramref name="existing"/>
    /// để gọi nối tiếp vẫn duy nhất. Không gian 100k mã; kẹp trước để lỗi rõ khi shop gần đầy.</summary>
    private static List<string> MakeUniqueSkus(HashSet<string> existing, int count)
    {
        if (count <= 0) return new();
        if (count > SkuSpace - existing.Count)
            throw new InvalidOperationException(
                $"Shop đã dùng {existing.Count:N0} mã SKU 'B#####' — không đủ chỗ sinh thêm {count:N0} (tối đa {SkuSpace:N0}).");
        var result = new List<string>(count);
        while (result.Count < count)
        {
            var sku = "B" + Random.Shared.Next(0, SkuSpace).ToString("D5");
            if (existing.Add(sku)) result.Add(sku);   // Add=false → trùng, bốc mã khác
        }
        return result;
    }

    /// <summary>Vá đuôi name_rewritten khi đổi SKU: đuôi (sau trim) đúng bằng <paramref name="oldSku"/> → dựng lại
    /// "{tiêu đề} {newSku}" rồi cắt ≤120 giữ SKU (REUSE cùng hàm runner → khớp byte). Đuôi KHÔNG khớp SKU cũ (tên
    /// sửa tay) hoặc tên-sửa rỗng / SKU cũ rỗng → GIỮ NGUYÊN tên (chỉ đổi cột sku).</summary>
    internal static string PatchSkuTail(string? rewritten, string? oldSku, string? newSku)
    {
        var name = (rewritten ?? "").TrimEnd();
        var os = (oldSku ?? "").Trim();
        var ns = (newSku ?? "").Trim();
        if (name.Length == 0 || os.Length == 0) return rewritten ?? "";
        if (!name.EndsWith(os, StringComparison.Ordinal)) return rewritten ?? "";
        var body = name[..^os.Length].Trim();
        return BigSellerText.TruncateProductNamePreservingSku($"{body} {ns}".Trim(), ns, RewrittenMaxLen);
    }

    // ── Đọc tập SKU hiện có của 1 shop (dùng chung; tx null = ngoài transaction) ─────
    private static async Task<HashSet<string>> ReadSkuSetAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, string acct, string sheet, CancellationToken ct)
    {
        const string sql = "SELECT DISTINCT btrim(sku) FROM product_rows " +
                           "WHERE account_id=$1 AND sheet=$2 AND NULLIF(btrim(sku),'') IS NOT NULL;";
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue(acct);
        cmd.Parameters.AddWithValue(sheet);
        var set = new HashSet<string>(StringComparer.Ordinal);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            if (!rd.IsDBNull(0)) set.Add(rd.GetString(0));
        return set;
    }

    /// <summary>Sinh <paramref name="count"/> SKU mới CHƯA có trong shop (đọc tập hiện có + <see cref="MakeUniqueSkus"/>).</summary>
    public async Task<List<string>> GenerateSkusAsync(string acct, string sheet, int count, CancellationToken ct)
    {
        if (count <= 0) return new();
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var set = await ReadSkuSetAsync(conn, null, acct, sheet, ct);
        return MakeUniqueSkus(set, count);
    }

    /// <summary>Có dòng KHÁC trong shop (loại <paramref name="excludeRowNo"/>) mang cùng SKU non-blank? — cảnh báo
    /// TRƯỚC khi lưu để user xác nhận (index sẽ chặn nếu vẫn cố). SKU blank → false (không kiểm).</summary>
    public async Task<bool> ExistsSkuInShopAsync(string acct, string sheet, string sku, int excludeRowNo, CancellationToken ct)
    {
        var s = (sku ?? "").Trim();
        if (s.Length == 0) return false;
        const string sql = "SELECT EXISTS (SELECT 1 FROM product_rows " +
                           "WHERE account_id=$1 AND sheet=$2 AND btrim(sku)=$3 AND row_no<>$4);";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(acct);
        cmd.Parameters.AddWithValue(sheet);
        cmd.Parameters.AddWithValue(s);
        cmd.Parameters.AddWithValue(excludeRowNo);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Điền SKU cho các dòng THIẾU (blank) trong <paramref name="rows"/> bằng mã mới, KHÔNG trùng SKU đã có
    /// trong shop LẪN SKU sẵn có trong chính <paramref name="rows"/>. Trả (rows đã điền, số dòng được cấp). Dùng khi
    /// import Excel → dòng file thiếu SKU vẫn đạt chuẩn per-shop.</summary>
    public async Task<(List<ProductXlsxCodec.ParsedRow> Rows, int Filled)> FillMissingSkusAsync(
        string acct, string sheet, List<ProductXlsxCodec.ParsedRow> rows, CancellationToken ct)
    {
        if (rows is null || rows.Count == 0) return (rows ?? new(), 0);
        var blankIdx = new List<int>();
        for (var i = 0; i < rows.Count; i++)
            if ((rows[i].Data.Sku ?? "").Trim().Length == 0) blankIdx.Add(i);
        if (blankIdx.Count == 0) return (rows, 0);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var set = await ReadSkuSetAsync(conn, null, acct, sheet, ct);
        // Loại luôn SKU đã có sẵn trong file (dòng có SKU) → mã sinh không đụng chúng.
        foreach (var r in rows)
        {
            var s = (r.Data.Sku ?? "").Trim();
            if (s.Length > 0) set.Add(s);
        }
        var codes = MakeUniqueSkus(set, blankIdx.Count);
        var result = new List<ProductXlsxCodec.ParsedRow>(rows);
        for (var j = 0; j < blankIdx.Count; j++)
        {
            var i = blankIdx[j];
            result[i] = result[i] with { Data = result[i].Data with { Sku = codes[j] } };
        }
        return (result, blankIdx.Count);
    }

    /// <summary>Cấp SKU MỚI cho các dòng theo khoá vị trí (1 transaction), gộp theo (acct, sheet): mỗi dòng nhận 1 mã
    /// mới CHƯA có trong shop, UPDATE sku + vá đuôi name_rewritten (<see cref="PatchSkuTail"/>) + updated_at/
    /// updated_by='sku-regen'. KHÔNG đụng rewritten_at. Trả số dòng đã đổi.</summary>
    public async Task<int> RegenerateSkusAsync(List<(string Acct, string Sheet, int RowNo)> keys, CancellationToken ct)
    {
        if (keys is null || keys.Count == 0) return 0;
        const string updSql = @"
UPDATE product_rows SET sku=$4, name_rewritten=$5, updated_at=now(), updated_by='sku-regen'
WHERE account_id=$1 AND sheet=$2 AND row_no=$3;";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var changed = 0;
        foreach (var g in keys.GroupBy(k => (k.Acct, k.Sheet)))
        {
            var rowNos = g.Select(k => k.RowNo).Distinct().ToArray();
            // Dòng cần cấp lại (đọc sku + name_rewritten cũ để vá đuôi).
            var current = new List<(int RowNo, string Sku, string Rewritten)>();
            await using (var q = new NpgsqlCommand(
                "SELECT row_no, sku, name_rewritten FROM product_rows " +
                "WHERE account_id=$1 AND sheet=$2 AND row_no = ANY($3) ORDER BY row_no;", conn, tx))
            {
                q.Parameters.AddWithValue(g.Key.Acct);
                q.Parameters.AddWithValue(g.Key.Sheet);
                q.Parameters.AddWithValue(rowNos);
                await using var rd = await q.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) current.Add((rd.GetInt32(0), S(rd, 1), S(rd, 2)));
            }
            if (current.Count == 0) continue;

            // Tập SKU shop (gồm cả SKU cũ của các dòng sắp đổi) → mã mới khác MỌI thứ → không đụng dòng khác lẫn nhau.
            var set = await ReadSkuSetAsync(conn, tx, g.Key.Acct, g.Key.Sheet, ct);
            var codes = MakeUniqueSkus(set, current.Count);
            for (var i = 0; i < current.Count; i++)
            {
                var (rowNo, oldSku, oldRewritten) = current[i];
                var newSku = codes[i];
                await using var up = new NpgsqlCommand(updSql, conn, tx);
                up.Parameters.AddWithValue(g.Key.Acct);
                up.Parameters.AddWithValue(g.Key.Sheet);
                up.Parameters.AddWithValue(rowNo);
                up.Parameters.AddWithValue(newSku);
                up.Parameters.AddWithValue(N(PatchSkuTail(oldRewritten, oldSku, newSku)));   // rỗng → NULL
                changed += await up.ExecuteNonQueryAsync(ct);
            }
        }
        await tx.CommitAsync(ct);
        return changed;
    }

    /// <summary>Pre-check trước khi ghi import: trong CÙNG lô dòng, SKU non-blank trùng nhau → ném lỗi rõ (liệt kê
    /// ≤5 SKU) để UI hiện được, thay vì unique_violation khó hiểu GIỮA transaction.</summary>
    internal static void CheckNoDupSkuWithin(IEnumerable<ProductXlsxCodec.ParsedRow> rows)
    {
        var dups = rows
            .Select(r => (r.Data.Sku ?? "").Trim())
            .Where(s => s.Length > 0)
            .GroupBy(s => s, StringComparer.Ordinal)
            .Where(gr => gr.Count() > 1)
            .Select(gr => gr.Key)
            .ToList();
        if (dups.Count > 0)
            throw new InvalidOperationException(
                $"File có SKU trùng trong cùng sheet: {string.Join(", ", dups.Take(5))}"
                + (dups.Count > 5 ? $" … (+{dups.Count - 5} SKU nữa)" : ""));
    }

    /// <summary>Đổi lỗi DB khó hiểu → câu tiếng Việt gọn cho UI. Vi phạm UNIQUE SKU per-shop (SqlState 23505) là lỗi
    /// hay gặp nhất khi sửa/nhập tay → thông báo rõ; còn lại giữ nguyên message.</summary>
    public static string Friendly(Exception ex) => ex switch
    {
        PostgresException { SqlState: "23505" } => "SKU trùng trong cùng shop — mỗi SKU chỉ được 1 dòng trong 1 shop.",
        _ => ex.Message,
    };
}
