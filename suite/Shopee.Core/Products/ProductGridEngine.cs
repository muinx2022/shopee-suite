using Shopee.Core.Coordination;

namespace Shopee.Core.Products;

/// <summary>
/// Lõi DÙNG CHUNG cho các màn "lưới dữ liệu sản phẩm" — gom TOÀN BỘ hành vi (lọc, phân trang, chọn nhiều,
/// mark-sold/reset-sold/regen-sku/delete, lưu 1 dòng + check SKU trùng, chuỗi status/confirm tiếng Việt) để hub web
/// (Blazor, <c>ProductDbDataOps</c>) và client desktop (Avalonia, <see cref="HubApiProductDataOps"/>) KHÔNG viết
/// trùng logic. Thuần Shopee.Core (KHÔNG import Avalonia/AspNetCore); UI tiêm <see cref="IProductDataOps"/> + hàm
/// confirm, nghe <see cref="Changed"/> để vẽ lại. Mọi thao tác tự quản <see cref="Busy"/> + nuốt lỗi vào
/// <see cref="Status"/> (KHÔNG ném ra ngoài); đang Busy thì bỏ qua.
///
/// FIXED-SCOPE (<see cref="SetScope"/>): lưới per-shop (tab Dữ liệu Fleet) ép Acct/Sheet của MỌI query về 1 shop —
/// filter chỉ còn <see cref="AllDataFilter.Text"/> tìm đa trường. Trang "📦 Dữ liệu" liên-shop không set scope.
/// </summary>
public sealed class ProductGridEngine
{
    /// <summary>Cỡ trang hợp lệ (UI dựng dropdown từ đây; <see cref="SetPageSizeAsync"/> chỉ nhận giá trị trong này).</summary>
    public static readonly int[] PageSizes = { 50, 100, 200, 500 };

    private static readonly AllDataFilter DefaultFilter = new(null, null, null, null, null, false, false, null);

    private readonly IProductDataOps _ops;
    private readonly Func<string, Task<bool>> _confirm;

    private readonly HashSet<ProductRowKey> _selected = new();
    private List<AllDataRow> _rows = new();
    private string? _fixedAcct, _fixedSheet;   // scope ép (null = không ép chiều đó)

    public ProductGridEngine(IProductDataOps ops, Func<string, Task<bool>> confirm)
    {
        _ops = ops;
        _confirm = confirm;
        // KHÔNG bắn Changed trong ctor (UI có thể chưa đăng ký / chưa dựng xong).
    }

    // ── State đọc được (UI bind get) ────────────────────────────────────────────────
    public AllDataFilter Applied { get; private set; } = DefaultFilter;
    public int Page { get; private set; } = 1;
    public int PageSize { get; private set; } = 100;
    public int Total { get; private set; }
    public int PageCount => Math.Max(1, (Total + PageSize - 1) / PageSize);
    public IReadOnlyList<AllDataRow> Rows => _rows;
    public IReadOnlyCollection<ProductRowKey> Selected => _selected;
    public int SelectedCount => _selected.Count;
    public bool Busy { get; private set; }
    public string Status { get; private set; } = "";
    public bool PgReady { get; private set; } = true;

    /// <summary>Bắn sau MỖI thay đổi state đáng vẽ lại (cuối/đầu mỗi thao tác + đổi selection). KHÔNG bắn trong ctor.</summary>
    public event Action? Changed;
    private void Raise() => Changed?.Invoke();

    // ── Fixed-scope ─────────────────────────────────────────────────────────────────

    /// <summary>Ép Acct/Sheet của MỌI query về 1 shop (null/blank = không ép chiều đó). Đổi scope → reset trang 1 +
    /// bỏ chọn; KHÔNG tự reload (caller gọi <see cref="ReloadAsync"/>/<see cref="ApplyFilterAsync"/>).</summary>
    public void SetScope(string? fixedAcct, string? fixedSheet)
    {
        var a = string.IsNullOrEmpty(fixedAcct) ? null : fixedAcct;
        var s = string.IsNullOrEmpty(fixedSheet) ? null : fixedSheet;
        if (a == _fixedAcct && s == _fixedSheet) return;
        _fixedAcct = a;
        _fixedSheet = s;
        Applied = ForceScope(Applied);
        Page = 1;
        _selected.Clear();
    }

    // Ép Acct/Sheet của filter về scope (scope set → BỎ QUA giá trị filter tương ứng).
    private AllDataFilter ForceScope(AllDataFilter f) => f with
    {
        Acct = _fixedAcct ?? f.Acct,
        Sheet = _fixedSheet ?? f.Sheet,
    };

    /// <summary>Bộ lọc đang áp có phủ (acct, sheet)? (null = mọi → phủ; ngược lại phải khớp) — caller /data dùng để
    /// quyết định reload sau khi thêm dòng.</summary>
    public bool FilterCovers(string acct, string sheet) =>
        (Applied.Acct is null || Applied.Acct == acct) && (Applied.Sheet is null || Applied.Sheet == sheet);

    // ── Lọc / phân trang / reload ────────────────────────────────────────────────────

    /// <summary>Áp bộ lọc mới (ép scope), về trang 1, bỏ chọn, xoá status rồi nạp.</summary>
    public async Task ApplyFilterAsync(AllDataFilter f)
    {
        if (Busy) return;
        Applied = ForceScope(f);
        Page = 1;
        _selected.Clear();
        Status = "";
        Busy = true; Raise();
        try { await LoadCoreAsync(CancellationToken.None); }
        finally { Busy = false; Raise(); }
    }

    /// <summary>Xoá sạch bộ lọc về mặc định (vẫn ép scope) rồi nạp từ trang 1.</summary>
    public Task ClearFilterAsync() => ApplyFilterAsync(DefaultFilter);

    /// <summary>Khôi phục TRỌN trạng thái xem (filter + trang + cỡ trang) trong 1 lượt nạp — cho deep-link/F5
    /// (/data đọc query URL, lưới per-shop nhận InitialState). Cỡ trang ngoài <see cref="PageSizes"/> → giữ mặc định.</summary>
    public async Task RestoreAsync(AllDataFilter f, int page, int pageSize)
    {
        if (Busy) return;
        Applied = ForceScope(f);
        if (PageSizes.Contains(pageSize)) PageSize = pageSize;
        Page = Math.Max(1, page);
        _selected.Clear();
        Status = "";
        Busy = true; Raise();
        try { await LoadCoreAsync(CancellationToken.None); }
        finally { Busy = false; Raise(); }
    }

    /// <summary>Nhảy tới trang <paramref name="p"/> (kẹp [1..PageCount]), bỏ chọn, nạp.</summary>
    public async Task GoPageAsync(int p)
    {
        if (Busy) return;
        Page = Math.Clamp(p, 1, PageCount);
        _selected.Clear();
        Busy = true; Raise();
        try { await LoadCoreAsync(CancellationToken.None); }
        finally { Busy = false; Raise(); }
    }

    /// <summary>Đổi cỡ trang (chỉ nhận giá trị trong <see cref="PageSizes"/>) → về trang 1, bỏ chọn, nạp.</summary>
    public async Task SetPageSizeAsync(int size)
    {
        if (Busy || !PageSizes.Contains(size)) return;
        PageSize = size;
        Page = 1;
        _selected.Clear();
        Busy = true; Raise();
        try { await LoadCoreAsync(CancellationToken.None); }
        finally { Busy = false; Raise(); }
    }

    /// <summary>Nạp lại trang hiện tại — GIỮ chọn (dùng sau khi lưu/đóng modal, hoặc refresh thủ công).</summary>
    public async Task ReloadAsync()
    {
        if (Busy) return;
        Busy = true; Raise();
        try { await LoadCoreAsync(CancellationToken.None); }
        finally { Busy = false; Raise(); }
    }

    // Nạp lõi: query 1 trang; nếu Page vượt PageCount thực tế (Total>0) → kẹp về trang cuối query lại 1 lần. KHÔNG tự
    // quản Busy (caller lo), KHÔNG bắn Changed (caller bắn ở finally). GIỮ tập chọn (selection tách khỏi Rows).
    private async Task LoadCoreAsync(CancellationToken ct)
    {
        try
        {
            // Caller "nhảy trang cuối" có thể đưa Page khổng lồ (int.MaxValue) → kẹp trước kẻo (Page-1)*PageSize
            // tràn int thành offset ÂM (Postgres ném "OFFSET must not be negative"); vòng kẹp-theo-Total phía dưới
            // sẽ đưa về trang cuối thật.
            if (Page > int.MaxValue / PageSize) Page = int.MaxValue / PageSize;
            var offset = (Page - 1) * PageSize;
            var page = await _ops.QueryAllAsync(Applied, offset, PageSize, ct);
            PgReady = true;
            var total = page.Total;
            var rows = page.Rows ?? new List<AllDataRow>();
            var pageCount = Math.Max(1, (total + PageSize - 1) / PageSize);
            if (Page > pageCount && total > 0)
            {
                Page = pageCount;
                offset = (Page - 1) * PageSize;
                page = await _ops.QueryAllAsync(Applied, offset, PageSize, ct);
                total = page.Total;
                rows = page.Rows ?? new List<AllDataRow>();
            }
            Total = total;
            _rows = rows;
        }
        catch (ProductStoreNotReadyException)
        {
            PgReady = false;
            Total = 0;
            _rows = new List<AllDataRow>();
        }
        catch (Exception ex)
        {
            Status = "✘ Lỗi đọc dữ liệu: " + FriendlyError(ex);
        }
    }

    // ── Chọn nhiều ──────────────────────────────────────────────────────────────────
    public bool IsSelected(ProductRowKey key) => _selected.Contains(key);

    public void ToggleSelect(ProductRowKey key)
    {
        if (!_selected.Remove(key)) _selected.Add(key);
        Raise();
    }

    public void SetSelected(ProductRowKey key, bool on)
    {
        var changed = on ? _selected.Add(key) : _selected.Remove(key);
        if (changed) Raise();
    }

    public void ClearSelection()
    {
        if (_selected.Count == 0) return;
        _selected.Clear();
        Raise();
    }

    // ── Thao tác trên tập chọn ───────────────────────────────────────────────────────

    /// <summary>+1 "đã bán" cho các dòng chọn (KHÔNG confirm, như /data). GIỮ chọn.</summary>
    public async Task MarkSoldAsync()
    {
        if (Busy || _selected.Count == 0) return;
        var keys = _selected.ToList();
        var n = keys.Count;
        Busy = true; Status = "⏳ Đang đánh dấu đã bán…"; Raise();
        try
        {
            await _ops.MarkSoldAsync(keys, CancellationToken.None);
            await LoadCoreAsync(CancellationToken.None);   // GIỮ chọn: dòng lên xanh + sold_count +1
            Status = $"✔ +1 đã bán cho {n} dòng.";
        }
        catch (ProductStoreNotReadyException) { PgReady = false; Status = ""; }
        catch (Exception ex) { Status = "✘ Lỗi đánh dấu đã bán: " + FriendlyError(ex); }
        finally { Busy = false; Raise(); }
    }

    /// <summary>Đặt "đã bán" về 0 cho các dòng chọn (confirm trước). GIỮ chọn.</summary>
    public async Task ResetSoldAsync()
    {
        if (Busy || _selected.Count == 0) return;
        var keys = _selected.ToList();
        var n = keys.Count;
        if (!await _confirm($"Đặt 'đã bán' về 0 cho {n} dòng đã chọn (xoá lịch sử bán của các dòng đó)?")) return;
        Busy = true; Status = "⏳ Đang đặt đã bán về 0…"; Raise();
        try
        {
            await _ops.ResetSoldAsync(keys, CancellationToken.None);
            await LoadCoreAsync(CancellationToken.None);   // GIỮ chọn: dòng hết xanh + sold_count = 0
            Status = $"✔ Đã đặt đã-bán = 0 cho {n} dòng.";
        }
        catch (ProductStoreNotReadyException) { PgReady = false; Status = ""; }
        catch (Exception ex) { Status = "✘ Lỗi đặt đã bán về 0: " + FriendlyError(ex); }
        finally { Busy = false; Raise(); }
    }

    /// <summary>Cấp SKU MỚI (B#####) cho các dòng chọn (confirm trước; đếm số dòng ĐANG CHỌN có tên-sửa sẽ bị vá đuôi).
    /// GIỮ chọn.</summary>
    public async Task RegenSkusAsync()
    {
        if (Busy || _selected.Count == 0) return;
        var keys = _selected.ToList();
        // Chỉ đếm trên Rows đang xem (như /data): dòng chọn ở trang khác không tính vào cảnh báo vá đuôi.
        var withName = _rows.Count(r => _selected.Contains(new ProductRowKey(r.AccountId, r.Sheet, r.RowNo))
            && !string.IsNullOrWhiteSpace(r.Data.NameRewritten));
        if (!await _confirm(
            $"Sinh SKU MỚI (B#####) cho {keys.Count} dòng đã chọn"
            + (withName > 0 ? $" — {withName} dòng có tên-sửa sẽ được vá đuôi tên theo SKU mới" : "")
            + ". Tiếp tục?"))
            return;
        Busy = true; Status = "⏳ Đang sinh SKU mới…"; Raise();
        try
        {
            var done = await _ops.RegenSkusAsync(keys, CancellationToken.None);
            await LoadCoreAsync(CancellationToken.None);   // GIỮ chọn: thấy SKU/tên mới
            Status = $"✔ Đã cấp SKU mới cho {done} dòng.";
        }
        catch (ProductStoreNotReadyException) { PgReady = false; Status = ""; }
        catch (Exception ex) { Status = "✘ Lỗi sinh SKU: " + FriendlyError(ex); }
        finally { Busy = false; Raise(); }
    }

    /// <summary>Xoá VĨNH VIỄN các dòng chọn (confirm trước). CLEAR chọn.</summary>
    public async Task DeleteSelectedAsync()
    {
        if (Busy || _selected.Count == 0) return;
        var keys = _selected.ToList();
        var n = keys.Count;
        if (!await _confirm($"Xoá VĨNH VIỄN {n} dòng đã chọn (kèm lịch sử đã bán của các dòng đó)? Không thể hoàn tác."))
            return;
        Busy = true; Status = "⏳ Đang xoá…"; Raise();
        try
        {
            var del = await _ops.DeleteRowsAsync(keys, CancellationToken.None);
            _selected.Clear();   // CLEAR chọn (khác mark/reset/regen)
            await LoadCoreAsync(CancellationToken.None);
            Status = $"✔ Đã xoá {del} dòng.";
        }
        catch (ProductStoreNotReadyException) { PgReady = false; Status = ""; }
        catch (Exception ex) { Status = "✘ Lỗi xoá: " + FriendlyError(ex); }
        finally { Busy = false; Raise(); }
    }

    // ── Lưu 1 dòng (thêm/sửa) ─────────────────────────────────────────────────────────

    /// <summary>Lưu 1 dòng — gói validate SKU-trùng (sku non-blank + (thêm ‖ sửa-mà-đổi) → check tồn tại → confirm),
    /// rồi Update (Ok=false = không tìm thấy dòng) hoặc Insert (Sku trống → hiện thực tự sinh B#####). KHÔNG tự reload
    /// (caller quyết — /data có luật <see cref="FilterCovers"/>). Trả (Ok, RowNo cuối, Sku cuối); Ok=false khi huỷ
    /// confirm / lỗi / không tìm thấy dòng.</summary>
    public async Task<(bool Ok, int RowNo, string Sku)> SaveRowAsync(
        bool isEdit, string acct, string sheet, int rowNo, string origSku, ProductRowData data)
    {
        if (Busy) return (false, 0, "");
        var sku = (data.Sku ?? "").Trim();
        try
        {
            // Cảnh báo SKU trùng: thêm → mọi SKU tay; sửa → chỉ khi SKU ĐỔI (loại chính dòng đang sửa).
            if (sku.Length > 0)
            {
                var skuChanged = !isEdit || !string.Equals(sku, (origSku ?? "").Trim(), StringComparison.Ordinal);
                if (skuChanged)
                {
                    var dup = await _ops.SkuExistsAsync(acct, sheet, sku, isEdit ? rowNo : -1, CancellationToken.None);
                    if (dup && !await _confirm(
                        $"SKU '{sku}' đã tồn tại trong shop này — vẫn lưu? (kho sẽ CHẶN nếu trùng thật)."))
                        return (false, 0, "");
                }
            }
            Busy = true; Raise();
            if (isEdit)
            {
                var ok = await _ops.UpdateRowAsync(acct, sheet, rowNo, data, CancellationToken.None);
                Status = ok ? $"✔ Đã lưu dòng {rowNo}." : $"✘ Không tìm thấy dòng {rowNo} (đã bị xoá?).";
                return (ok, rowNo, sku);
            }
            else
            {
                var (newRow, newSku) = await _ops.InsertRowAsync(acct, sheet, data, CancellationToken.None);
                Status = $"✔ Đã thêm dòng {newRow} (SKU {newSku}).";
                return (true, newRow, newSku);
            }
        }
        catch (ProductStoreNotReadyException) { PgReady = false; return (false, 0, ""); }
        catch (Exception ex) { Status = "✘ Lỗi lưu: " + FriendlyError(ex); return (false, 0, ""); }
        finally { Busy = false; Raise(); }
    }

    /// <summary>Rút gọn message lỗi cho dòng Status (chống tràn UI) — mang từ DataViewModel để hub + client hiện y hệt.</summary>
    public static string FriendlyError(Exception ex)
    {
        var msg = ex.Message?.Trim() ?? "";
        if (msg.Length == 0) msg = ex.GetType().Name;
        return msg.Length > 140 ? msg[..140] + "…" : msg;
    }
}
