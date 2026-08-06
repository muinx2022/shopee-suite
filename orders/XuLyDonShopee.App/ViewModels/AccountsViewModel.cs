using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Data;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.App.ViewModels;

/// <summary>
/// Màn hình tài khoản: panel trái là danh sách + tìm kiếm, panel phải là form CRUD.
/// <para>Chia thành nhiều file <c>partial</c> theo khối việc — file này giữ DANH SÁCH (nạp/lọc/tick/xóa),
/// LỰA CHỌN và panel NHẬT KÝ; form Chi tiết ở <c>AccountsViewModel.Form.cs</c>; tab "Shops" (thống kê chuẩn bị
/// hàng, local + hub) ở <c>AccountsViewModel.KetQua.cs</c>; phiên chạy/bridge ở <c>AccountsViewModel.Phien.cs</c>.
/// Mọi property công khai vẫn nằm trên CÙNG một lớp nên XAML binding không đổi.</para>
/// </summary>
public partial class AccountsViewModel : ViewModelBase, IDisposable
{
    private readonly AppServices _services;
    private List<Account> _all = new();
    private bool _isRefreshing;

    /// <summary>Đang NẠP giá trị cờ từ Settings lúc khởi tạo → chặn setter ghi ngược lại DB (tránh write thừa).</summary>
    private bool _loadingSettings;

    /// <summary>Tập Id các tài khoản đang tick — nguồn BỀN để khôi phục tick khi danh sách dựng lại (search/
    /// Save/đổi tab/phiên lưu cookie), kể cả dòng đang bị ẩn do lọc. Không dùng để chạy/dừng nhóm (hai lệnh
    /// đó đọc trực tiếp <see cref="Accounts"/> đang hiển thị).</summary>
    private readonly HashSet<long> _selectedIds = new();

    public AccountsViewModel(AppServices services)
    {
        _services = services;

        // Nghe các phiên chạy nền để cập nhật nút/hiển thị theo TỪNG tài khoản (không còn cờ IsBusy toàn cục).
        // Sự kiện có thể đến từ thread nền → handler marshal về UI thread trước khi đụng UI (xem RunOnUi).
        _services.Sessions.Changed += OnSessionsChanged;

        // Panel log hiển thị theo TỪNG tài khoản: ActivityLog giữ buffer RIÊNG mỗi nguồn và báo về đã GOM NHÓM
        // (tối đa 1 lần/250ms cho mỗi nguồn), LUÔN trên UI thread → handler chỉ việc dựng lại chuỗi MỘT lần,
        // ĐỒNG BỘ, KHÔNG lock/await. Gỡ đăng ký ở Dispose (ActivityLog sống suốt vòng đời app).
        _services.Log.SourceUpdated += OnLogSourceUpdated;

        // Có nguồn NGOÀI màn này thêm tài khoản (sync shop từ BigSeller Insert dòng mới) → nghe để tự nạp lại
        // danh sách, thấy shop mới ngay không cần đổi màn. Sự kiện có thể đến từ thread nền → marshal về UI
        // thread (RunOnUi) trước khi đụng ObservableCollection.
        _services.AccountsChanged += OnAccountsChanged;

        // Vừa chuẩn bị xong 1 đơn → số ở tab "Shops" của tài khoản đó vừa tăng trong CSDL. Nghe để nạp lại
        // NGAY (chỉ khi đúng tài khoản đang mở), thay vì bắt người dùng đổi tài khoản/đổi ngày mới thấy số mới.
        _services.PrepareCountChanged += OnPrepareCountChanged;

        // Phiên vừa đọc được danh sách shop → dựng lưới tab "Shops" NGAY. Không có cái này thì mở app + chọn
        // tài khoản TRƯỚC khi phiên đọc shop sẽ thấy lưới TRỐNG mãi (chỉ hết khi bấm sang tk khác rồi bấm lại).
        _services.ShopListChanged += OnShopListChanged;

        // Phiên vào/ra một shop → cột tiến độ của tab "Shops" chuyển chấm + bật/tắt vòng quay. Cũng từ thread
        // nền của phiên → marshal về UI thread (RunOnUi) trước khi đụng ResultRows.
        _services.ShopCheckChanged += OnShopCheckChanged;

        // Banner lỗi địa chỉ vừa ghi/đóng → nạp lại list trên tab Shops (thread nền → RunOnUi).
        _services.AddressAlertsChanged += OnAddressAlertsChanged;

        // Nạp cờ "Xóa profile và tạo lại" từ Settings (bền qua restart). Setter tự LƯU nên chặn ghi ngược
        // trong lúc nạp bằng _loadingSettings.
        _loadingSettings = true;
        XoaProfileTaoLai = _services.Settings.GetSyncFreshProfile();
        TuDongXacNhan = _services.Settings.GetAutoConfirmEmail();
        _loadingSettings = false;

        Reload();

        // Máy chạy vòng liên tục CẢ ĐÊM: không có nhịp này thì ô ngày tab "Shops" đứng mãi ở ngày mở app —
        // qua nửa đêm số đóng băng ở hôm qua và đơn của ngày mới không hiện ra nữa. Callback chạy trên thread
        // nền nên tự marshal về UI thread (xem NhipSangNgay).
        _timerSangNgay = new System.Threading.Timer(_ => NhipSangNgay(), null, NhipDoSangNgay, NhipDoSangNgay);

        // Kéo Hub pickup-alerts khi đang mở tab Shops (ban đầu tắt — bật trong CapNhatTimerSyncPickupAlerts).
        _timerSyncPickupAlerts = new System.Threading.Timer(
            _ => NhipSyncPickupAlerts(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
    }

    /// <summary>Dọn đồng hồ dò sang ngày. VM sống suốt vòng đời app (dựng một lần trong <see cref="MainViewModel"/>)
    /// nên điểm dọn là lúc THOÁT app — <c>OrdersModuleHost.StopAsync</c>, đúng chỗ đang dọn các timer nền khác
    /// của module.</summary>
    public void Dispose()
    {
        try { _timerSangNgay.Dispose(); } catch { /* bỏ qua khi thoát */ }
        try { _timerSyncPickupAlerts.Dispose(); } catch { /* bỏ qua khi thoát */ }
        // ActivityLog sống suốt vòng đời app → không gỡ là VM này còn bị giữ lại (rò bộ nhớ).
        try { _services.Log.SourceUpdated -= OnLogSourceUpdated; } catch { /* bỏ qua khi thoát */ }
        try { _services.AddressAlertsChanged -= OnAddressAlertsChanged; } catch { /* bỏ qua khi thoát */ }
        try { _services.PrepareCountChanged -= OnPrepareCountChanged; } catch { /* bỏ qua khi thoát */ }
        try { _services.ShopListChanged -= OnShopListChanged; } catch { /* bỏ qua khi thoát */ }
        try { _services.ShopCheckChanged -= OnShopCheckChanged; } catch { /* bỏ qua khi thoát */ }
        try { _services.AccountsChanged -= OnAccountsChanged; } catch { /* bỏ qua khi thoát */ }
        try { _services.Sessions.Changed -= OnSessionsChanged; } catch { /* bỏ qua khi thoát */ }
    }

    /// <summary>Danh sách tài khoản đang hiển thị (sau khi lọc). Mỗi phần tử là <see cref="AccountRowViewModel"/>
    /// bọc <see cref="Account"/> + tick chọn + trạng thái phiên (chấm chạy / "Chờ lấy: N").</summary>
    public ObservableCollection<AccountRowViewModel> Accounts { get; } = new();

    /// <summary>Nhật ký của RIÊNG tài khoản đang chọn dưới dạng MỘT chuỗi (mỗi dòng một <c>Display</c>) — panel log
    /// là TextBox chỉ-đọc (bôi đen + Ctrl+C / chuột phải Copy được). Gán MỘT lần mỗi nhịp báo của
    /// <see cref="ActivityLog.SourceUpdated"/> (~250ms) trong <see cref="RebuildLogText"/>, không phải property
    /// tính toán — mỗi dòng log dồn về KHÔNG còn kéo theo một lượt dựng lại chuỗi + đo lại layout.</summary>
    [ObservableProperty]
    private string _logText = string.Empty;

    /// <summary>Đường dẫn file log hôm nay (hiển thị mờ dưới panel để biết file log ở đâu). Báo đổi ở mỗi lần
    /// <see cref="RebuildLogText"/> — không thì qua nửa đêm UI vẫn hiện file của hôm qua.</summary>
    public string LogPath => _services.Log.CurrentLogPath;

    /// <summary>Xóa các dòng đang hiển thị của TÀI KHOẢN đang chọn (KHÔNG xóa file log trên đĩa); chưa chọn
    /// tài khoản → xóa toàn bộ hiển thị. Panel tự dựng lại qua <see cref="ActivityLog.SourceUpdated"/>.</summary>
    [RelayCommand]
    private void ClearLog()
    {
        // Chụp Email đồng bộ (không await) — theo bài học không giữ tham chiếu SelectedRow qua await.
        var email = SelectedRow?.Email;
        if (email is not null)
        {
            _services.Log.Clear(email);
        }
        else
        {
            _services.Log.Clear();
        }
    }

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Cờ TOÀN CỤC "Xóa profile và tạo lại" (bền qua <see cref="SettingsRepository"/>): BẬT ⇒ mỗi phiên
    /// mở mới xóa hồ sơ trình duyệt của tài khoản rồi tạo lại sạch (phải đăng nhập lại). Setter LƯU NGAY vào
    /// Settings (trừ lúc đang nạp giá trị ban đầu).</summary>
    [ObservableProperty]
    private bool _xoaProfileTaoLai;

    partial void OnXoaProfileTaoLaiChanged(bool value)
    {
        if (_loadingSettings)
        {
            return; // đang nạp từ Settings lúc khởi tạo — không ghi ngược
        }

        _services.Settings.SetSyncFreshProfile(value);
    }

    /// <summary>Cờ TOÀN CỤC "Tự động xác nhận" (bền qua <see cref="SettingsRepository"/>): BẬT ⇒ khi Shopee bắt
    /// verify qua email, app tự tìm mail + bấm link "TẠI ĐÂY" + chờ đăng nhập; TẮT ⇒ chỉ đăng nhập hộp thư rồi
    /// DỪNG cho user tự bấm. Setter LƯU NGAY (trừ lúc đang nạp).</summary>
    [ObservableProperty]
    private bool _tuDongXacNhan;

    partial void OnTuDongXacNhanChanged(bool value)
    {
        if (_loadingSettings)
        {
            return;
        }

        _services.Settings.SetAutoConfirmEmail(value);
    }

    /// <summary>Bộ lọc "Chỉ hiện TK chưa xác nhận" đang bật — lọc client-side trên danh sách rows.</summary>
    [ObservableProperty]
    private bool _showOnlyUnverified;

    partial void OnShowOnlyUnverifiedChanged(bool value)
    {
        OnPropertyChanged(nameof(UnverifiedButtonText));
        RefreshList(_editingId ?? SelectedRow?.Id);
    }

    /// <summary>Số tài khoản đang mang cờ "TK chưa xác nhận" (đếm trên toàn bộ _all, không phụ thuộc lọc/tìm kiếm).</summary>
    public int UnverifiedCount => _all.Count(a => a.VerifyFailedAt is not null);

    /// <summary>Hiện nút lọc khi có ≥1 TK chưa xác nhận HOẶC đang bật lọc (để còn nút "Hiện tất cả" thoát ra
    /// kể cả khi số vừa về 0).</summary>
    public bool IsUnverifiedFilterVisible => UnverifiedCount > 0 || ShowOnlyUnverified;

    /// <summary>Nhãn nút lọc: đang lọc → "Hiện tất cả"; ngược lại → "Những TK chưa xác nhận (N)".</summary>
    public string UnverifiedButtonText => ShowOnlyUnverified
        ? "Hiện tất cả"
        : $"Những TK chưa xác nhận ({UnverifiedCount})";

    /// <summary>Bật/tắt bộ lọc "TK chưa xác nhận" (nút ở đầu danh sách).</summary>
    [RelayCommand]
    private void ToggleShowUnverified() => ShowOnlyUnverified = !ShowOnlyUnverified;

    /// <summary>Dòng đang chọn trong danh sách (bọc <see cref="Account"/>). Chỗ nào cần bản ghi gốc thì đọc
    /// <c>SelectedRow?.Account</c>.</summary>
    [ObservableProperty]
    private AccountRowViewModel? _selectedRow;

    /// <summary>Nguồn SÁNG/TẮT của nút 🗑: bật khi đang bôi đậm một dòng (<see cref="SelectedRow"/> — xóa dòng
    /// đó, hành vi cũ) HOẶC có ≥1 dòng đang hiển thị được tick (xóa hàng loạt theo tick). Notify lại tại mọi
    /// điểm tick/lựa chọn đổi (<see cref="ToggleRowTick"/>, <see cref="SelectAll"/>, <see cref="Reload"/>,
    /// <see cref="OnSelectedRowChanged"/>) để nút cập nhật kịp.</summary>
    public bool CanDelete => SelectedRow is not null || Accounts.Any(r => r.IsSelected);

    /// <summary>Tab chi tiết đang mở: 0 = Thông tin tài khoản, 1 = Shops. Click acc → nhảy sang Shops.</summary>
    [ObservableProperty]
    private int _detailTabIndex;

    partial void OnSearchTextChanged(string value)
    {
        // Bỏ qua khi đang cập nhật danh sách bằng code (tránh chạy lọc hai lần).
        if (_isRefreshing)
        {
            return;
        }

        // Lọc lại và giữ nguyên form đang sửa: reselect theo tài khoản đang chỉnh sửa.
        RefreshList(_editingId ?? SelectedRow?.Id);
    }

    partial void OnSelectedRowChanged(AccountRowViewModel? value)
    {
        // Đổi tài khoản đang chọn → dựng lại panel log theo tài khoản mới. Làm TRƯỚC guard _isRefreshing để
        // log luôn khớp SelectedRow ở mọi đường (kể cả khi RefreshList set lại lựa chọn dưới cờ refresh);
        // rebuild chỉ gán LogText, đồng bộ trên UI thread, không reentrancy.
        RebuildLogText();

        // Bôi đậm dòng đổi → nút 🗑 có thể đổi sáng/tắt. Đặt TRƯỚC guard _isRefreshing (giống RebuildFilteredLog)
        // để bám đúng cả đường refresh set lại lựa chọn dưới cờ.
        OnPropertyChanged(nameof(CanDelete));

        if (_isRefreshing)
        {
            return;
        }

        if (value != null)
        {
            // Chọn lại đúng tài khoản đang sửa dở → GIỮ nguyên form (không nạp đè, tránh mất dữ liệu).
            // Ngoài trường hợp đó thì nạp form của tài khoản vừa chọn.
            if (value.Id != _editingId)
            {
                IsNew = false;
                LoadIntoForm(value.Account);
                IsEditing = true;
            }

            // Plan B: bấm 1 tài khoản → nổi lên đầu danh sách + đưa cửa sổ Brave của nó ra trước (best-effort).
            BringSelectedToFront(value);

            // Click acc → mở tab Shops ngay (xem số chuẩn bị trong ngày), khỏi phải bấm tab tay.
            DetailTabIndex = 1;
        }
        else if (!IsNew)
        {
            IsEditing = false;
            ClearForm();
            DetailTabIndex = 0;
        }

        // Tab "Shops": nạp lưới Shop|Chuẩn bị hàng theo tài khoản vừa chọn (bỏ chọn → clear). Đặt SAU khi
        // form/_editingId đã đồng bộ (dùng SelectedRow.Id). Lưới hiện NGAY bằng số cục bộ, rồi hỏi hub đè số chung.
        LoadResults();
        LoadAddressAlertsFromLocal();
        _ = RefreshHubCountsAsync();
        _ = SyncAddressAlertsFromHubAsync();
        CapNhatTimerSyncPickupAlerts();
    }

    partial void OnDetailTabIndexChanged(int value)
    {
        CapNhatTimerSyncPickupAlerts();
        // Vào tab Shops → kéo Hub ngay (không chờ đủ 60s) để nhận dismiss từ máy khác sớm.
        if (value == 1 && SelectedRow is not null)
        {
            _ = SyncAddressAlertsFromHubAsync();
        }
    }

    /// <summary>
    /// Khi chọn một tài khoản CÓ phiên đang chạy → cố đưa cửa sổ Brave của phiên đó ra trước (focus).
    /// Best-effort — fail thì bỏ qua, không phá luồng. <b>KHÔNG</b> đổi thứ tự danh sách (theo yêu cầu người
    /// dùng: bấm vào tài khoản KHÔNG được làm danh sách nhảy thứ tự).
    /// </summary>
    private void BringSelectedToFront(AccountRowViewModel row)
    {
        var session = _services.Sessions.Get(row.Id);
        if (session is not null)
        {
            WindowFocus.BringToFront(session.BraveProcess);
        }
    }

    /// <summary>
    /// Một nguồn vừa có log mới (<see cref="ActivityLog.SourceUpdated"/> — đã gom nhóm, LUÔN nổ trên UI thread).
    /// Chỉ dựng lại panel khi nguồn đó ĐÚNG là tài khoản đang chọn; nguồn khác (tài khoản khác đang chạy,
    /// "Đơn hàng", "Hàng loạt"...) không tốn một lượt dựng chuỗi nào. So khớp KHÔNG phân biệt hoa/thường cho
    /// khớp cách <see cref="ActivityLog"/> gom buffer theo nguồn.
    /// </summary>
    private void OnLogSourceUpdated(string source)
    {
        // Chụp Email đang chọn MỘT LẦN (đồng bộ, không await) — không giữ tham chiếu SelectedRow.
        var email = SelectedRow?.Email;
        if (email is null || !string.Equals(email, source, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        RebuildLogText();
    }

    /// <summary>
    /// Dựng lại <see cref="LogText"/> từ buffer của tài khoản đang chọn (<see cref="ActivityLog.Snapshot"/> —
    /// tối đa <see cref="ActivityLog.MaxLinesPerSource"/> dòng MỚI NHẤT của RIÊNG tài khoản đó). Chụp Email MỘT
    /// LẦN vào biến cục bộ; toàn bộ ĐỒNG BỘ trên UI thread (không await xen giữa — bài học
    /// <c>viewmodel-mutable-field-after-await</c>). Chưa chọn tài khoản → panel rỗng.
    /// </summary>
    private void RebuildLogText()
    {
        var email = SelectedRow?.Email;
        LogText = email is null
            ? string.Empty
            : string.Join("\n", _services.Log.Snapshot(email).Select(e => e.Display));

        // Đường dẫn file đổi theo NGÀY → báo đổi ở đây là đủ (rẻ), khỏi đứng ở file hôm qua sau nửa đêm.
        OnPropertyChanged(nameof(LogPath));
    }

    /// <summary>Nạp lại danh sách từ DB, giữ lựa chọn/form nếu bản ghi còn tồn tại.</summary>
    public void Reload()
    {
        var selectId = _editingId ?? SelectedRow?.Id;
        _all = _services.Accounts.GetAll();
        RefreshList(selectId);
        OnPropertyChanged(nameof(CanDelete));
        // Cờ "TK chưa xác nhận" có thể đổi từ ngoài màn (autorun đánh dấu / phiên gỡ) → làm mới đếm + nút lọc.
        OnPropertyChanged(nameof(UnverifiedCount));
        OnPropertyChanged(nameof(IsUnverifiedFilterVisible));
        OnPropertyChanged(nameof(UnverifiedButtonText));
    }

    /// <summary>
    /// Dựng lại danh sách hiển thị theo bộ lọc hiện tại rồi chọn lại bản ghi <paramref name="selectId"/>.
    /// Việc gán SelectedRow được thực hiện dưới cờ <c>_isRefreshing</c> nên KHÔNG nạp đè form.
    /// </summary>
    private void RefreshList(long? selectId)
    {
        _isRefreshing = true;
        ApplyFilter();
        var match = selectId is long id ? Accounts.FirstOrDefault(a => a.Id == id) : null;
        SelectedRow = match;
        _isRefreshing = false;
    }

    private void ApplyFilter()
    {
        // Trước khi Clear: đồng bộ tick của các dòng ĐANG hiển thị vào tập bền (tick → thêm, bỏ tick → xóa).
        // Dòng đang bị ẩn (không có trong Accounts) GIỮ nguyên trạng thái cũ trong tập → không mất tick.
        foreach (var r in Accounts)
        {
            if (r.IsSelected)
            {
                _selectedIds.Add(r.Id);
            }
            else
            {
                _selectedIds.Remove(r.Id);
            }
        }

        Accounts.Clear();
        foreach (var account in _all.Where(a => PassesFilter(a, SearchText)
                                                && (!ShowOnlyUnverified || a.VerifyFailedAt is not null)))
        {
            // Dựng row VM bọc bản ghi; khôi phục tick theo Id; đồng bộ trạng thái phiên (chấm chạy / "Chờ lấy: N").
            var row = new AccountRowViewModel(account) { IsSelected = _selectedIds.Contains(account.Id) };
            row.SyncFromSession(_services.Sessions.Get(account.Id));
            Accounts.Add(row);
        }
    }

    private static bool PassesFilter(Account a, string? searchText)
    {
        var query = searchText?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            return true;
        }

        return (a.Email?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (a.Note?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    [RelayCommand]
    private void Add()
    {
        _isRefreshing = true;
        SelectedRow = null;
        _isRefreshing = false;

        IsNew = true;
        ClearForm();
        IsEditing = true;
        DetailTabIndex = 0; // thêm mới → form Thông tin, không nhảy Shops (chưa có shop)
    }

    /// <summary>
    /// "Kéo TK từ Hub" — hỏi Hub danh bạ sub-acc Đơn hàng rồi tạo sẵn bản ghi cục bộ cho các login máy CHƯA có.
    /// Việc hỏi-hub + ghi-DB nằm ở <see cref="HubDirectoryPuller"/>; màn hình chỉ rót nơi ghi nhật ký, dòng
    /// trạng thái và cách nạp lại danh sách.
    /// </summary>
    [RelayCommand]
    private Task KeoTuHubAsync()
        => new HubDirectoryPuller(_services).KeoAsync(
            m => _services.Log.Append(BatchLogSource, m),
            m => BusyStatus = m,
            Reload);

    /// <inheritdoc cref="HubDirectoryPuller.TinhLoginCanThem"/>
    public static List<string> TinhLoginCanThem(IEnumerable<string> hubLogins, IEnumerable<string> localEmails)
        => HubDirectoryPuller.TinhLoginCanThem(hubLogins, localEmails);

    /// <summary>
    /// "Chọn toàn bộ" — toggle trên danh sách ĐANG HIỂN THỊ (sau lọc): nếu chưa tick hết thì tick hết;
    /// nếu đã tick hết thì bỏ tick hết.
    /// </summary>
    [RelayCommand]
    private void SelectAll()
    {
        var allSelected = Accounts.Count > 0 && Accounts.All(r => r.IsSelected);
        var target = !allSelected;
        foreach (var row in Accounts)
        {
            row.IsSelected = target;
        }

        OnPropertyChanged(nameof(CanDelete));
    }

    /// <summary>
    /// Toggle tick của ĐÚNG một dòng (dùng khi bấm dòng/checkbox ở view). GIỮ khả năng tick nhiều: chỉ đổi
    /// dòng này, các dòng khác không đụng. KHÔNG đụng <see cref="SelectedRow"/> — việc chọn dòng (đổ Chi tiết
    /// + log) do <c>ListBox.SelectedItem</c> tự lo; tách bạch để reselect/Reload programmatic KHÔNG toggle tick.
    /// </summary>
    public void ToggleRowTick(AccountRowViewModel row)
    {
        row.IsSelected = !row.IsSelected;
        OnPropertyChanged(nameof(CanDelete));
    }

    /// <summary>
    /// Xóa tài khoản — ƯU TIÊN TICK: có ≥1 dòng ĐANG HIỂN THỊ được tick → xóa TẤT CẢ dòng tick đó (một lần xác
    /// nhận, ghi số lượng + tối đa 5 email đầu); không tick dòng nào → fallback xóa dòng đang bôi đậm
    /// (<see cref="SelectedRow"/>) như cũ. Chỉ xét tick trên <see cref="Accounts"/> (sau lọc) — nhất quán với
    /// "Sync đã chọn"/"Dừng đã chọn"; tick bền của dòng đang bị ẩn do tìm kiếm KHÔNG bị xóa. Trước khi xóa mỗi
    /// tài khoản gọi <c>Sessions.Stop</c> (no-op nếu không có phiên) để không mồ côi cửa sổ Brave, và gỡ id khỏi
    /// tập tick bền <see cref="_selectedIds"/>.
    /// </summary>
    [RelayCommand]
    private async Task DeleteAsync()
    {
        // Chụp (Id, Email) target TRƯỚC mọi await — không giữ tham chiếu row qua await (bài học sẵn trong file).
        var targets = Accounts.Where(r => r.IsSelected).Select(r => (r.Id, r.Email)).ToList();
        if (targets.Count == 0 && SelectedRow is not null)
        {
            targets = new List<(long Id, string Email)> { (SelectedRow.Id, SelectedRow.Email) };
        }

        if (targets.Count == 0)
        {
            return;
        }

        string message;
        if (targets.Count == 1)
        {
            message = $"Bạn có chắc muốn xóa tài khoản \"{targets[0].Email}\"? Thao tác này không thể hoàn tác.";
        }
        else
        {
            var emails = string.Join("\n", targets.Take(5).Select(t => t.Email));
            if (targets.Count > 5)
            {
                emails += $"\n… và {targets.Count - 5} tài khoản khác";
            }

            message = $"Bạn có chắc muốn xóa {targets.Count} tài khoản đã tick?\n{emails}\n" +
                      "Thao tác này không thể hoàn tác.";
        }

        var ok = await DialogService.ConfirmAsync("Xóa tài khoản", message);
        if (!ok)
        {
            return;
        }

        foreach (var target in targets)
        {
            _services.Sessions.Stop(target.Id); // no-op an toàn nếu tài khoản không có phiên — tránh Brave mồ côi.
            _services.Accounts.Delete(target.Id);
            _selectedIds.Remove(target.Id);
        }

        // Untick các dòng vừa xóa còn trong danh sách hiển thị CŨ (tra lại theo Id — không giữ ref row qua await):
        // vòng đồng bộ đầu của RefreshList đọc tick từ rows cũ, không untick thì id đã xóa bị NẠP LẠI vào
        // _selectedIds → SQLite cấp lại id cho tài khoản thêm sau ⇒ tài khoản mới tự dưng tick sẵn.
        var deletedIds = targets.Select(t => t.Id).ToHashSet();
        foreach (var row in Accounts.Where(r => deletedIds.Contains(r.Id)))
        {
            row.IsSelected = false;
        }

        IsNew = false;
        _isRefreshing = true;
        SelectedRow = null;
        _isRefreshing = false;
        IsEditing = false;
        ClearForm();
        Reload();
    }

    /// <summary>
    /// TẬP tài khoản đổi từ NGOÀI màn này (vd sync shop BigSeller Insert dòng mới) → marshal về UI thread rồi
    /// <see cref="Reload"/> để danh sách đón dòng mới ngay. <see cref="Reload"/> đã GIỮ lựa chọn/form/tick hiện
    /// tại (chọn lại theo <c>_editingId</c>/SelectedRow, khôi phục tick theo Id) nên ngữ nghĩa không đổi.
    /// </summary>
    private void OnAccountsChanged() => RunOnUi(Reload);

    /// <summary>Chạy <paramref name="action"/> trên UI thread (chạy ngay nếu đã ở UI thread).</summary>
    private static void RunOnUi(Action action) => Services.UiDispatch.Run(action);
}
