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

/// <summary>Một lựa chọn ở ComboBox lọc theo tài khoản. <see cref="Id"/> null = "Tất cả tài khoản".</summary>
public sealed record AccountFilterOption(long? Id, string Label);

/// <summary>
/// Màn "Đơn hàng" (ĐỌC-CHỈ): lọc theo tài khoản / trạng thái / tìm kiếm, hiển thị bảng đơn đã sync và
/// xuất các dòng đang lọc ra CSV (UTF-8 BOM). Không sửa/xóa đơn ở đây.
/// </summary>
public partial class OrdersViewModel : ViewModelBase
{
    /// <summary>Sentinel cho mục "tất cả" ở ComboBox trạng thái.</summary>
    public const string AllStatusesLabel = "Tất cả trạng thái";

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

    /// <summary>Lựa chọn của ComboBox tài khoản: "Tất cả" + từng tài khoản.</summary>
    public ObservableCollection<AccountFilterOption> AccountOptions { get; } = new();

    /// <summary>Lựa chọn của ComboBox trạng thái: <see cref="AllStatusesLabel"/> + các trạng thái có thật.</summary>
    public ObservableCollection<string> StatusOptions { get; } = new();

    /// <summary>Các dòng đơn đang hiển thị (đã lọc).</summary>
    public ObservableCollection<OrderRowViewModel> Rows { get; } = new();

    [ObservableProperty]
    private AccountFilterOption? _selectedAccount;

    /// <summary>
    /// NGUỒN SỰ THẬT của bộ lọc tài khoản: chuỗi text trong ô "Gõ tên shop". Lọc SỐNG theo từng ký tự
    /// (xem <see cref="OnAccountFilterTextChanged"/> + <see cref="Apply"/>):
    ///  · trống → tất cả shop;
    ///  · khớp ĐÚNG nhãn 1 shop (trim, không phân biệt hoa/thường) → đúng shop đó (như chọn gợi ý);
    ///  · gõ dở (vd "mex") → lọc HỢP mọi shop có Email CHỨA text (lọc trong bộ nhớ).
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
    /// Nạp lại từ DB: danh sách tài khoản + trạng thái cho bộ lọc (giữ lựa chọn cũ nếu còn), rồi truy vấn
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
        AccountOptions.Add(new AccountFilterOption(null, "Tất cả tài khoản"));
        foreach (var a in _services.Accounts.GetAll())
        {
            AccountOptions.Add(new AccountFilterOption(a.Id, a.Email));
        }
        // Đồng bộ SelectedAccount theo text: khớp đúng nhãn 1 shop → shop đó; trống/gõ dở → sentinel "tất cả".
        SelectedAccount = prevText.Length == 0
            ? AccountOptions[0]
            : AccountOptions.FirstOrDefault(o =>
                  string.Equals(o.Label.Trim(), prevText, StringComparison.OrdinalIgnoreCase)) ?? AccountOptions[0];

        // Trạng thái CHỈ của tài khoản đang lọc → không chọn phải trạng thái không có đơn nào của TK đó.
        // (Gõ dở → SelectedAccount là sentinel → trạng thái của MỌI tài khoản; chấp nhận đơn giản.)
        ReloadStatuses(SelectedAccount?.Id, prevStatus);

        _suppressApply = false;
        Apply();
    }

    /// <summary>
    /// Dựng lại danh sách trạng thái cho ComboBox theo tài khoản đang lọc (<paramref name="accountId"/>
    /// null = mọi tài khoản). Giữ <paramref name="preferredStatus"/> nếu còn hợp lệ, không thì về
    /// <see cref="AllStatusesLabel"/>. Người gọi tự quản lý cờ <see cref="_suppressApply"/>.
    /// </summary>
    private void ReloadStatuses(long? accountId, string? preferredStatus)
    {
        StatusOptions.Clear();
        StatusOptions.Add(AllStatusesLabel);
        foreach (var s in _services.Orders.AllStatuses(accountId))
        {
            StatusOptions.Add(s);
        }
        SelectedStatus = preferredStatus is not null && StatusOptions.Contains(preferredStatus)
            ? preferredStatus
            : AllStatusesLabel;
    }

    /// <summary>
    /// Suy bộ lọc HIỆN TẠI (tài khoản/trạng thái/tìm kiếm) từ text nguồn-sự-thật — dùng CHUNG cho
    /// <see cref="Apply"/> (1 trang), Xuất CSV và In nhiều đơn (MỌI trang) nên phạm vi luôn khớp nhau.
    /// Phạm vi tài khoản quyết định TỪ text:
    ///  · trống              → mọi tài khoản (accountId null);
    ///  · khớp ĐÚNG nhãn option → đúng shop đó (sentinel Id null = tất cả);
    ///  · gõ dở             → HỢP các shop có Email CHỨA text (accountIds; RỖNG ⇒ không shop nào ⇒ 0 đơn).
    /// Repo lọc <c>accountIds</c> bằng <c>account_id IN (...)</c> phía SQL nên phân trang không thiếu dòng.
    /// </summary>
    private (long? accountId, string? status, string? search, IReadOnlyCollection<long>? accountIds) CurrentFilter()
    {
        var text = (AccountFilterText ?? string.Empty).Trim();
        var status = string.IsNullOrEmpty(SelectedStatus) || SelectedStatus == AllStatusesLabel
            ? null
            : SelectedStatus;
        var search = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText;

        long? queryAccountId = null;
        IReadOnlyCollection<long>? accountIds = null; // != null ⇒ chế độ gõ dở (HỢP các shop CHỨA text)
        if (text.Length > 0)
        {
            var exact = AccountOptions.FirstOrDefault(o =>
                string.Equals(o.Label.Trim(), text, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                queryAccountId = exact.Id; // sentinel → null = tất cả
            }
            else
            {
                accountIds = _services.Accounts.GetAll()
                    .Where(a => a.Email.Contains(text, StringComparison.OrdinalIgnoreCase))
                    .Select(a => a.Id)
                    .ToList();
            }
        }

        return (queryAccountId, status, search, accountIds);
    }

    /// <summary>
    /// Chạy truy vấn theo bộ lọc hiện tại và đổ TRANG HIỆN TẠI vào bảng (map account_id → nhãn tài khoản).
    /// Gọi <c>Count</c> trước để có <see cref="TotalCount"/>, clamp <see cref="CurrentPage"/> vào
    /// <c>[1, TotalPages]</c> (sync auto-refresh có thể giảm số trang) rồi <c>Query</c> với LIMIT/OFFSET.
    /// </summary>
    private void Apply()
    {
        var (queryAccountId, status, search, accountIds) = CurrentFilter();
        var labels = _services.Accounts.GetAll().ToDictionary(a => a.Id, a => a.Email);

        // Thư mục hóa đơn: đọc MỘT LẦN khi nạp danh sách (config hoặc mặc định cạnh app.db) rồi truyền vào mỗi
        // dòng → link "In phiếu" (SlipPath) trỏ CÙNG chỗ nơi xử lý đơn LƯU phiếu. Đổi thư mục ở Cài đặt → bấm
        // "Làm mới" để các dòng đón đường dẫn mới.
        var invoiceDir = _services.Settings.GetInvoiceFolder();

        // Tổng khớp bộ lọc (mọi trang) → số trang. Clamp trang hiện tại; KHÔNG kéo về 1 giữa chừng (auto-refresh).
        TotalCount = _services.Orders.Count(queryAccountId, status, search, accountIds);
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
        foreach (var row in _services.Orders.Query(queryAccountId, status, search, accountIds, PageSize, offset))
        {
            var label = labels.TryGetValue(row.AccountId, out var email) ? email : $"(TK #{row.AccountId})";
            // notify: link "In phiếu" của dòng báo trạng thái (thiếu file / lỗi mở) ra StatusMessage của màn.
            Rows.Add(new OrderRowViewModel(row, label, invoiceDir, msg => StatusMessage = msg));
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
        AccountFilterText = value?.Id is null ? string.Empty : value.Label;
        ReloadStatuses(value?.Id, SelectedStatus);
        CurrentPage = 1; // đổi bộ lọc tài khoản → về trang 1
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
            ? AccountOptions[0] // sentinel "Tất cả tài khoản"
            : AccountOptions.FirstOrDefault(o =>
                  string.Equals(o.Label.Trim(), text, StringComparison.OrdinalIgnoreCase)) ?? AccountOptions[0];

        _suppressApply = true;
        SelectedAccount = option;
        // Khớp đúng 1 shop → trạng thái của shop đó; trống/gõ dở (option sentinel) → trạng thái mọi tài khoản.
        ReloadStatuses(option.Id, SelectedStatus);
        CurrentPage = 1; // đổi bộ lọc tài khoản → về trang 1
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

    /// <summary>Nút "Làm mới": nạp lại toàn bộ từ DB (đón tài khoản/trạng thái/đơn mới sau khi sync) + áp bộ lọc.</summary>
    [RelayCommand]
    private void Refresh()
    {
        StatusMessage = null;
        Reload();
    }

    /// <summary>Nút "✕" trong ô lọc shop: xóa trắng text → về "tất cả tài khoản" (qua OnAccountFilterTextChanged).</summary>
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
        var (queryAccountId, status, search, accountIds) = CurrentFilter();
        var labels = _services.Accounts.GetAll().ToDictionary(a => a.Id, a => a.Email);
        var invoiceDir = _services.Settings.GetInvoiceFolder();

        // KHÔNG dùng Rows (chỉ 1 trang) — truy vấn lại toàn bộ đơn khớp bộ lọc rồi map như Apply (giữ nguyên format CSV).
        var exportRows = _services.Orders.Query(queryAccountId, status, search, accountIds)
            .Select(row =>
            {
                var label = labels.TryGetValue(row.AccountId, out var email) ? email : $"(TK #{row.AccountId})";
                return new OrderRowViewModel(row, label, invoiceDir);
            })
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
    /// bộ lọc tài khoản/trạng thái/tìm kiếm trên TẤT CẢ các trang (truy vấn KHÔNG phân trang, KHÔNG dùng
    /// <see cref="Rows"/> — chỉ 1 trang) và gửi file PDF phiếu (<see cref="OrderRowViewModel.SlipPath"/>) tới
    /// máy in mặc định qua <see cref="PdfPrinter.TryPrint"/>. Thiếu file (đơn chưa xử lý / phiếu chưa tải) →
    /// đếm "thiếu file", KHÔNG in. Có chờ nhỏ giữa các lệnh in để tránh dội máy in. Báo tổng kết ra
    /// <see cref="StatusMessage"/> (đã in / thiếu file / lỗi in nếu có).
    /// </summary>
    [RelayCommand]
    private async Task PrintPendingSlipsAsync()
    {
        var (queryAccountId, status, search, accountIds) = CurrentFilter();
        var invoiceDir = _services.Settings.GetInvoiceFolder();

        // Chụp danh sách đơn Chờ lấy hàng khớp bộ lọc trên MỌI trang NGAY lúc bấm (nhãn TK không cần cho SlipPath).
        var pending = _services.Orders.Query(queryAccountId, status, search, accountIds)
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

    /// <summary>Tên file gợi ý: <c>don-hang-{email|tatca}-{yyyyMMdd-HHmm}.csv</c> (email đã bỏ ký tự cấm).</summary>
    private string SuggestFileName()
    {
        var acc = SelectedAccount;
        var who = acc is null || acc.Id is null ? "tatca" : Sanitize(acc.Label);
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
