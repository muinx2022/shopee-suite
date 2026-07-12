using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shopee.Core.BigSeller;
using Shopee.Core.Coordination;
using Shopee.Suite.Services;

namespace Shopee.Suite.Modules.Data;

/// <summary>1 lựa chọn tài khoản cho combo lọc (Id null = tuỳ chọn "mọi"/"chọn").</summary>
public sealed record AccountOption(string? Id, string Label);
/// <summary>1 lựa chọn shop cho combo lọc (Sheet null = "mọi"/"chọn"; giá trị lọc = ShopeeDataSheet).</summary>
public sealed record ShopOption(string? Sheet, string Label);

/// <summary>
/// Màn "Dữ liệu sản phẩm" (client): tái hiện trang /data trên hub nhưng thao tác QUA HTTP (HubClient). Lọc theo
/// acc/shop/sku/giá + đã-bán + SKU-trùng, phân trang, chọn nhiều dòng để đánh dấu bán / cấp SKU mới / xoá, và
/// thêm/sửa 1 dòng qua cửa sổ modal. KHÔNG kế thừa ModuleViewModelBase (màn CRUD không cần log file). Nguồn
/// acc/shop = <see cref="BigSellerStore.Shared"/> (chỉ shop có ShopeeDataSheet); bộ lọc CHỈ áp khi bấm Lọc.
/// </summary>
public sealed partial class DataViewModel : ObservableObject
{
    private const string AllAccountsLabel = "— mọi tài khoản —";
    private const string AllShopsLabel = "— mọi shop —";

    // Snapshot cấu hình (để resolve nhãn acc/shop cho lưới + dựng option combo). Làm mới khi kho đổi.
    private IReadOnlyList<BigSellerAccount> _accounts = Array.Empty<BigSellerAccount>();
    private Dictionary<string, string> _acctLabel = new();
    private Dictionary<(string, string), string> _shopName = new();

    // Bộ lọc ĐANG ÁP (chỉ đổi khi bấm Lọc) — đổi trang/refresh dùng bộ này, không dùng ô đang gõ.
    private AllDataFilter _applied = new(null, null, null, null, null, false, false);

    // Tập khoá dòng đang chọn (bền qua reload cùng trang; xoá khi lọc/đổi trang) — như HashSet của hub.
    private readonly HashSet<ProductRowKey> _selectedKeys = new();
    private bool _applyingRows;   // đang dựng lại Rows → callback tick không mutate tập chọn
    private bool _loaded;         // EnsureLoaded chạy 1 lần

    // ── Option lọc ──
    public ObservableCollection<AccountOption> AccountOptions { get; } = new();
    public ObservableCollection<ShopOption> ShopOptions { get; } = new();
    [ObservableProperty] private AccountOption? _selectedAccountOption;
    [ObservableProperty] private ShopOption? _selectedShopOption;

    // ── Ô lọc (chỉ áp khi bấm Lọc) ──
    [ObservableProperty] private string _skuFilter = "";
    [ObservableProperty] private string _priceMinText = "";
    [ObservableProperty] private string _priceMaxText = "";
    [ObservableProperty] private bool _soldOnly;
    [ObservableProperty] private bool _dupSkuOnly;

    // ── Phân trang ──
    public int[] PageSizes { get; } = { 50, 100, 200, 500 };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageCount))]
    [NotifyPropertyChangedFor(nameof(PageText))]
    [NotifyPropertyChangedFor(nameof(CanFirstPrev))]
    [NotifyPropertyChangedFor(nameof(CanNextLast))]
    private int _pageSize = 100;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    [NotifyPropertyChangedFor(nameof(CanFirstPrev))]
    [NotifyPropertyChangedFor(nameof(CanNextLast))]
    private int _page = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageCount))]
    [NotifyPropertyChangedFor(nameof(PageText))]
    [NotifyPropertyChangedFor(nameof(CanFirstPrev))]
    [NotifyPropertyChangedFor(nameof(CanNextLast))]
    private int _total;

    public int PageCount => Math.Max(1, (Total + PageSize - 1) / PageSize);
    public string PageText => $"trang {Page} / {PageCount} · {Total:N0} dòng";
    public bool CanFirstPrev => !IsBusy && Page > 1;
    public bool CanNextLast => !IsBusy && Page < PageCount;

    // ── Dòng + tập chọn ──
    public ObservableCollection<DataRowItem> Rows { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private int _selectedCount;

    public bool HasSelection => SelectedCount > 0;

    // ── Trạng thái chung ──
    [ObservableProperty] private string _status = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInteract))]
    [NotifyPropertyChangedFor(nameof(CanFirstPrev))]
    [NotifyPropertyChangedFor(nameof(CanNextLast))]
    private bool _isBusy;

    public bool CanInteract => !IsBusy;

    // Kho suy giảm: chưa cấu hình Hub (client null) hoặc Postgres chưa sẵn sàng (HTTP 503) → hiện hint, không crash.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DegradedMessage))]
    [NotifyPropertyChangedFor(nameof(IsDegraded))]
    private bool _pgReady = true;

    public bool HubConfigured => CoordinationRuntime.Client is not null;
    public string DegradedMessage =>
        !HubConfigured ? "Chưa kết nối Hub — bật đồng bộ Hub trong Cài đặt."
        : !PgReady ? "Kho Hub (Postgres) chưa sẵn sàng."
        : "";
    public bool IsDegraded => DegradedMessage.Length > 0;

    public DataViewModel()
    {
        // KHÔNG I/O trong ctor — option acc/shop + query trang 1 nạp ở EnsureLoaded (View gọi lúc Loaded).
    }

    /// <summary>Lần đầu View hiển thị: nạp option acc/shop + query trang 1, đăng ký làm mới khi kho đổi.</summary>
    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        RefreshAccountsSnapshot();
        BigSellerStore.Shared.Changed += OnStoreChanged;
        _ = Reload();
    }

    // Kho tài khoản đổi (đồng bộ Hub…) → làm mới snapshot + option trên UI thread (KHÔNG đụng bộ lọc đang áp).
    private void OnStoreChanged() => UiThread.Post(RefreshAccountsSnapshot);

    // Dựng lại snapshot nhãn + option combo, GIỮ lựa chọn acc/shop đang chọn nếu còn.
    private void RefreshAccountsSnapshot()
    {
        _accounts = BigSellerStore.Shared.Accounts;
        _acctLabel = _accounts.ToDictionary(a => a.Id, a => a.DisplayName);
        _shopName = new();
        foreach (var a in _accounts)
            foreach (var s in a.Shops)
                if (!string.IsNullOrWhiteSpace(s.ShopeeDataSheet))
                    _shopName[(a.Id, s.ShopeeDataSheet)] = s.DisplayName;

        var curAcctId = SelectedAccountOption?.Id;
        var curSheet = SelectedShopOption?.Sheet;

        AccountOptions.Clear();
        AccountOptions.Add(new AccountOption(null, AllAccountsLabel));
        foreach (var a in _accounts)
            AccountOptions.Add(new AccountOption(a.Id, a.DisplayName));

        // Đặt lại lựa chọn acc rồi LUÔN dựng lại option shop: record so sánh bằng GIÁ TRỊ nên setter có thể
        // no-op (acc cũ giữ nguyên nhãn) → OnSelectedAccountOptionChanged không chạy dù shop của acc đã đổi.
        SelectedAccountOption = AccountOptions.FirstOrDefault(o => o.Id == curAcctId) ?? AccountOptions[0];
        RebuildShopOptions(SelectedAccountOption?.Id);
        SelectedShopOption = ShopOptions.FirstOrDefault(o => o.Sheet == curSheet) ?? ShopOptions[0];
    }

    // Đổi tài khoản (combo) → dựng lại option shop + reset shop về "mọi" (như hub OnAcctChange). KHÔNG query.
    partial void OnSelectedAccountOptionChanged(AccountOption? value)
    {
        RebuildShopOptions(value?.Id);
        SelectedShopOption = ShopOptions[0];
    }

    // Option shop của 1 acc (sentinel "mọi" + shop có ShopeeDataSheet).
    private void RebuildShopOptions(string? acctId)
    {
        ShopOptions.Clear();
        ShopOptions.Add(new ShopOption(null, AllShopsLabel));
        var acct = _accounts.FirstOrDefault(a => a.Id == acctId);
        if (acct is not null)
            foreach (var s in acct.Shops)
                if (!string.IsNullOrWhiteSpace(s.ShopeeDataSheet))
                    ShopOptions.Add(new ShopOption(s.ShopeeDataSheet, s.DisplayName));
    }

    // Đổi cỡ trang → về trang 1, bỏ chọn, nạp lại (bỏ qua lúc chưa EnsureLoaded).
    partial void OnPageSizeChanged(int value)
    {
        if (!_loaded) return;
        Page = 1;
        ClearSelectionInternal();
        _ = Reload();
    }

    private AllDataFilter BuildFilter() => new(
        Acct: string.IsNullOrEmpty(SelectedAccountOption?.Id) ? null : SelectedAccountOption!.Id,
        Sheet: string.IsNullOrEmpty(SelectedShopOption?.Sheet) ? null : SelectedShopOption!.Sheet,
        Sku: string.IsNullOrWhiteSpace(SkuFilter) ? null : SkuFilter.Trim(),
        PriceMin: ParsePrice(PriceMinText),
        PriceMax: ParsePrice(PriceMaxText),
        SoldOnly: SoldOnly,
        DupSkuOnly: DupSkuOnly);

    private static long? ParsePrice(string? s) => long.TryParse((s ?? "").Trim(), out var v) && v > 0 ? v : null;

    private string AcctLabel(string id) => _acctLabel.TryGetValue(id, out var n) ? n : ShortId(id);
    // Ngăn không khớp shop nào (shop đã xoá / sheet lạ) → hiện khoá ngăn có 🔒 (như hub).
    private string ShopLabel(string acct, string sheet) => _shopName.TryGetValue((acct, sheet), out var n) ? n : "🔒 " + sheet;
    private static string ShortId(string id) => string.IsNullOrEmpty(id) ? "?" : (id.Length <= 8 ? id : id[..8] + "…");

    // ══ Nạp lõi: query 1 trang qua HubClient + resolve nhãn + kẹp trang nếu vượt. Không tự quản IsBusy
    //    (caller lo). Không đặt Status khi thành công (giữ thông báo thao tác của command). ══
    private async Task LoadCoreAsync()
    {
        var client = CoordinationRuntime.Client;
        if (client is null)
        {
            _applyingRows = true; Rows.Clear(); _applyingRows = false;
            Total = 0;
            _selectedKeys.Clear();
            SelectedCount = 0;
            OnPropertyChanged(nameof(DegradedMessage));
            OnPropertyChanged(nameof(IsDegraded));
            OnPropertyChanged(nameof(HubConfigured));
            return;
        }
        try
        {
            var req = new AllDataQueryRequest(_applied, (Page - 1) * PageSize, PageSize);
            var page = await client.QueryProductAllDataAsync(req);
            PgReady = true;
            var total = page?.Total ?? 0;
            var rows = page?.Rows ?? new List<AllDataRow>();
            // Trang vượt sau khi biết Total → kẹp về trang cuối rồi query lại 1 lần.
            var pageCount = Math.Max(1, (total + PageSize - 1) / PageSize);
            if (Page > pageCount && total > 0)
            {
                Page = pageCount;
                req = new AllDataQueryRequest(_applied, (Page - 1) * PageSize, PageSize);
                page = await client.QueryProductAllDataAsync(req);
                total = page?.Total ?? 0;
                rows = page?.Rows ?? new List<AllDataRow>();
            }
            ApplyRows(total, rows);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            PgReady = false;
            _applyingRows = true; Rows.Clear(); _applyingRows = false;
            Total = 0;
        }
        catch (TaskCanceledException)
        {
            Status = "✘ Lỗi kết nối Hub: hết thời gian chờ.";
        }
        catch (Exception ex)
        {
            Status = "✘ Lỗi kết nối Hub: " + FriendlyError(ex);
        }
    }

    private void ApplyRows(int total, List<AllDataRow> rows)
    {
        Total = total;
        _applyingRows = true;
        Rows.Clear();
        foreach (var r in rows)
        {
            var item = new DataRowItem(r, AcctLabel(r.AccountId), ShopLabel(r.AccountId, r.Sheet), OnRowSelectionToggled);
            if (_selectedKeys.Contains(item.Key)) item.IsSelected = true;
            Rows.Add(item);
        }
        _applyingRows = false;
        SelectedCount = _selectedKeys.Count;
    }

    // Tick 1 dòng → cập nhật tập chọn (bỏ qua khi đang dựng lại Rows).
    private void OnRowSelectionToggled(DataRowItem item)
    {
        if (_applyingRows) return;
        if (item.IsSelected) _selectedKeys.Add(item.Key);
        else _selectedKeys.Remove(item.Key);
        SelectedCount = _selectedKeys.Count;
    }

    private void ClearSelectionInternal()
    {
        _selectedKeys.Clear();
        _applyingRows = true;
        foreach (var r in Rows) r.IsSelected = false;
        _applyingRows = false;
        SelectedCount = 0;
    }

    // Reload trang hiện tại (giữ _applied + tập chọn) — dùng cho EnsureLoaded/đổi cỡ trang/sau thao tác.
    private async Task Reload()
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await LoadCoreAsync(); }
        finally { IsBusy = false; }
    }

    // ══ Commands ══
    [RelayCommand]
    private async Task Filter()
    {
        if (IsBusy) return;
        _applied = BuildFilter();
        Page = 1;
        ClearSelectionInternal();
        Status = "";
        IsBusy = true;
        try { await LoadCoreAsync(); }
        finally { IsBusy = false; }
    }

    // Xoá sạch mọi điều kiện lọc về mặc định rồi Lọc lại từ trang 1 (như hub ClearFilter).
    [RelayCommand]
    private async Task ClearFilter()
    {
        SkuFilter = "";
        PriceMinText = "";
        PriceMaxText = "";
        SoldOnly = false;
        DupSkuOnly = false;
        SelectedAccountOption = AccountOptions.FirstOrDefault();   // sentinel → reset shop về "mọi"
        await Filter();
    }

    private async Task GoPage(int target)
    {
        if (IsBusy) return;
        Page = Math.Clamp(target, 1, PageCount);
        ClearSelectionInternal();
        IsBusy = true;
        try { await LoadCoreAsync(); }
        finally { IsBusy = false; }
    }

    [RelayCommand] private Task FirstPage() => GoPage(1);
    [RelayCommand] private Task PrevPage() => GoPage(Page - 1);
    [RelayCommand] private Task NextPage() => GoPage(Page + 1);
    [RelayCommand] private Task LastPage() => GoPage(PageCount);

    [RelayCommand]
    private async Task Refresh()
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await LoadCoreAsync(); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void ClearSelection() => ClearSelectionInternal();

    [RelayCommand]
    private async Task MarkSold()
    {
        if (IsBusy || _selectedKeys.Count == 0) return;
        var client = CoordinationRuntime.Client;
        if (client is null) return;
        var keys = _selectedKeys.ToList();
        var n = keys.Count;
        IsBusy = true;
        Status = "⏳ Đang đánh dấu đã bán…";
        try
        {
            await client.MarkProductsSoldAsync(keys);
            await LoadCoreAsync();   // GIỮ selection: dòng lên xanh + sold_count +1
            Status = $"✔ +1 đã bán cho {n} dòng.";
        }
        catch (Exception ex) { Status = "✘ Lỗi đánh dấu đã bán: " + FriendlyError(ex); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RegenSkus()
    {
        if (IsBusy || _selectedKeys.Count == 0) return;
        var client = CoordinationRuntime.Client;
        if (client is null) return;
        var keys = _selectedKeys.ToList();
        // Đếm số dòng ĐANG CHỌN có tên-sửa (sẽ bị vá đuôi tên theo SKU mới) để cảnh báo.
        var withName = Rows.Count(r => r.IsSelected && !string.IsNullOrWhiteSpace(r.NameRewritten));
        var msg = $"Sinh SKU MỚI (B#####) cho {keys.Count} dòng đã chọn"
                + (withName > 0 ? $" — {withName} dòng có tên-sửa sẽ được vá đuôi tên theo SKU mới" : "")
                + ". Tiếp tục?";
        if (!await Dialogs.ConfirmAsync(msg, "Sinh SKU mới")) return;
        IsBusy = true;
        Status = "⏳ Đang sinh SKU mới…";
        try
        {
            var done = await client.RegenProductSkusAsync(keys);
            await LoadCoreAsync();   // giữ selection (cùng trang) → thấy SKU/tên mới
            Status = $"✔ Đã cấp SKU mới cho {done} dòng.";
        }
        catch (Exception ex) { Status = "✘ Lỗi sinh SKU: " + FriendlyError(ex); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DeleteSelected()
    {
        if (IsBusy || _selectedKeys.Count == 0) return;
        var client = CoordinationRuntime.Client;
        if (client is null) return;
        var keys = _selectedKeys.ToList();
        var n = keys.Count;
        if (!await Dialogs.ConfirmAsync(
            $"Xoá VĨNH VIỄN {n} dòng đã chọn (kèm lịch sử đã bán của các dòng đó)? Không thể hoàn tác.", "Xoá dòng"))
            return;
        IsBusy = true;
        Status = "⏳ Đang xoá…";
        try
        {
            var del = await client.DeleteProductRowsAsync(keys);
            _selectedKeys.Clear();
            await LoadCoreAsync();
            SelectedCount = 0;
            Status = $"✔ Đã xoá {del} dòng.";
        }
        catch (Exception ex) { Status = "✘ Lỗi xoá: " + FriendlyError(ex); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task AddRow()
    {
        if (IsBusy) return;
        var vm = new RowEditViewModel(_accounts, _applied.Acct, _applied.Sheet);
        var ok = await WindowHost.ShowDialogAsync(new RowEditWindow(vm));
        if (ok != true) return;
        Status = vm.ResultStatus;
        // Chỉ reload nếu bộ lọc đang áp PHỦ (acct, sheet) vừa thêm (như hub FilterCovers). KHÔNG tự đổi bộ lọc.
        if (FilterCovers(vm.ResultAcct, vm.ResultSheet))
        {
            _selectedKeys.Clear();
            await Reload();
        }
    }

    [RelayCommand]
    private async Task EditRow(DataRowItem? item)
    {
        if (IsBusy || item is null) return;
        var vm = new RowEditViewModel(item.Model, item.AccLabel, item.ShopLabel);
        var ok = await WindowHost.ShowDialogAsync(new RowEditWindow(vm));
        if (ok != true) return;
        Status = vm.ResultStatus;
        await Reload();   // sửa: reload trang hiện tại
    }

    // Bộ lọc đang áp có phủ (acct, sheet)? (acc/sheet null = mọi → phủ; ngược lại phải khớp).
    private bool FilterCovers(string acct, string sheet) =>
        (_applied.Acct is null || _applied.Acct == acct) && (_applied.Sheet is null || _applied.Sheet == sheet);

    /// <summary>Rút gọn message lỗi cho dòng Status (chống tràn UI).</summary>
    internal static string FriendlyError(Exception ex)
    {
        var msg = ex.Message?.Trim() ?? "";
        if (msg.Length == 0) msg = ex.GetType().Name;
        return msg.Length > 140 ? msg[..140] + "…" : msg;
    }
}
