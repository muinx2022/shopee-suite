using ClosedXML.Excel;
using Shopee.Hub;

namespace Shopee.Hub.Web.Services;

/// <summary>
/// Đọc CẤU TRÚC dòng của các sheet trong workbook (đã đồng bộ lên Hub) để vẽ "bản đồ dòng" ở trang Thống kê:
/// CHỈ lấy danh sách SỐ DÒNG TUYỆT ĐỐI của các dòng DỮ LIỆU có thật (bỏ header, bỏ dòng trống) — KHÔNG đọc
/// nội dung ô → nhẹ. Danh sách này cũng là "bảng dịch" giữa 2 kiểu đánh số trong ledger:
///  · scrape lưu theo CHỈ SỐ DỒN (vị trí trong danh sách, 1-based) → phần tử thứ p.
///  · import/update lưu theo SỐ DÒNG TUYỆT ĐỐI → tra trong danh sách.
/// Workbook trên Hub thường ĐA SHEET (mỗi shop 1 sheet) → parse 1 LẦN, cache TẤT CẢ sheet của file đó theo
/// (tên file + version manifest) → mở shop khác cùng tài khoản là tức thì; file đổi (version tăng) thì đọc lại.
///
/// Acc đã chuyển sang KHO HUB (Postgres, <c>DataSource=="hub"</c>): không có workbook để parse → dựng bản đồ từ
/// bảng <c>product_rows</c> (danh sách <c>row_no</c> theo thứ tự = ánh xạ dense p ↔ row_no[p-1], đúng ngữ nghĩa
/// dense đã chốt). Không có version-file nên cache TTL ngắn theo (acctId, sheet). Postgres chưa cấu hình/chưa
/// sẵn sàng → bản đồ RỖNG (thống kê hiện "chưa có dữ liệu"), KHÔNG ném.
/// </summary>
public sealed class SheetMapService
{
    private readonly HubDatabase _db;
    private readonly FileStoreConfigService _config;
    private readonly IServiceProvider _sp;   // ProductDb có thể KHÔNG đăng ký DI (chưa cấu hình Postgres) → resolve tuỳ lúc
    private readonly object _lock = new();
    // key = "{fileName}|{sheetName}" (OrdinalIgnoreCase) → (version đã cache, danh sách dòng tuyệt đối).
    private readonly Dictionary<string, (int version, List<int> rows)> _cache = new(StringComparer.OrdinalIgnoreCase);
    // Nhánh kho Hub KHÔNG có version-file → cache TTL ngắn theo "{acctId}|{sheet}".
    private readonly Dictionary<string, (DateTimeOffset at, List<int> rows)> _dbCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan DbCacheTtl = TimeSpan.FromSeconds(30);

    public SheetMapService(HubDatabase db, FileStoreConfigService config, IServiceProvider sp)
    {
        _db = db; _config = config; _sp = sp;
    }

    public sealed record SheetInfo(bool Found, string? Error, List<int> DataRows);

    /// <summary>Trả danh sách SỐ DÒNG TUYỆT ĐỐI các dòng dữ liệu của (tài khoản BigSeller + sheet). Acc excel-mode:
    /// đọc file workbook trên Hub (prefix <c>workbooks/{accountId}/</c>) + parse ở luồng nền, cache theo version.
    /// Acc hub-mode: dựng từ <c>product_rows</c> (cache TTL ngắn).</summary>
    public async Task<SheetInfo> GetAsync(string accountId, string sheet)
    {
        if (string.IsNullOrEmpty(accountId))
            return new(false, "Thiếu tài khoản.", []);

        // Acc dùng kho Hub → bản đồ dòng dựng từ Postgres, KHÔNG đọc workbook Excel.
        if (IsHubMode(accountId))
            return await GetFromDbAsync(accountId, sheet).ConfigureAwait(false);

        var entry = _db.ListFiles()
            .FirstOrDefault(f => f.Name.StartsWith($"workbooks/{accountId}/", StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return new(false, "Chưa có workbook trên Hub cho tài khoản này (client chưa đồng bộ lên).", []);

        var wantKey = entry.Name + "|" + (sheet ?? "");
        lock (_lock)
            if (_cache.TryGetValue(wantKey, out var c) && c.version == entry.Version)
                return new(true, null, c.rows);

        var (sheets, first, error) = await Task.Run(() => ReadAllSheets(entry.Name)).ConfigureAwait(false);
        if (error is not null) return new(false, error, []);

        lock (_lock)
            foreach (var kv in sheets)
                _cache[entry.Name + "|" + kv.Key] = (entry.Version, kv.Value);

        // Sheet trống → dùng sheet đầu; có tên → PHẢI khớp (không âm thầm rơi về sheet khác → tô nhầm dòng).
        var targetName = string.IsNullOrWhiteSpace(sheet) ? first : sheet;
        if (targetName is null) return new(false, "Workbook không có sheet nào.", []);
        foreach (var kv in sheets)
            if (string.Equals(kv.Key, targetName, StringComparison.OrdinalIgnoreCase))
                return new(true, null, kv.Value);
        return new(false, $"Không tìm thấy sheet \"{sheet}\" trong workbook trên Hub.", []);
    }

    /// <summary>Acc này đã chuyển sang kho Hub (Postgres) chưa? Đọc <c>DataSource</c> từ config/bigseller.json.
    /// Lỗi đọc config → coi như excel-mode (giữ nhánh workbook).</summary>
    private bool IsHubMode(string accountId)
    {
        try { return _config.BigSellerAccounts().Any(a => a.Id == accountId && a.UsesHubData); }
        catch { return false; }
    }

    /// <summary>Bản đồ dòng dựng từ <c>product_rows</c> (danh sách row_no tăng dần = ánh xạ dense p ↔ row_no[p-1]).
    /// ProductDb null/chưa sẵn sàng hoặc lỗi truy vấn → bản đồ RỖNG (Found=true, DataRows=[] → thống kê hiện
    /// "chưa có dữ liệu"), KHÔNG ném. Cache TTL 30s theo (acctId, sheet).</summary>
    private async Task<SheetInfo> GetFromDbAsync(string accountId, string sheet)
    {
        var key = accountId + "|" + (sheet ?? "");
        lock (_lock)
            if (_dbCache.TryGetValue(key, out var c) && (DateTimeOffset.UtcNow - c.at) < DbCacheTtl)
                return new(true, null, c.rows);

        var pdb = _sp.GetService<ProductDb>();
        if (pdb is null || !pdb.IsReady)
            return new(true, null, []);   // Postgres chưa cấu hình/chưa migrate → rỗng, không cache (đợi lên rồi thử lại)

        List<int> rows;
        try { rows = await pdb.GetRowNosAsync(accountId, sheet ?? "", CancellationToken.None).ConfigureAwait(false); }
        catch { return new(true, null, []); }   // lỗi DB → rỗng, không làm vỡ trang thống kê

        lock (_lock) _dbCache[key] = (DateTimeOffset.UtcNow, rows);
        return new(true, null, rows);
    }

    private (Dictionary<string, List<int>> sheets, string? first, string? error) ReadAllSheets(string name)
    {
        try
        {
            var bytes = _db.ReadFile(name);
            if (bytes is null || bytes.Length == 0) return (new(), null, "Không đọc được file workbook trên Hub.");
            using var ms = new MemoryStream(bytes);
            using var wb = new XLWorkbook(ms);
            var map = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            string? first = null;
            foreach (var ws in wb.Worksheets)
            {
                first ??= ws.Name;
                // Dòng 1 = header; lấy các dòng dữ liệu CÓ THẬT (bỏ trống), theo số dòng tuyệt đối tăng dần.
                map[ws.Name] = ws.RowsUsed().Where(r => r.RowNumber() > 1).Select(r => r.RowNumber()).OrderBy(x => x).ToList();
            }
            if (first is null) return (map, null, "Workbook không có sheet nào.");
            return (map, first, null);
        }
        catch (Exception ex) { return (new(), null, "Lỗi đọc workbook: " + ex.Message); }
    }
}
