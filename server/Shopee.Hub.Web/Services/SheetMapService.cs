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
/// </summary>
public sealed class SheetMapService
{
    private readonly HubDatabase _db;
    private readonly object _lock = new();
    // key = "{fileName}|{sheetName}" (OrdinalIgnoreCase) → (version đã cache, danh sách dòng tuyệt đối).
    private readonly Dictionary<string, (int version, List<int> rows)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public SheetMapService(HubDatabase db) => _db = db;

    public sealed record SheetInfo(bool Found, string? Error, List<int> DataRows);

    /// <summary>Trả danh sách SỐ DÒNG TUYỆT ĐỐI các dòng dữ liệu của (tài khoản BigSeller + sheet). Đọc file
    /// workbook trên Hub (prefix <c>workbooks/{accountId}/</c>) + parse ở luồng nền, có cache theo version.</summary>
    public async Task<SheetInfo> GetAsync(string accountId, string sheet)
    {
        if (string.IsNullOrEmpty(accountId))
            return new(false, "Thiếu tài khoản.", []);

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
