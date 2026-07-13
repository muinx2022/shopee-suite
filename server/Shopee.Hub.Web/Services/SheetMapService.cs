using Shopee.Hub;

namespace Shopee.Hub.Web.Services;

/// <summary>
/// Dựng "bản đồ dòng" của một (tài khoản BigSeller + sheet) để vẽ ở trang Thống kê — nay dựng TỪ kho sản phẩm
/// Postgres (<c>product_rows</c>), KHÔNG còn parse workbook Excel (mọi acc đã chuyển sang <c>DataSource=="hub"</c>).
/// Lấy danh sách <c>row_no</c> theo thứ tự tăng dần = ánh xạ dense p ↔ row_no[p-1]. Danh sách này cũng là
/// "bảng dịch" giữa 2 kiểu đánh số trong ledger:
///  · scrape lưu theo CHỈ SỐ DỒN (vị trí trong danh sách, 1-based) → phần tử thứ p.
///  · import/update lưu theo SỐ DÒNG TUYỆT ĐỐI → tra trong danh sách.
/// Không có version-file nên cache TTL ngắn theo (acctId, sheet). Postgres chưa cấu hình/chưa sẵn sàng → bản đồ
/// RỖNG (thống kê hiện "chưa có dữ liệu"), KHÔNG ném.
/// </summary>
public sealed class SheetMapService
{
    private readonly IServiceProvider _sp;   // ProductDb có thể KHÔNG đăng ký DI (chưa cấu hình Postgres) → resolve tuỳ lúc
    private readonly object _lock = new();
    // Cache TTL ngắn theo "{acctId}|{sheet}".
    private readonly Dictionary<string, (DateTimeOffset at, List<int> rows)> _dbCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan DbCacheTtl = TimeSpan.FromSeconds(30);

    public SheetMapService(IServiceProvider sp)
    {
        _sp = sp;
    }

    public sealed record SheetInfo(bool Found, string? Error, List<int> DataRows);

    /// <summary>Trả danh sách dòng dữ liệu của (tài khoản BigSeller + sheet), dựng từ <c>product_rows</c>
    /// (Postgres, cache TTL ngắn).</summary>
    public async Task<SheetInfo> GetAsync(string accountId, string sheet)
    {
        if (string.IsNullOrEmpty(accountId))
            return new(false, "Thiếu tài khoản.", []);

        return await GetFromDbAsync(accountId, sheet).ConfigureAwait(false);
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
}
