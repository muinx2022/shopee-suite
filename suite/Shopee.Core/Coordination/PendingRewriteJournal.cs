using Shopee.Core.Infrastructure;

namespace Shopee.Core.Coordination;

/// <summary>
/// Kho tạm BỀN-QUA-RESTART cho kết quả rewrite tên (AI) CHƯA đẩy được lên Hub — chống MẤT TIỀN AI khi mất
/// mạng/Hub 503 giữa lúc rewrite: ghi WRITE-AHEAD ra file NGAY TRƯỚC khi gọi Hub (Append), rồi flush lại khi
/// có kết nối (TryFlushAsync, gọi lúc khởi động). Server (ProductsRewritten) idempotent → flush trùng vô hại.
/// 1 file <c>.jsonl</c> / tài khoản dưới %AppData%\ShopeeSuite\pending-rewrites, mỗi dòng 1 kết quả rewrite.
/// Thread-safe trong process bằng <c>lock</c> static (nhiều lane rewrite có thể Append song song).
/// </summary>
public static class PendingRewriteJournal
{
    private static readonly object _lock = new();
    private static int _flushing;   // 0/1 chống 2 lượt flush chồng lấn (line-index rewrite phải độc quyền)
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private const int BatchMax = 500;

    /// <summary>Thư mục chứa các file journal (tự tạo). Tên file = <c>{acctId}.jsonl</c>.</summary>
    private static string Dir => SuitePaths.ModuleDir("pending-rewrites");
    private static string FileFor(string acctId) => Path.Combine(Dir, acctId + ".jsonl");

    /// <summary>1 dòng journal: kết quả rewrite của 1 (sheet, rowNo). ts = mốc ghi (unix ms) — chỉ để chẩn đoán,
    /// KHÔNG gửi lên Hub (request chỉ cần rowNo + nameRewritten).</summary>
    private sealed record Entry(string Sheet, int RowNo, string NameRewritten, long Ts);

    /// <summary>Ghi WRITE-AHEAD N kết quả rewrite cho 1 (acc, sheet) — GỌI TRƯỚC khi POST lên Hub. No-op nếu
    /// acctId rỗng hoặc items rỗng. Nhiều lane có thể gọi song song (nối cuối file trong lock).</summary>
    public static void Append(string acctId, string sheet, IEnumerable<ProductRewrittenItem> items)
    {
        if (string.IsNullOrEmpty(acctId)) return;
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sb = new StringBuilder();
        foreach (var it in items)
            sb.Append(JsonSerializer.Serialize(new Entry(sheet ?? "", it.RowNo, it.NameRewritten ?? "", ts), JsonOpts)).Append('\n');
        if (sb.Length == 0) return;
        lock (_lock)
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllText(FileFor(acctId), sb.ToString(), Encoding.UTF8);
        }
    }

    /// <summary>Đọc MỌI file journal, gom theo (acct = tên file, sheet), POST ProductsRewritten từng batch ≤500.
    /// Batch OK → xoá các dòng đó khỏi file (ghi lại phần còn; rỗng → xoá file). Batch hỏng (mất mạng / 503) →
    /// DỪNG file đó, giữ nguyên phần chưa flush cho lượt sau. Trả TỔNG số item đã flush thành công. Best-effort:
    /// file/dòng hỏng bỏ qua; chỉ OperationCanceledException được ném. Chống chồng lấn: lượt flush thứ 2 trả 0.</summary>
    public static async Task<int> TryFlushAsync(HubClient client, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _flushing, 1) == 1) return 0;   // đang flush → bỏ lượt này
        try
        {
            string[] files;
            lock (_lock)
            {
                if (!Directory.Exists(Dir)) return 0;
                files = Directory.GetFiles(Dir, "*.jsonl");
            }

            var flushed = 0;
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var acctId = Path.GetFileNameWithoutExtension(file);

                // Đọc + parse (giữ SỐ DÒNG gốc để loại đúng dòng đã flush). Append chỉ nối CUỐI file → chỉ số
                // các dòng đã đọc ổn định qua các Append chen giữa; dòng mới có chỉ số lớn hơn nên tự sống sót.
                List<(int line, Entry e)> entries;
                lock (_lock) { entries = ReadEntries(file); }
                if (entries.Count == 0) continue;

                var doneLines = new HashSet<int>();
                foreach (var g in entries.GroupBy(x => x.e.Sheet, StringComparer.Ordinal))
                {
                    foreach (var batch in g.Chunk(BatchMax))
                    {
                        ct.ThrowIfCancellationRequested();
                        var req = new ProductRewrittenRequest(acctId, g.Key,
                            batch.Select(x => new ProductRewrittenItem(x.e.RowNo, x.e.NameRewritten)).ToList());
                        try { await client.PostProductRewrittenAsync(req, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { throw; }
                        catch { RewriteExcluding(file, doneLines); return flushed; }   // hỏng → chốt phần đã flush, dừng
                        foreach (var x in batch) doneLines.Add(x.line);
                        flushed += batch.Length;
                    }
                }
                RewriteExcluding(file, doneLines);
            }
            return flushed;
        }
        finally { Interlocked.Exchange(ref _flushing, 0); }
    }

    /// <summary>Đọc file → danh sách (chỉ số dòng, entry) đã parse; dòng rỗng/hỏng bỏ qua (KHÔNG tính vào tập
    /// sẽ flush, và cũng KHÔNG bị RewriteExcluding xoá → giữ nguyên). Không tồn tại → rỗng.</summary>
    private static List<(int line, Entry e)> ReadEntries(string file)
    {
        var result = new List<(int, Entry)>();
        if (!File.Exists(file)) return result;
        string[] lines;
        try { lines = File.ReadAllLines(file, Encoding.UTF8); }
        catch { return result; }
        for (var i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            Entry? e = null;
            try { e = JsonSerializer.Deserialize<Entry>(lines[i], JsonOpts); }
            catch { }
            if (e is not null) result.Add((i, e));
        }
        return result;
    }

    /// <summary>Ghi lại file BỎ các dòng có chỉ số trong <paramref name="doneLines"/> (đã flush); giữ mọi dòng
    /// khác (kể cả dòng mới Append chen vào lúc đang POST + dòng hỏng). Rỗng → xoá file. Trong lock (độc quyền
    /// với Append). doneLines rỗng → no-op (không đụng file, khỏi ghi vô ích).</summary>
    private static void RewriteExcluding(string file, HashSet<int> doneLines)
    {
        if (doneLines.Count == 0) return;
        lock (_lock)
        {
            string[] lines;
            try { lines = File.Exists(file) ? File.ReadAllLines(file, Encoding.UTF8) : []; }
            catch { return; }
            var sb = new StringBuilder();
            for (var i = 0; i < lines.Length; i++)
            {
                if (doneLines.Contains(i) || string.IsNullOrWhiteSpace(lines[i])) continue;
                sb.Append(lines[i]).Append('\n');
            }
            try
            {
                if (sb.Length == 0) File.Delete(file);
                else File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }
}
