using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shopee.Core.BigSeller;
using Shopee.Core.Coordination;
using Shopee.Core.Products;
using Shopee.Suite.Services;

namespace Shopee.Suite.Modules.Data;

/// <summary>1 lựa chọn tài khoản cho combo lọc (Id null = tuỳ chọn "mọi"/"chọn").</summary>
public sealed record AccountOption(string? Id, string Label);
/// <summary>1 lựa chọn shop cho combo lọc (Sheet null = "mọi"/"chọn"; giá trị lọc = ShopeeDataSheet).</summary>
public sealed record ShopOption(string? Sheet, string Label);

/// <summary>
/// Màn "Dữ liệu sản phẩm" (client): lớp UI mỏng bọc <see cref="ProductGridEngine"/> (Shopee.Core) — engine giữ
/// TOÀN BỘ hành vi (lọc/trang/chọn nhiều/mark-sold/reset/regen/xoá/lưu 1 dòng + chuỗi status·confirm tiếng Việt),
/// VM chỉ map state engine → property bind + uỷ lệnh xuống engine. Kho SP thao tác QUA HTTP bằng
/// <see cref="HubApiProductDataOps"/> (HubClient). KHÔNG kế thừa ModuleViewModelBase (màn CRUD không cần log file).
/// Nguồn acc/shop = <see cref="BigSellerStore.Shared"/> (chỉ shop có ShopeeDataSheet); bộ lọc CHỈ áp khi bấm Lọc.
///
/// FIXED-ACCT (tab "Dữ liệu" ở Workspace): ctor <see cref="DataViewModel(string?)"/> ép mọi query về 1 tài khoản
/// (ẩn combo acc) qua <see cref="ProductGridEngine.SetScope"/>; đổi tài khoản đang xem qua <see cref="Rescope"/>.
/// </summary>
public sealed partial class DataViewModel : ObservableObject
{
    private const string AllAccountsLabel = "— mọi tài khoản —";
    private const string AllShopsLabel = "— mọi shop —";

    // Engine dùng chung — nguồn sự thật. Tạo LAZY ở EnsureLoaded (CoordinationRuntime.Client có thể null lúc ctor).
    private ProductGridEngine? _engine;

    // Chế độ FIXED-ACCT (tab Dữ liệu ở Workspace): ép mọi query về 1 acct, ẩn combo acc. Non-fixed = trang module.
    private readonly bool _isFixedMode;
    private string? _fixedAcctId;

    // Snapshot cấu hình (để resolve nhãn acc/shop cho lưới + dựng option combo). Làm mới khi kho đổi.
    private IReadOnlyList<BigSellerAccount> _accounts = Array.Empty<BigSellerAccount>();
    private Dictionary<string, string> _acctLabel = new();
    private Dictionary<(string, string), string> _shopName = new();

    private bool _applyingRows;   // đang dựng lại Rows → callback tick không mutate tập chọn engine
    private bool _syncing;        // đang map state engine → property (chặn OnPageSizeChanged gọi ngược engine)
    private bool _loaded;         // EnsureLoaded chạy 1 lần
    private IReadOnlyList<AllDataRow>? _lastRowsRef;   // Rows engine lần sync trước (đổi tham chiếu = phải dựng lại)

    // Confirm cho engine: bình thường hộp owner=MainWindow ("Dữ liệu sản phẩm"); khi RowEditWindow mở modal,
    // RowEditViewModel tạm trỏ về hộp owner=modal (tránh confirm "SKU trùng" bị khuất sau form) qua SetConfirmOverride.
    private readonly Func<string, Task<bool>> _defaultConfirm = m => Dialogs.ConfirmAsync(m, "Dữ liệu sản phẩm");
    private Func<string, Task<bool>>? _confirmOverride;
    private Task<bool> RouteConfirm(string msg) => (_confirmOverride ?? _defaultConfirm)(msg);
    internal void SetConfirmOverride(Func<string, Task<bool>>? confirm) => _confirmOverride = confirm;

    // ── Option lọc ──
    public ObservableCollection<AccountOption> AccountOptions { get; } = new();
    public ObservableCollection<ShopOption> ShopOptions { get; } = new();
    [ObservableProperty] private AccountOption? _selectedAccountOption;
    [ObservableProperty] private ShopOption? _selectedShopOption;

    /// <summary>Fixed-acct (Workspace) → ẩn combo tài khoản (khoá vào acct đang xem); trang module → hiện.</summary>
    public bool ShowAccountFilter => !_isFixedMode;

    // ── Ô lọc (chỉ áp khi bấm Lọc) ──
    [ObservableProperty] private string _skuFilter = "";
    [ObservableProperty] private string _priceMinText = "";
    [ObservableProperty] private string _priceMaxText = "";
    [ObservableProperty] private bool _soldOnly;
    [ObservableProperty] private bool _dupSkuOnly;

    // ── Phân trang ──
    public int[] PageSizes => ProductGridEngine.PageSizes;

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

    // ── Dòng + tập chọn (tập chọn nằm TRONG engine — VM chỉ vẽ lại cờ tick) ──
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

    /// <summary>Trang module "Dữ liệu sản phẩm": mọi acc/shop, hiện combo tài khoản.</summary>
    public DataViewModel()
    {
        // KHÔNG I/O trong ctor — engine + option acc/shop + query trang 1 nạp ở EnsureLoaded (View gọi lúc Loaded).
    }

    /// <summary>Tab "Dữ liệu" ở Workspace: ép mọi query về 1 tài khoản (ẩn combo acc). acctId null = mọi acc.</summary>
    public DataViewModel(string? fixedAcctId)
    {
        _isFixedMode = true;
        _fixedAcctId = fixedAcctId;
    }

    /// <summary>Lần đầu View hiển thị: tạo engine (nếu đã cấu hình Hub) + nạp option acc/shop + query trang 1,
    /// đăng ký làm mới khi kho đổi.</summary>
    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        RefreshAccountsSnapshot();
        BigSellerStore.Shared.Changed += OnStoreChanged;

        var client = CoordinationRuntime.Client;
        if (client is null)
        {
            // Chưa cấu hình Hub → banner suy giảm, KHÔNG tạo engine (tránh crash). Hành vi như cũ.
            OnPropertyChanged(nameof(DegradedMessage));
            OnPropertyChanged(nameof(IsDegraded));
            OnPropertyChanged(nameof(HubConfigured));
            return;
        }
        _engine = new ProductGridEngine(new HubApiProductDataOps(client), RouteConfirm);
        _engine.Changed += OnEngineChanged;
        if (_isFixedMode) _engine.SetScope(_fixedAcctId, null);
        SyncFromEngine();                              // state khởi tạo (rỗng) trước khi nạp
        _ = _engine.ApplyFilterAsync(BuildFilter());   // query trang 1
    }

    // Kho tài khoản đổi (đồng bộ Hub…) → làm mới snapshot + option trên UI thread (KHÔNG đụng bộ lọc đang áp).
    private void OnStoreChanged() => UiThread.Post(RefreshAccountsSnapshot);

    // Engine bắn Changed (từ thread bất kỳ) → marshal UI thread rồi vẽ lại.
    private void OnEngineChanged() => UiThread.Post(SyncFromEngine);

    // Map state engine → property VM đang bind. Rows dựng lại CHỈ khi tham chiếu Rows đổi (nạp mới) — đổi riêng
    // selection thì engine giữ nguyên list → chỉ vá lại cờ tick (tránh churn cả lưới mỗi lần tick 1 dòng).
    private void SyncFromEngine()
    {
        if (_engine is null) return;
        var e = _engine;
        _syncing = true;
        Page = e.Page;
        PageSize = e.PageSize;
        Total = e.Total;
        Status = e.Status;
        IsBusy = e.Busy;
        PgReady = e.PgReady;
        SelectedCount = e.SelectedCount;
        _syncing = false;

        if (!ReferenceEquals(e.Rows, _lastRowsRef))
        {
            _lastRowsRef = e.Rows;
            _applyingRows = true;
            Rows.Clear();
            foreach (var r in e.Rows)
            {
                var item = new DataRowItem(r, AcctLabel(r.AccountId), ShopLabel(r.AccountId, r.Sheet), OnRowSelectionToggled);
                item.IsSelected = e.IsSelected(item.Key);
                Rows.Add(item);
            }
            _applyingRows = false;
        }
        else
        {
            // Cùng tập dòng (chỉ đổi selection) → chỉ đồng bộ cờ chọn (bỏ qua callback tick nhờ _applyingRows).
            _applyingRows = true;
            foreach (var item in Rows) item.IsSelected = e.IsSelected(item.Key);
            _applyingRows = false;
        }
    }

    /// <summary>Workspace đổi tài khoản đang chọn → dời scope engine sang acct mới + dựng lại option shop + reset ô
    /// lọc rồi nạp lại (fire-and-forget). Chưa loaded/suy giảm → chỉ nhớ acct để EnsureLoaded dùng khi tạo engine.</summary>
    public void Rescope(string? acctId)
    {
        // Cùng tài khoản (Workspace Rebuild tạo lại instance mỗi lần kho đổi nhưng Id không đổi) → GIỮ bộ lọc
        // đang xem, KHÔNG reload; option shop tự làm mới qua OnStoreChanged của chính VM này.
        if (_fixedAcctId == acctId) return;
        _fixedAcctId = acctId;
        if (!_loaded || _engine is null) return;   // EnsureLoaded sẽ SetScope theo _fixedAcctId khi tạo engine
        _engine.SetScope(acctId, null);
        RefreshAccountsSnapshot();                 // acct cố định đổi → shop options theo acct mới
        SkuFilter = "";
        PriceMinText = "";
        PriceMaxText = "";
        SoldOnly = false;
        DupSkuOnly = false;
        _ = _engine.ClearFilterAsync();            // nạp lại theo scope mới
    }

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

        var curSheet = SelectedShopOption?.Sheet;

        AccountOptions.Clear();
        if (_isFixedMode)
        {
            // Combo acc ẩn — khoá đúng 1 option = acct cố định (hoặc "mọi" khi acctId null / acc đã xoá).
            var acct = _fixedAcctId is null ? null : _accounts.FirstOrDefault(a => a.Id == _fixedAcctId);
            AccountOptions.Add(acct is not null
                ? new AccountOption(acct.Id, acct.DisplayName)
                : new AccountOption(null, AllAccountsLabel));
        }
        else
        {
            AccountOptions.Add(new AccountOption(null, AllAccountsLabel));
            foreach (var a in _accounts)
                AccountOptions.Add(new AccountOption(a.Id, a.DisplayName));
        }

        var curAcctId = _isFixedMode ? _fixedAcctId : SelectedAccountOption?.Id;
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

    // Đổi cỡ trang → uỷ engine (nó về trang 1, bỏ chọn, nạp). Bỏ qua khi đang sync ngược / chưa có engine.
    partial void OnPageSizeChanged(int value)
    {
        if (_syncing || !_loaded || _engine is null) return;
        _ = _engine.SetPageSizeAsync(value);
    }

    private AllDataFilter BuildFilter() => new(
        Acct: string.IsNullOrEmpty(SelectedAccountOption?.Id) ? null : SelectedAccountOption!.Id,
        Sheet: string.IsNullOrEmpty(SelectedShopOption?.Sheet) ? null : SelectedShopOption!.Sheet,
        Sku: string.IsNullOrWhiteSpace(SkuFilter) ? null : SkuFilter.Trim(),
        PriceMin: ParsePrice(PriceMinText),
        PriceMax: ParsePrice(PriceMaxText),
        SoldOnly: SoldOnly,
        DupSkuOnly: DupSkuOnly,
        Text: null);   // màn /data không dùng tìm đa trường (có ô sku/giá riêng) — Text dành cho lưới per-shop

    private static long? ParsePrice(string? s) => long.TryParse((s ?? "").Trim(), out var v) && v > 0 ? v : null;

    private string AcctLabel(string id) => _acctLabel.TryGetValue(id, out var n) ? n : ShortId(id);
    // Ngăn không khớp shop nào (shop đã xoá / sheet lạ) → hiện khoá ngăn có 🔒 (như hub).
    private string ShopLabel(string acct, string sheet) => _shopName.TryGetValue((acct, sheet), out var n) ? n : "🔒 " + sheet;
    private static string ShortId(string id) => string.IsNullOrEmpty(id) ? "?" : (id.Length <= 8 ? id : id[..8] + "…");

    // Tick 1 dòng → cập nhật tập chọn engine (bỏ qua khi đang dựng lại Rows). Engine bắn Changed → SyncFromEngine.
    private void OnRowSelectionToggled(DataRowItem item)
    {
        if (_applyingRows) return;
        _engine?.SetSelected(item.Key, item.IsSelected);
    }

    // ══ Commands — chỉ uỷ lệnh xuống engine (engine tự quản Busy + Status + confirm) ══
    [RelayCommand]
    private Task Filter() => _engine?.ApplyFilterAsync(BuildFilter()) ?? Task.CompletedTask;

    // Xoá sạch mọi điều kiện lọc về mặc định rồi nạp lại từ trang 1 (như hub ClearFilter).
    [RelayCommand]
    private async Task ClearFilter()
    {
        SkuFilter = "";
        PriceMinText = "";
        PriceMaxText = "";
        SoldOnly = false;
        DupSkuOnly = false;
        if (ShowAccountFilter) SelectedAccountOption = AccountOptions.FirstOrDefault();   // sentinel → reset shop
        SelectedShopOption = ShopOptions.FirstOrDefault();
        if (_engine is not null) await _engine.ClearFilterAsync();
    }

    private Task GoPage(int target) => _engine?.GoPageAsync(target) ?? Task.CompletedTask;
    [RelayCommand] private Task FirstPage() => GoPage(1);
    [RelayCommand] private Task PrevPage() => GoPage(Page - 1);
    [RelayCommand] private Task NextPage() => GoPage(Page + 1);
    [RelayCommand] private Task LastPage() => GoPage(PageCount);

    [RelayCommand] private Task Refresh() => _engine?.ReloadAsync() ?? Task.CompletedTask;

    [RelayCommand] private void ClearSelection() => _engine?.ClearSelection();
    [RelayCommand] private void SelectAllPage() => _engine?.SelectAllOnPage();

    [RelayCommand] private Task MarkSold() => _engine?.MarkSoldAsync() ?? Task.CompletedTask;
    [RelayCommand] private Task ResetSold() => _engine?.ResetSoldAsync() ?? Task.CompletedTask;
    [RelayCommand] private Task RegenSkus() => _engine?.RegenSkusAsync() ?? Task.CompletedTask;
    [RelayCommand] private Task DeleteSelected() => _engine?.DeleteSelectedAsync() ?? Task.CompletedTask;

    [RelayCommand]
    private async Task AddRow()
    {
        if (_engine is null || _engine.Busy) return;
        // Prefill acc/shop từ bộ lọc đang áp (fixed-acct → Applied.Acct đã bị ép về acct cố định).
        var vm = new RowEditViewModel(_engine, SetConfirmOverride, _accounts, _engine.Applied.Acct, _engine.Applied.Sheet);
        var ok = await WindowHost.ShowDialogAsync(new RowEditWindow(vm));
        if (ok != true) return;
        Status = vm.ResultStatus;
        // Chỉ reload nếu bộ lọc đang áp PHỦ (acct, sheet) vừa thêm (FilterCovers). KHÔNG tự đổi bộ lọc.
        if (_engine.FilterCovers(vm.ResultAcct, vm.ResultSheet))
            await _engine.ReloadAsync();
    }

    [RelayCommand]
    private async Task EditRow(DataRowItem? item)
    {
        if (_engine is null || _engine.Busy || item is null) return;
        var vm = new RowEditViewModel(_engine, SetConfirmOverride, item.Model, item.AccLabel, item.ShopLabel);
        var ok = await WindowHost.ShowDialogAsync(new RowEditWindow(vm));
        if (ok != true) return;
        Status = vm.ResultStatus;
        await _engine.ReloadAsync();   // sửa: reload trang hiện tại
    }
}
