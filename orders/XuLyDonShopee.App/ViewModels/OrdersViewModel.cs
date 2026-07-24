using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.App.ViewModels;

/// <summary>
/// Một lựa chọn ở ComboBox lọc theo SHOP. Nhãn <see cref="Label"/> = tên đăng nhập shop (hoặc sentinel
/// "Tất cả shop"). <see cref="Id"/> luôn null (giữ lại chỉ để bớt churn XAML — shop không có id số; phân biệt
/// sentinel bằng <see cref="OrdersViewModel.IsAllShops"/> theo nhãn, KHÔNG theo Id).
/// </summary>
public sealed record AccountFilterOption(long? Id, string Label);

/// <summary>
/// Màn "Đơn hàng" (ĐỌC-CHỈ): lọc theo shop / trạng thái / tìm kiếm, hiển thị bảng đơn đã sync và
/// xuất các dòng đang lọc ra CSV (UTF-8 BOM). Không sửa/xóa đơn ở đây.
/// </summary>
public partial class OrdersViewModel : ViewModelBase
{
    /// <summary>Sentinel cho mục "tất cả" ở ComboBox trạng thái.</summary>
    public const string AllStatusesLabel = "Tất cả trạng thái";

    /// <summary>Sentinel cho mục "tất cả shop" ở ComboBox lọc shop (nhãn nhận diện sentinel — xem <see cref="IsAllShops"/>).</summary>
    public const string AllShopsLabel = "Tất cả shop";

    /// <summary>Option có phải sentinel "Tất cả shop" không (null hoặc nhãn = <see cref="AllShopsLabel"/>). Vì mọi
    /// option đều có Id null nên phân biệt theo NHÃN, không theo Id.</summary>
    internal static bool IsAllShops(AccountFilterOption? option)
        => option is null || option.Label == AllShopsLabel;

    /// <summary>Chờ nhỏ giữa hai lệnh in liên tiếp (tránh dội máy in / mở ồ ạt cửa sổ app PDF).</summary>
    private const int PrintDispatchDelayMs = 700;

    private readonly AppServices _services;

    /// <summary>Chặn requery khi đang dựng lại danh sách lựa chọn trong <see cref="Reload"/>.</summary>
    private bool _suppressApply;

    public OrdersViewModel(AppServices services)
    {
        _services = services;
        // Sync xong (phiên ghi đơn vào DB rồi phát OrdersChanged, có thể từ thread nền) → TỰ nạp lại màn.
        // VM sống suốt vòng đời app (MainViewModel giữ) cùng AppServices → không cần gỡ đăng ký.
        _services.OrdersChanged += OnOrdersChanged;
        Reload();
    }

    /// <summary>
    /// Phiên sync vừa ghi đơn vào DB (phát <see cref="AppServices.OrdersChanged"/> — CÓ THỂ từ thread nền của
    /// phiên) → MARSHAL về UI thread rồi <see cref="Reload"/> để màn đang mở tự đón đơn mới, GIỮ NGUYÊN bộ lọc
    /// tài khoản/trạng thái/tìm kiếm hiện tại. ObservableCollection <see cref="Rows"/> chỉ được đụng trên UI thread.
    /// </summary>
    private void OnOrdersChanged()
    {
        var ui = Avalonia.Threading.Dispatcher.UIThread;
        if (ui.CheckAccess())
        {
            Reload();
        }
        else
        {
            ui.Post(Reload);
        }
    }

    /// <summary>Lựa chọn của ComboBox lọc shop: "Tất cả shop" + từng tên shop (từ <c>AllShopLogins</c>).</summary>
    public ObservableCollection<AccountFilterOption> AccountOptions { get; } = new();

    /// <summary>Lựa chọn của ComboBox trạng thái: <see cref="AllStatusesLabel"/> + các trạng thái có thật.</summary>
    public ObservableCollection<string> StatusOptions { get; } = new();

    /// <summary>Các dòng đơn đang hiển thị (đã lọc).</summary>
    public ObservableCollection<OrderRowViewModel> Rows { get; } = new();

    [ObservableProperty]
    private AccountFilterOption? _selectedAccount;

    /// <summary>
    /// NGUỒN SỰ THẬT của bộ lọc shop: chuỗi text trong ô "Gõ tên shop". Lọc SỐNG theo từng ký tự
    /// (xem <see cref="OnAccountFilterTextChanged"/> + <see cref="Apply"/>):
    ///  · trống → tất cả shop (shopLogin=null);
    ///  · khớp ĐÚNG nhãn 1 shop (trim, không phân biệt hoa/thường) → đúng shop đó, khớp CHÍNH XÁC (như chọn gợi ý);
    ///  · gõ dở (vd "mex") → LIKE <c>%text%</c> trên cột <c>shop_login</c> (lọc phía SQL).
    /// <see cref="SelectedAccount"/> được đồng bộ theo text để dùng lại cho tên file CSV.
    /// </summary>
    [ObservableProperty]
    private string _accountFilterText = string.Empty;

    [ObservableProperty]
    private string? _selectedStatus;

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Thông báo kết quả xuất CSV (null = ẩn).</summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Nhãn tổng số đơn: dòng đang hiển thị (1 trang) / tổng khớp bộ lọc (mọi trang).</summary>
    public string TotalText => $"Đang hiển thị: {Rows.Count}/{TotalCount} đơn";

    // ===================== Phân trang (phía DB — LIMIT/OFFSET + COUNT) =====================

    /// <summary>Các cỡ trang cho ComboBox (mặc định 100).</summary>
    public int[] PageSizeOptions { get; } = { 50, 100, 200 };

    /// <summary>Số đơn mỗi trang. Đổi cỡ trang → về trang 1 (xem <see cref="OnPageSizeChanged"/>).</summary>
    [ObservableProperty]
    private int _pageSize = 100;

    /// <summary>Trang hiện tại (1-based). Đổi qua <see cref="PrevPageCommand"/>/<see cref="NextPageCommand"/>.</summary>
    [ObservableProperty]
    private int _currentPage = 1;

    private int _totalCount;

    /// <summary>Tổng số đơn KHỚP bộ lọc trên MỌI trang (từ <c>Count</c> phía DB) — mẫu số phân trang.</summary>
    public int TotalCount
    {
        get => _totalCount;
        private set => SetProperty(ref _totalCount, value);
    }

    /// <summary>Số trang = ceil(TotalCount / PageSize), tối thiểu 1 (TotalCount 0 → "Trang 1/1").</summary>
    public int TotalPages => Math.Max(1, (TotalCount + PageSize - 1) / Math.Max(1, PageSize));

    /// <summary>Nhãn "Trang x/y" cạnh nút chuyển trang.</summary>
    public string PageInfoText => $"Trang {CurrentPage}/{TotalPages}";

    /// <summary>
    /// Nạp lại từ DB: danh sách shop + trạng thái cho bộ lọc (giữ lựa chọn cũ nếu còn), rồi truy vấn
    /// theo bộ lọc hiện tại. Gọi khi mở màn hoặc bấm "Làm mới" (sau khi vừa sync thêm đơn).
    /// </summary>
    public void Reload()
    {
        _suppressApply = true;

        // Khôi phục bộ lọc theo TEXT đang gõ (nguồn sự thật) — KHÔNG chỉ theo id — để auto-refresh sau sync
        // không phá bộ lọc đang gõ dở (vd "mex"): text được giữ nguyên, chỉ dựng lại options + Apply.
        var prevText = (AccountFilterText ?? string.Empty).Trim();
        var prevStatus = SelectedStatus;

        AccountOptions.Clear();
        AccountOptions.Add(new AccountFilterOption(null, AllShopsLabel));
        foreach (var s in _services.Orders.AllShopLogins())
        {
            AccountOptions.Add(new AccountFilterOption(null, s));
        }
        // Đồng bộ SelectedAccount theo text: khớp đúng nhãn 1 shop → shop đó; trống/gõ dở → sentinel "tất cả shop".
        SelectedAccount = prevText.Length == 0
            ? AccountOptions[0]
            : AccountOptions.FirstOrDefault(o =>
                  string.Equals(o.Label.Trim(), prevText, StringComparison.OrdinalIgnoreCase)) ?? AccountOptions[0];

        // Trạng thái CHỈ của shop đang lọc → không chọn phải trạng thái không có đơn nào của shop đó.
        // (Gõ dở → SelectedAccount là sentinel → trạng thái của MỌI shop; chấp nhận đơn giản.)
        ReloadStatuses(IsAllShops(SelectedAccount) ? null : SelectedAccount!.Label, prevStatus);

        _suppressApply = false;
        Apply();
    }

    /// <summary>
    /// Dựng lại danh sách trạng thái cho ComboBox theo shop đang lọc (<paramref name="shopLogin"/>
    /// null = mọi shop). Giữ <paramref name="preferredStatus"/> nếu còn hợp lệ, không thì về
    /// <see cref="AllStatusesLabel"/>. Người gọi tự quản lý cờ <see cref="_suppressApply"/>.
    /// </summary>
    private void ReloadStatuses(string? shopLogin, string? preferredStatus)
    {
        StatusOptions.Clear();
        StatusOptions.Add(AllStatusesLabel);
        foreach (var s in _services.Orders.AllStatuses(shopLogin: shopLogin))
        {
            StatusOptions.Add(s);
        }
        SelectedStatus = preferredStatus is not null && StatusOptions.Contains(preferredStatus)
            ? preferredStatus
            : AllStatusesLabel;
    }

    /// <summary>
    /// Suy bộ lọc HIỆN TẠI (shop/trạng thái/tìm kiếm) từ text nguồn-sự-thật — dùng CHUNG cho
    /// <see cref="Apply"/> (1 trang), Xuất CSV và In nhiều đơn (MỌI trang) nên phạm vi luôn khớp nhau.
    /// Phạm vi shop quyết định TỪ text:
    ///  · trống               → mọi shop (shopLogin null);
    ///  · khớp ĐÚNG nhãn 1 shop → shop đó, khớp CHÍNH XÁC (shopExact=true; dùng nhãn shop, không dùng text gõ
    ///    để không lệch hoa/thường so với giá trị đã lưu);
    ///  · gõ dở              → LIKE <c>%text%</c> trên cột <c>shop_login</c> (shopExact=false; không khớp thì 0 đơn).
    /// Repo lọc <c>shop_login</c> phía SQL nên phân trang không thiếu dòng.
    /// </summary>
    private (string? shopLogin, bool shopExact, string? status, string? search) CurrentFilter()
    {
        var text = (AccountFilterText ?? string.Empty).Trim();
        var status = string.IsNullOrEmpty(SelectedStatus) || SelectedStatus == AllStatusesLabel
            ? null
            : SelectedStatus;
        var search = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText;

        string? shopLogin = null;
        bool shopExact = false;
        if (text.Length > 0)
        {
            var exact = AccountOptions.FirstOrDefault(o =>
                !IsAllShops(o) && string.Equals(o.Label.Trim(), text, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                shopLogin = exact.Label; // nhãn shop đã lưu (khớp chính xác, không lệch hoa/thường)
                shopExact = true;
            }
            else
            {
                shopLogin = text; // gõ dở → LIKE %text%
                shopExact = false;
            }
        }

        return (shopLogin, shopExact, status, search);
    }

    /// <summary>Nhãn cột "Shop" của một dòng: tên shop (<c>shop_login</c>), hoặc "(shop ?)" cho đơn cũ chưa gắn shop.</summary>
    private static string ShopLabelOf(Core.Models.OrderRow row)
        => string.IsNullOrWhiteSpace(row.ShopLogin) ? "(shop ?)" : row.ShopLogin!;

    /// <summary>
    /// Chạy truy vấn theo bộ lọc hiện tại và đổ TRANG HIỆN TẠI vào bảng (nhãn dòng = tên shop từ shop_login).
    /// Gọi <c>Count</c> trước để có <see cref="TotalCount"/>, clamp <see cref="CurrentPage"/> vào
    /// <c>[1, TotalPages]</c> (sync auto-refresh có thể giảm số trang) rồi <c>Query</c> với LIMIT/OFFSET.
    /// </summary>
    private void Apply()
    {
        var (shopLogin, shopExact, status, search) = CurrentFilter();

        // Thư mục hóa đơn: đọc MỘT LẦN khi nạp danh sách (config hoặc mặc định cạnh app.db) rồi truyền vào mỗi
        // dòng → link "In phiếu" (SlipPath) trỏ CÙNG chỗ nơi xử lý đơn LƯU phiếu. Đổi thư mục ở Cài đặt → bấm
        // "Làm mới" để các dòng đón đường dẫn mới.
        var invoiceDir = _services.Settings.GetInvoiceFolder();

        // Tổng khớp bộ lọc (mọi trang) → số trang. Clamp trang hiện tại; KHÔNG kéo về 1 giữa chừng (auto-refresh).
        TotalCount = _services.Orders.Count(status: status, searchText: search, shopLogin: shopLogin, shopExact: shopExact);
        if (CurrentPage > TotalPages)
        {
            CurrentPage = TotalPages;
        }
        else if (CurrentPage < 1)
        {
            CurrentPage = 1;
        }

        var offset = (CurrentPage - 1) * PageSize;

        Rows.Clear();
        foreach (var row in _services.Orders.Query(status: status, searchText: search,
                     limit: PageSize, offset: offset, shopLogin: shopLogin, shopExact: shopExact))
        {
            var label = ShopLabelOf(row);
            // notify: link "In phiếu" của dòng báo trạng thái (thiếu file / lỗi mở) ra StatusMessage của màn.
            // redownloadSlip: nút "Tải phiếu" nhờ phiên của tài khoản tải lại file phiếu thiếu (nếu phiên chạy).
            Rows.Add(new OrderRowViewModel(row, label, invoiceDir, msg => StatusMessage = msg, RedownloadSlipForRowAsync));
        }

        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(PageInfoText));
        PrevPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Trang trước (chặn dưới ở trang 1). Đổi trang → requery, KHÔNG reset về 1.</summary>
    [RelayCommand(CanExecute = nameof(CanPrevPage))]
    private void PrevPage()
    {
        if (CurrentPage <= 1)
        {
            return;
        }
        CurrentPage--;
        Apply();
    }

    private bool CanPrevPage() => CurrentPage > 1;

    /// <summary>Trang sau (chặn trên ở trang cuối). Đổi trang → requery, KHÔNG reset về 1.</summary>
    [RelayCommand(CanExecute = nameof(CanNextPage))]
    private void NextPage()
    {
        if (CurrentPage >= TotalPages)
        {
            return;
        }
        CurrentPage++;
        Apply();
    }

    private bool CanNextPage() => CurrentPage < TotalPages;

    /// <summary>
    /// Đường set THẲNG <see cref="SelectedAccount"/> từ code/test (vd <c>vm.SelectedAccount = ...</c>). Đồng bộ
    /// ngược <see cref="AccountFilterText"/> = nhãn shop (hoặc "" cho sentinel) để text vẫn là nguồn sự thật, nạp
    /// lại trạng thái theo shop, rồi Apply MỘT LẦN. Mọi set chéo qua <see cref="_suppressApply"/> để KHÔNG Apply đúp:
    /// khi lời gọi đến từ <see cref="OnAccountFilterTextChanged"/> (đã bật cờ) thì handler này thoát sớm.
    /// </summary>
    partial void OnSelectedAccountChanged(AccountFilterOption? value)
    {
        if (_suppressApply)
        {
            return;
        }

        _suppressApply = true;
        AccountFilterText = IsAllShops(value) ? string.Empty : value!.Label;
        ReloadStatuses(IsAllShops(value) ? null : value!.Label, SelectedStatus);
        CurrentPage = 1; // đổi bộ lọc shop → về trang 1
        _suppressApply = false;

        Apply();
    }

    /// <summary>
    /// Ô "Gõ tên shop" đổi (gõ từng ký tự, xóa, hoặc click gợi ý làm control tự đặt Text = Label). Text là NGUỒN
    /// SỰ THẬT: đồng bộ <see cref="SelectedAccount"/> theo quy tắc (trống/gõ dở → sentinel "tất cả"; khớp đúng nhãn
    /// → shop đó) rồi nạp lại trạng thái + Apply MỘT LẦN. Set chéo qua <see cref="_suppressApply"/> để không Apply đúp.
    /// </summary>
    partial void OnAccountFilterTextChanged(string value)
    {
        if (_suppressApply)
        {
            return;
        }

        var text = (value ?? string.Empty).Trim();
        var option = text.Length == 0
            ? AccountOptions[0] // sentinel "Tất cả shop"
            : AccountOptions.FirstOrDefault(o =>
                  string.Equals(o.Label.Trim(), text, StringComparison.OrdinalIgnoreCase)) ?? AccountOptions[0];

        _suppressApply = true;
        SelectedAccount = option;
        // Khớp đúng 1 shop → trạng thái của shop đó; trống/gõ dở (option sentinel) → trạng thái mọi shop.
        ReloadStatuses(IsAllShops(option) ? null : option.Label, SelectedStatus);
        CurrentPage = 1; // đổi bộ lọc shop → về trang 1
        _suppressApply = false;

        Apply();
    }

    partial void OnSelectedStatusChanged(string? value)
    {
        if (!_suppressApply)
        {
            CurrentPage = 1; // đổi bộ lọc trạng thái → về trang 1
            Apply();
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        if (!_suppressApply)
        {
            CurrentPage = 1;  // đổi từ khóa tìm → về trang 1
            Apply(); // tìm kiếm trực tiếp theo từng ký tự
        }
    }

    /// <summary>Đổi cỡ trang → về trang 1 rồi truy vấn lại (số trang đổi theo cỡ trang mới).</summary>
    partial void OnPageSizeChanged(int value)
    {
        if (!_suppressApply)
        {
            CurrentPage = 1;
            Apply();
        }
    }

    /// <summary>
    /// Double-click 1 dòng đơn (từ code-behind màn Đơn hàng) → mở hộp thoại thông tin cơ bản + đổi trạng thái.
    /// Nguồn cho ComboBox = các trạng thái ĐÃ SYNC về (<see cref="OrdersRepository.AllStatuses"/>, chuỗi tự do —
    /// KHÔNG enum). Người dùng chọn trạng thái KHÁC hiện tại rồi bấm "Lưu" → ghi cột <c>status</c> qua
    /// <see cref="OrdersRepository.UpdateStatus"/> rồi <see cref="Reload"/> để lưới hiện ngay. Hủy hoặc chọn
    /// trùng trạng thái cũ → không đổi gì.
    /// LƯU Ý: đây là SỬA TẠM (local-only) — KHÔNG đụng logic sync/gsheet/hub; lần sync sau lấy trạng thái thật
    /// từ Shopee sẽ ghi đè giá trị vừa đổi.
    /// </summary>
    public async Task EditOrderStatusAsync(OrderRowViewModel row)
    {
        var statuses = _services.Orders.AllStatuses();
        var newStatus = await DialogService.EditOrderAsync(row, statuses);
        if (string.IsNullOrEmpty(newStatus) || newStatus == row.Status)
        {
            return; // Hủy / chưa chọn / chọn trùng trạng thái cũ → không ghi gì
        }

        _services.Orders.UpdateStatus(row.AccountId, row.OrderSn, newStatus);
        Reload(); // đón trạng thái mới vào lưới (giữ nguyên bộ lọc/trang hiện tại)
    }

    /// <summary>
    /// Nút "Tải phiếu" trên một dòng đơn thiếu file: tìm phiên ĐANG chạy của tài khoản (qua
    /// <see cref="AccountSessionManager"/>) rồi nhờ nó tải lại phiếu. Phiên KHÔNG chạy → hướng dẫn mở phiên;
    /// phiên đang bận điều hướng → <see cref="AccountSession.RedownloadSlipAsync"/> trả false (báo thử lại sau).
    /// Chạy NỀN (async) — không block UI. Lưu được → phiên tự phát OrdersChanged nên lưới tự nạp lại (cột Phiếu
    /// đổi). Mọi lỗi → báo qua <see cref="StatusMessage"/>, KHÔNG crash.
    /// </summary>
    private async Task RedownloadSlipForRowAsync(OrderRowViewModel row)
    {
        var session = _services.Sessions.Get(row.AccountId);
        if (session is null || session.State != SessionState.Running)
        {
            StatusMessage = "Mở phiên tài khoản này trước (màn Tài khoản) rồi bấm Tải phiếu.";
            return;
        }

        StatusMessage = $"Đang tải lại phiếu đơn {row.OrderSn}...";
        bool ok;
        try
        {
            ok = await session.RedownloadSlipAsync(row.OrderSn);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Tải phiếu đơn {row.OrderSn} lỗi: {ex.Message}";
            return;
        }

        StatusMessage = ok
            ? $"Đã tải lại phiếu đơn {row.OrderSn}."
            : $"Chưa tải được phiếu đơn {row.OrderSn} (tài khoản đang bận thao tác khác hoặc không thấy đơn — thử lại sau, xem nhật ký).";
    }

    /// <summary>Nút "Làm mới": nạp lại toàn bộ từ DB (đón tài khoản/trạng thái/đơn mới sau khi sync) + áp bộ lọc.</summary>
    [RelayCommand]
    private void Refresh()
    {
        StatusMessage = null;
        Reload();
    }

    /// <summary>Nút "✕" trong ô lọc shop: xóa trắng text → về "Tất cả shop" (qua OnAccountFilterTextChanged).</summary>
    [RelayCommand]
    private void ClearAccountFilter()
    {
        AccountFilterText = string.Empty;
    }

    /// <summary>
    /// Nút "Xuất CSV": ghi ra file (UTF-8 BOM) qua SaveFileDialog. Xuất TOÀN BỘ đơn khớp bộ lọc trên MỌI
    /// trang (truy vấn KHÔNG phân trang cùng bộ lọc) — phân trang chỉ tối ưu hiển thị, không thu hẹp dữ liệu xuất.
    /// </summary>
    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        var (shopLogin, shopExact, status, search) = CurrentFilter();
        var invoiceDir = _services.Settings.GetInvoiceFolder();

        // KHÔNG dùng Rows (chỉ 1 trang) — truy vấn lại toàn bộ đơn khớp bộ lọc rồi map như Apply (giữ nguyên format CSV).
        var exportRows = _services.Orders.Query(status: status, searchText: search, shopLogin: shopLogin, shopExact: shopExact)
            .Select(row => new OrderRowViewModel(row, ShopLabelOf(row), invoiceDir))
            .ToList();

        if (exportRows.Count == 0)
        {
            StatusMessage = "Không có đơn nào để xuất.";
            return;
        }

        var count = exportRows.Count;
        var bytes = OrderCsvExporter.BuildCsvWithBom(exportRows.Select(r => r.ToExportRow()));

        string? saved;
        try
        {
            saved = await DialogService.SaveCsvAsync(SuggestFileName(), bytes);
        }
        catch (OperationCanceledException)
        {
            throw; // hủy tác vụ → ném xuyên, không nuốt
        }
        catch (Exception ex)
        {
            // Lỗi GHI thật (file .csv đang mở trong Excel, thiếu quyền, đĩa đầy...) → báo, KHÔNG để app crash.
            var failMessage = $"Xuất CSV thất bại: {ex.Message}";
            StatusMessage = failMessage;
            _services.Log.Append("Đơn hàng", failMessage);
            return;
        }

        if (saved is null)
        {
            return; // người dùng bấm Hủy → im lặng
        }

        var message = $"Đã xuất {count} đơn → {saved}";
        StatusMessage = message;
        _services.Log.Append("Đơn hàng", message);
    }

    /// <summary>
    /// Nút "In nhiều đơn": lấy MỌI đơn "Chờ lấy hàng" (<see cref="OrderRowViewModel.IsPendingPickup"/>) khớp
    /// bộ lọc shop/trạng thái/tìm kiếm trên TẤT CẢ các trang (truy vấn KHÔNG phân trang, KHÔNG dùng
    /// <see cref="Rows"/> — chỉ 1 trang) và gửi file PDF phiếu (<see cref="OrderRowViewModel.SlipPath"/>) tới
    /// máy in mặc định qua <see cref="PdfPrinter.TryPrint"/>. Thiếu file (đơn chưa xử lý / phiếu chưa tải) →
    /// đếm "thiếu file", KHÔNG in. Có chờ nhỏ giữa các lệnh in để tránh dội máy in. Báo tổng kết ra
    /// <see cref="StatusMessage"/> (đã in / thiếu file / lỗi in nếu có).
    /// </summary>
    [RelayCommand]
    private async Task PrintPendingSlipsAsync()
    {
        var (shopLogin, shopExact, status, search) = CurrentFilter();
        var invoiceDir = _services.Settings.GetInvoiceFolder();

        // Chụp danh sách đơn Chờ lấy hàng khớp bộ lọc trên MỌI trang NGAY lúc bấm (nhãn shop không cần cho SlipPath).
        var pending = _services.Orders.Query(status: status, searchText: search, shopLogin: shopLogin, shopExact: shopExact)
            .Select(row => new OrderRowViewModel(row, string.Empty, invoiceDir))
            .Where(r => r.IsPendingPickup)
            .ToList();
        if (pending.Count == 0)
        {
            StatusMessage = "Không có đơn Chờ lấy hàng nào trong danh sách để in.";
            return;
        }

        int printed = 0, missing = 0, failed = 0;
        foreach (var row in pending)
        {
            if (!File.Exists(row.SlipPath))
            {
                missing++; // đơn Chờ lấy hàng CHƯA có file phiếu (chưa xử lý / chưa tải) → bỏ qua, đếm thiếu
                continue;
            }

            if (PdfPrinter.TryPrint(row.SlipPath))
            {
                printed++;
            }
            else
            {
                failed++; // có file nhưng in lỗi (không có app PDF mặc định hỗ trợ verb print, file khóa...)
            }

            // Chờ nhỏ giữa các lệnh in — tránh dội máy in / mở ồ ạt cửa sổ app PDF.
            await Task.Delay(PrintDispatchDelayMs);
        }

        var message = $"Đã gửi in {printed} phiếu Chờ lấy hàng (thiếu file: {missing}).";
        if (failed > 0)
        {
            message += $" Lỗi in: {failed}.";
        }
        StatusMessage = message;
        _services.Log.Append("Đơn hàng", message);
    }

    /// <summary>Tên file gợi ý: <c>don-hang-{shop|tatca}-{yyyyMMdd-HHmm}.csv</c> (tên shop đã bỏ ký tự cấm).</summary>
    private string SuggestFileName()
    {
        var acc = SelectedAccount;
        var who = IsAllShops(acc) ? "tatca" : Sanitize(acc!.Label);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm");
        return $"don-hang-{who}-{stamp}.csv";
    }

    /// <summary>Thay các ký tự không hợp lệ trong tên file bằng '_'.</summary>
    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(cleaned) ? "tk" : cleaned;
    }
}
