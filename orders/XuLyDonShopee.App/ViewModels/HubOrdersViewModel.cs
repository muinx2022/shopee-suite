using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XuLyDonShopee.App.Services;

namespace XuLyDonShopee.App.ViewModels;

/// <summary>
/// Ba ca "không có dòng nào" của màn "Đơn toàn hệ thống" PHẢI phân biệt được (không cùng hiện một lưới trống
/// câm) — cộng thêm ca lọc-không-ra và ca đang tải. Xem <see cref="HubOrdersViewModel.EmptyMessage"/>.
/// </summary>
public enum HubOrdersState
{
    /// <summary>Máy này chưa kết nối Hub (hook chưa rót / hub chưa cấu hình) — KHÁC hẳn "hub trả 0 đơn".</summary>
    NotConnected,
    /// <summary>Đang chờ Hub trả lời.</summary>
    Loading,
    /// <summary>Gọi được nhưng Hub không phản hồi (offline / timeout / hub cũ chưa có route).</summary>
    HubError,
    /// <summary>Hub trả về 0 đơn và KHÔNG có bộ lọc nào đang bật — Hub thật sự chưa có đơn.</summary>
    Empty,
    /// <summary>Hub trả về 0 đơn NHƯNG đang có bộ lọc — nới bộ lọc là thấy.</summary>
    FilteredEmpty,
    /// <summary>Có dữ liệu trên lưới.</summary>
    Loaded,
}

/// <summary>Một lựa chọn ở ô lọc SHOP. <see cref="Id"/> null = sentinel "Tất cả shop"; khác null = id shop TRÊN HUB.</summary>
public sealed record HubShopOption(long? Id, string Label);

/// <summary>
/// Màn "Đơn toàn hệ thống" (CHỈ ĐỌC): xem đơn của MỌI shop / MỌI máy, ĐỌC THẲNG từ Hub qua hook
/// <see cref="AppServices.QueryHubOrders"/> — <b>KHÔNG chép đơn về CSDL máy này</b> (đơn thuộc shop của máy
/// khác nên không có <c>account_id</c> local; chép vào bảng <c>orders</c> sẽ bị đẩy ngược lên Hub, ghi trùng
/// dòng Google Sheet, bị vòng dọn "đơn kết thúc" xoá, bị vòng chờ đẩy nhặt nhầm).
/// <para><b>LỌC + PHÂN TRANG CHẠY PHÍA HUB</b> (tham số <c>shopId/status/q/page/pageSize</c>) — màn không bao
/// giờ tải hết đơn về rồi lọc trong bộ nhớ.</para>
/// <para>Mọi lượt gọi Hub là BẤT ĐỒNG BỘ và HUỶ ĐƯỢC: đổi bộ lọc liên tục thì lượt trước bị huỷ qua
/// <see cref="_cts"/>, chỉ lượt mới nhất được đổ lên lưới.</para>
/// </summary>
public partial class HubOrdersViewModel : ViewModelBase
{
    /// <summary>Sentinel mục "tất cả" ở ô lọc shop.</summary>
    public const string AllShopsLabel = "Tất cả shop";

    /// <summary>Sentinel mục "tất cả" ở ô lọc trạng thái.</summary>
    public const string AllStatusesLabel = "Tất cả trạng thái";

    /// <summary>Chờ trước khi gọi Hub cho các thay đổi GÕ TỪNG KÝ TỰ (ô tìm kiếm) — mỗi ký tự một request qua
    /// tunnel là quá tốn; lượt cũ bị huỷ nên chỉ ký tự cuối thật sự đi.</summary>
    private const int SearchDebounceMs = 350;

    private readonly AppServices _services;

    /// <summary>Huỷ lượt gọi Hub TRƯỚC khi bắt đầu lượt mới (người dùng đổi bộ lọc liên tục).</summary>
    private CancellationTokenSource? _cts;

    /// <summary>Bản đồ id shop (trên Hub) → tên hiển thị. Nạp MỘT LẦN (lần tải đầu / khi bấm Tải lại) rồi TRA
    /// trong bộ nhớ — KHÔNG gọi Hub cho mỗi dòng đơn.</summary>
    private Dictionary<long, string> _shopNames = new();

    /// <summary>Đã nạp được danh sách shop lần nào chưa (false → lượt tải kế sẽ nạp lại).</summary>
    private bool _shopsLoaded;

    /// <summary>Chặn tải lại khi đang dựng lại danh sách lựa chọn / khôi phục selection trong lúc nạp.</summary>
    private bool _suppressReload;

    public HubOrdersViewModel(AppServices services)
    {
        _services = services;
        // KHÔNG gọi Hub ở ctor: VM được dựng lúc mở app (MainViewModel), gọi mạng ở đây sẽ làm chậm khởi động.
        // Màn tự tải lần đầu khi người dùng điều hướng tới (MainViewModel.OnSelectedNavIndexChanged).
        // ⇒ Đặt sentinel mặc định TRONG cờ chặn: set SelectedShop/SelectedStatus vốn kích hoạt một lượt tải.
        _suppressReload = true;
        ShopOptions.Add(new HubShopOption(null, AllShopsLabel));
        SelectedShop = ShopOptions[0];
        StatusOptions.Add(AllStatusesLabel);
        SelectedStatus = AllStatusesLabel;
        _suppressReload = false;
    }

    /// <summary>Lựa chọn ô lọc shop: "Tất cả shop" + từng shop trên Hub (theo tên).</summary>
    public ObservableCollection<HubShopOption> ShopOptions { get; } = new();

    /// <summary>Lựa chọn ô lọc trạng thái: "Tất cả trạng thái" + các trạng thái đã biết (xem <see cref="MergeStatuses"/>).</summary>
    public ObservableCollection<string> StatusOptions { get; } = new();

    /// <summary>Các dòng đơn của TRANG hiện tại (Hub đã lọc + phân trang sẵn).</summary>
    public ObservableCollection<HubOrderRowViewModel> Rows { get; } = new();

    [ObservableProperty]
    private HubShopOption? _selectedShop;

    [ObservableProperty]
    private string? _selectedStatus;

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Trạng thái màn — quyết định hiện lưới hay hiện thông báo nào (xem <see cref="EmptyMessage"/>).</summary>
    [ObservableProperty]
    private HubOrdersState _state = HubOrdersState.NotConnected;

    /// <summary>Các cỡ trang cho ComboBox (mặc định 100) — khớp màn "Đơn hàng".</summary>
    public int[] PageSizeOptions { get; } = { 50, 100, 200 };

    [ObservableProperty]
    private int _pageSize = 100;

    /// <summary>Trang hiện tại (1-based).</summary>
    [ObservableProperty]
    private int _currentPage = 1;

    private int _totalCount;

    /// <summary>Tổng số đơn KHỚP bộ lọc trên MỌI trang (Hub trả <c>total</c>) — mẫu số phân trang.</summary>
    public int TotalCount
    {
        get => _totalCount;
        private set => SetProperty(ref _totalCount, value);
    }

    /// <summary>Số trang = ceil(TotalCount / PageSize), tối thiểu 1.</summary>
    public int TotalPages => Math.Max(1, (TotalCount + PageSize - 1) / Math.Max(1, PageSize));

    public string PageInfoText => $"Trang {CurrentPage}/{TotalPages}";

    /// <summary>Nhãn tổng số: dòng đang hiển thị (1 trang) / tổng khớp bộ lọc (mọi trang, tính trên Hub).</summary>
    public string TotalText => $"Đang hiển thị: {Rows.Count}/{TotalCount} đơn (mọi máy)";

    /// <summary>Có đang chờ Hub trả lời không (nút Tải lại mờ đi trong lúc chờ).</summary>
    public bool IsLoading => State == HubOrdersState.Loading;

    /// <summary>
    /// Hiện LƯỚI hay hiện KHỐI THÔNG BÁO. Đang tải mà lưới ĐÃ có dòng (đổi trang / gõ tiếp) → GIỮ lưới cũ, không
    /// nháy sang khối thông báo rồi quay lại; chỉ khi chưa có gì để xem mới hiện "Đang tải đơn từ Hub…".
    /// </summary>
    public bool HasRows => State == HubOrdersState.Loaded
                           || (State == HubOrdersState.Loading && Rows.Count > 0);

    /// <summary>Ngược lại <see cref="HasRows"/> — khối thông báo chiếm chỗ lưới.</summary>
    public bool ShowMessage => !HasRows;

    /// <summary>
    /// Thông báo cho từng ca KHÔNG có dòng nào — BẮT BUỘC phân biệt được (chống "hỏng im lặng"): chưa kết nối
    /// Hub / Hub không phản hồi / Hub có 0 đơn là ba việc khác nhau, cách xử lý khác nhau.
    /// </summary>
    public string EmptyMessage => State switch
    {
        HubOrdersState.NotConnected => "Máy này chưa kết nối Hub — không xem được đơn toàn hệ thống.",
        HubOrdersState.Loading => "Đang tải đơn từ Hub…",
        HubOrdersState.HubError => "Không lấy được dữ liệu từ Hub (Hub không phản hồi). Thử Tải lại.",
        HubOrdersState.Empty => "Chưa có đơn nào trên Hub.",
        HubOrdersState.FilteredEmpty => "Không có đơn nào khớp bộ lọc (thử xoá bớt lọc shop / trạng thái / từ khoá).",
        _ => string.Empty,
    };

    /// <summary>Có bộ lọc nào đang bật không — để phân biệt "Hub trống" với "lọc không ra".</summary>
    private bool HasFilter =>
        SelectedShop?.Id is not null
        || (SelectedStatus is not null && SelectedStatus != AllStatusesLabel)
        || !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>Nút "Tải lại": nạp lại danh sách shop + trang hiện tại từ Hub (đón shop/đơn mới của máy khác).</summary>
    [RelayCommand]
    private Task RefreshAsync()
    {
        _shopsLoaded = false; // ép nạp lại danh sách shop (máy khác có thể vừa đăng ký shop mới)
        return LoadAsync();
    }

    /// <summary>Trang trước (chặn dưới ở trang 1).</summary>
    [RelayCommand(CanExecute = nameof(CanPrevPage))]
    private Task PrevPageAsync()
    {
        if (CurrentPage <= 1)
        {
            return Task.CompletedTask;
        }
        CurrentPage--;
        return LoadAsync();
    }

    private bool CanPrevPage() => CurrentPage > 1;

    /// <summary>Trang sau (chặn trên ở trang cuối).</summary>
    [RelayCommand(CanExecute = nameof(CanNextPage))]
    private Task NextPageAsync()
    {
        if (CurrentPage >= TotalPages)
        {
            return Task.CompletedTask;
        }
        CurrentPage++;
        return LoadAsync();
    }

    private bool CanNextPage() => CurrentPage < TotalPages;

    /// <summary>Xoá trắng ô tìm kiếm (nút ✕ trong ô).</summary>
    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    /// <summary>
    /// Tải MỘT TRANG đơn từ Hub theo bộ lọc hiện tại. Hook <see cref="AppServices.QueryHubOrders"/> chưa rót
    /// (app Đơn hàng chạy độc lập / hub chưa cấu hình) → <see cref="HubOrdersState.NotConnected"/>, KHÔNG gọi gì.
    /// Hook trả null (hub không phản hồi) → <see cref="HubOrdersState.HubError"/>. Lượt trước còn chạy → HUỶ.
    /// </summary>
    /// <param name="debounce">true khi gọi từ ô gõ từng ký tự — chờ <see cref="SearchDebounceMs"/> trước khi
    /// thật sự gọi Hub (lượt sau huỷ lượt trước nên chỉ ký tự cuối đi).</param>
    public async Task LoadAsync(bool debounce = false)
    {
        var query = _services.QueryHubOrders;
        if (query is null)
        {
            CancelInFlight();
            Rows.Clear();
            TotalCount = 0;
            SetState(HubOrdersState.NotConnected);
            return;
        }

        CancelInFlight();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var ct = cts.Token;

        SetState(HubOrdersState.Loading);
        try
        {
            if (debounce)
            {
                await Task.Delay(SearchDebounceMs, ct);
            }

            // Danh sách shop: MỘT LẦN cho cả màn (không phải mỗi dòng). Lấy không được → giữ bản cũ, cột Shop
            // lùi về "shop #id" — KHÔNG coi là lỗi tải đơn.
            if (!_shopsLoaded && _services.ListHubShops is { } listShops)
            {
                var shops = await listShops(ct);
                if (ct.IsCancellationRequested)
                {
                    return;
                }
                if (shops is not null)
                {
                    _shopNames = shops.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().Name);
                    _shopsLoaded = true;
                    RebuildShopOptions(shops);
                }
            }

            var result = await query(
                new HubOrdersQuery(SelectedShop?.Id, NormalizedStatus(), NormalizedSearch(), CurrentPage, PageSize),
                ct);
            if (ct.IsCancellationRequested)
            {
                return; // lượt này đã bị lượt mới thay thế → KHÔNG đụng lưới
            }
            if (result is null)
            {
                Rows.Clear();
                TotalCount = 0;
                SetState(HubOrdersState.HubError);
                return;
            }

            TotalCount = result.Total;
            Rows.Clear();
            foreach (var item in result.Items)
            {
                Rows.Add(new HubOrderRowViewModel(item, ShopLabelOf(item.ShopId)));
            }
            MergeStatuses(result.Items);

            SetState(Rows.Count > 0
                ? HubOrdersState.Loaded
                : (HasFilter ? HubOrdersState.FilteredEmpty : HubOrdersState.Empty));
        }
        catch (OperationCanceledException)
        {
            // Lượt cũ bị huỷ (đổi bộ lọc / gõ tiếp) → bỏ qua im lặng, lượt mới sẽ đặt trạng thái.
        }
        catch (Exception)
        {
            // Hook đã nuốt lỗi mạng thành null; tới đây là lỗi ngoài dự tính → vẫn báo đúng "không lấy được".
            Rows.Clear();
            TotalCount = 0;
            SetState(HubOrdersState.HubError);
        }
    }

    /// <summary>Huỷ + giải phóng lượt gọi Hub đang chạy (nếu có).</summary>
    private void CancelInFlight()
    {
        var old = _cts;
        _cts = null;
        if (old is null)
        {
            return;
        }
        try { old.Cancel(); } catch { /* đã dispose */ }
        old.Dispose();
    }

    /// <summary>Đặt trạng thái + phát lại các property dẫn xuất (thông báo, hiện/ẩn lưới, tổng số, phân trang).</summary>
    private void SetState(HubOrdersState state)
    {
        State = state;
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(ShowMessage));
        OnPropertyChanged(nameof(EmptyMessage));
        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(PageInfoText));
        PrevPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Trạng thái gửi lên Hub: sentinel "Tất cả trạng thái" → null (không lọc).</summary>
    private string? NormalizedStatus()
        => string.IsNullOrEmpty(SelectedStatus) || SelectedStatus == AllStatusesLabel ? null : SelectedStatus;

    /// <summary>Từ khoá gửi lên Hub: trắng → null (không lọc).</summary>
    private string? NormalizedSearch()
        => string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();

    /// <summary>Nhãn cột "Shop" của một dòng: tên tra từ danh sách shop Hub; chưa có → "shop #{id}".</summary>
    private string ShopLabelOf(long shopId)
        => _shopNames.TryGetValue(shopId, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : $"shop #{shopId}";

    /// <summary>Dựng lại ô lọc shop từ danh sách Hub (sentinel + từng shop theo tên), GIỮ shop đang chọn nếu còn.</summary>
    private void RebuildShopOptions(IReadOnlyList<(long Id, string Name)> shops)
    {
        var keepId = SelectedShop?.Id;
        _suppressReload = true;
        ShopOptions.Clear();
        ShopOptions.Add(new HubShopOption(null, AllShopsLabel));
        foreach (var s in shops.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(s => s.Id))
        {
            ShopOptions.Add(new HubShopOption(s.Id, s.Name));
        }
        SelectedShop = ShopOptions.FirstOrDefault(o => o.Id == keepId) ?? ShopOptions[0];
        _suppressReload = false;
    }

    /// <summary>
    /// Gộp thêm các trạng thái vào ô lọc, GIỮ mục đang chọn. Nguồn: trạng thái CÓ THẬT trong trang vừa nhận từ
    /// Hub + vốn từ vựng trạng thái của chính máy này (<c>orders.status</c> local, ĐỌC-CHỈ) — Hub chưa có route
    /// liệt kê trạng thái, mà hai nguồn này dùng CHUNG bộ chữ do Shopee sinh ra.
    /// </summary>
    private void MergeStatuses(IReadOnlyList<HubOrderView> items)
    {
        var all = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var s in StatusOptions.Skip(1))
        {
            all.Add(s);
        }
        foreach (var s in items.Select(i => i.Status).Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            all.Add(s!);
        }
        try
        {
            foreach (var s in _services.Orders.AllStatuses())
            {
                all.Add(s);
            }
        }
        catch
        {
            // CSDL local hỏng/khoá → bỏ qua, ô lọc vẫn có các trạng thái thấy từ Hub.
        }

        if (all.Count == StatusOptions.Count - 1 && all.All(StatusOptions.Contains))
        {
            return; // không đổi → khỏi dựng lại (tránh nhấp nháy ComboBox mỗi lượt tải)
        }

        var keep = SelectedStatus;
        _suppressReload = true;
        StatusOptions.Clear();
        StatusOptions.Add(AllStatusesLabel);
        foreach (var s in all)
        {
            StatusOptions.Add(s);
        }
        SelectedStatus = keep is not null && StatusOptions.Contains(keep) ? keep : AllStatusesLabel;
        _suppressReload = false;
    }

    // ── Đổi bộ lọc → về trang 1 rồi tải lại (bất đồng bộ, không chặn UI; lượt cũ tự bị huỷ) ──

    partial void OnSelectedShopChanged(HubShopOption? value)
    {
        if (!_suppressReload)
        {
            CurrentPage = 1;
            _ = LoadAsync();
        }
    }

    partial void OnSelectedStatusChanged(string? value)
    {
        if (!_suppressReload)
        {
            CurrentPage = 1;
            _ = LoadAsync();
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        if (!_suppressReload)
        {
            CurrentPage = 1;
            _ = LoadAsync(debounce: true); // gõ từng ký tự → chờ một nhịp rồi mới hỏi Hub
        }
    }

    partial void OnPageSizeChanged(int value)
    {
        if (!_suppressReload)
        {
            CurrentPage = 1;
            _ = LoadAsync();
        }
    }
}
