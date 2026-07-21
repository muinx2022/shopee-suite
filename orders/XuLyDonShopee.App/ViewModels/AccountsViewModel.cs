using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Data;
using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.App.ViewModels;

/// <summary>
/// Màn hình tài khoản: panel trái là danh sách + tìm kiếm, panel phải là form CRUD.
/// </summary>
public partial class AccountsViewModel : ViewModelBase
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
        _services.Sessions.CookieSaved += OnSessionCookieSaved;

        // Panel log hiển thị theo TỪNG tài khoản: nghe collection log CHUNG rồi lọc theo Source == Email của
        // tài khoản đang chọn. Sự kiện LUÔN nổ trên UI thread (ActivityLog.Append mutate Entries qua uiPost =
        // Dispatcher.UIThread.Post) → handler ĐỒNG BỘ, KHÔNG lock/await (rebuild là vòng for thuần).
        _services.Log.Entries.CollectionChanged += OnLogEntriesChanged;

        // Nạp cờ "Xóa profile và tạo lại" từ Settings (bền qua restart). Setter tự LƯU nên chặn ghi ngược
        // trong lúc nạp bằng _loadingSettings.
        _loadingSettings = true;
        XoaProfileTaoLai = _services.Settings.GetSyncFreshProfile();
        _loadingSettings = false;

        Reload();
    }

    /// <summary>Danh sách tài khoản đang hiển thị (sau khi lọc). Mỗi phần tử là <see cref="AccountRowViewModel"/>
    /// bọc <see cref="Account"/> + tick chọn + trạng thái phiên (chấm chạy / "Chờ lấy: N").</summary>
    public ObservableCollection<AccountRowViewModel> Accounts { get; } = new();

    /// <summary>Toàn bộ dòng nhật ký của MỌI phiên (collection CHUNG do <see cref="ActivityLog"/> giữ). Panel
    /// log KHÔNG bind trực tiếp vào đây nữa mà bind <see cref="FilteredLogEntries"/> (đã lọc theo tài khoản).</summary>
    public ObservableCollection<LogEntry> LogEntries => _services.Log.Entries;

    /// <summary>Các dòng nhật ký của RIÊNG tài khoản đang chọn (lọc <c>Source == SelectedRow.Email</c>) — nguồn
    /// hiển thị của panel log ở cột chi tiết. Rỗng khi chưa chọn tài khoản. Cập nhật ĐỒNG BỘ trên UI thread qua
    /// <see cref="OnLogEntriesChanged"/> (append dòng mới khớp) và <see cref="RebuildFilteredLog"/> (dựng lại).</summary>
    public ObservableCollection<LogEntry> FilteredLogEntries { get; } = new();

    /// <summary>Đường dẫn file log hôm nay (hiển thị mờ dưới panel để biết file log ở đâu).</summary>
    public string LogPath => _services.Log.CurrentLogPath;

    /// <summary>Xóa các dòng đang hiển thị của TÀI KHOẢN đang chọn (KHÔNG xóa file log trên đĩa); chưa chọn
    /// tài khoản → xóa toàn bộ hiển thị. Filtered tự cập nhật qua sự kiện CollectionChanged.</summary>
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

    /// <summary>Các lựa chọn trạng thái cho ComboBox.</summary>
    public static AccountStatus[] StatusOptions { get; } =
    {
        AccountStatus.ChuaKiemTra,
        AccountStatus.HoatDong,
        AccountStatus.BiKhoa
    };

    /// <summary>Giá trị mặc định của địa chỉ lấy hàng khi tài khoản chưa chọn.</summary>
    public const string DefaultPickupAddress = "Thanh Hóa";

    /// <summary>Danh sách cố định địa chỉ lấy hàng cho ComboBox trên form.</summary>
    public static string[] PickupAddressOptions { get; } = ["Hà Nội", "TP Hồ Chí Minh", "Thanh Hóa"];

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

    /// <summary>Dòng đang chọn trong danh sách (bọc <see cref="Account"/>). Chỗ nào cần bản ghi gốc thì đọc
    /// <c>SelectedRow?.Account</c>.</summary>
    [ObservableProperty]
    private AccountRowViewModel? _selectedRow;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private bool _isNew;

    [ObservableProperty]
    private string _editEmail = string.Empty;

    [ObservableProperty]
    private string _editPassword = string.Empty;

    [ObservableProperty]
    private string _editPhone = string.Empty;

    [ObservableProperty]
    private string _editCookie = string.Empty;

    [ObservableProperty]
    private string _editNote = string.Empty;

    /// <summary>API key KiotProxy riêng của tài khoản (để trống = dùng cấu hình chung / IP máy).</summary>
    [ObservableProperty]
    private string _editProxyKey = string.Empty;

    /// <summary>Địa chỉ lấy hàng mặc định của tài khoản (chọn từ <see cref="PickupAddressOptions"/>).</summary>
    [ObservableProperty]
    private string _editPickupAddress = DefaultPickupAddress;

    [ObservableProperty]
    private AccountStatus _editStatus = AccountStatus.ChuaKiemTra;

    [ObservableProperty]
    private string? _createdAtText;

    [ObservableProperty]
    private string? _updatedAtText;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _showPassword;

    /// <summary>Dòng hướng dẫn/trạng thái hiển thị (đổ từ phiên của tài khoản đang chọn; null = ẩn).</summary>
    [ObservableProperty]
    private string? _busyStatus;

    /// <summary>Trạng thái theo dõi đơn "Chờ Lấy Hàng" (đổ từ phiên của tài khoản đang chọn; null = ẩn).</summary>
    [ObservableProperty]
    private string? _orderStatus;

    /// <summary>Panel phải hiện chữ mờ khi không ở chế độ xem/sửa.</summary>
    public bool ShowPlaceholder => !IsEditing;

    /// <summary>Nhãn kích thước cookie hiển thị cạnh tiêu đề khối cookie.</summary>
    public string CookieSizeText => string.IsNullOrEmpty(EditCookie)
        ? "JSON · trống"
        : $"JSON · {System.Text.Encoding.UTF8.GetByteCount(EditCookie) / 1024.0:0.0} KB";

    /// <summary>True nếu tài khoản đang có cookie đăng nhập — dùng để hiện trạng thái gọn ("đã có/chưa có")
    /// thay cho ô hiển thị chuỗi cookie thô (đỡ dài form).</summary>
    public bool HasCookie => !string.IsNullOrWhiteSpace(EditCookie);

    /// <summary>Cho dừng khi tài khoản đang chọn có phiên đang chạy.</summary>
    public bool CanStopSeller => _editingId is not null && _services.Sessions.IsRunning(_editingId ?? -1);

    /// <summary>
    /// Cho nút "Sync" (TRỌN GÓI) khi đang xem/sửa một tài khoản ĐÃ LƯU (có Id) — KHÔNG phụ thuộc phiên đang
    /// chạy. Bấm khi phiên CHƯA mở → tự mở trang bán hàng, CHỜ sẵn sàng rồi mới chạy chuỗi trọn gói (kiểm
    /// tra → xử lý nếu có đơn → sync); phiên đã chạy → chạy ngay. Tài khoản mới chưa lưu (IsNew) → tắt nút.
    /// </summary>
    public bool CanSyncOrders => IsEditing && !IsNew && _editingId is not null;

    /// <summary>Id của tài khoản đang được nạp trong form (null = form trống / tạo mới).</summary>
    private long? _editingId;

    partial void OnIsEditingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowPlaceholder));
        OnPropertyChanged(nameof(CanStopSeller));
        OnPropertyChanged(nameof(CanSyncOrders));
    }

    partial void OnIsNewChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStopSeller));
        OnPropertyChanged(nameof(CanSyncOrders));
    }

    partial void OnEditCookieChanged(string value)
    {
        OnPropertyChanged(nameof(CookieSizeText));
        OnPropertyChanged(nameof(HasCookie));
    }

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
        // rebuild chỉ đụng FilteredLogEntries, đồng bộ trên UI thread, không reentrancy.
        RebuildFilteredLog();

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
        }
        else if (!IsNew)
        {
            IsEditing = false;
            ClearForm();
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
    /// Collection log CHUNG (<c>_services.Log.Entries</c>) vừa đổi — cập nhật <see cref="FilteredLogEntries"/>
    /// theo tài khoản đang chọn. LUÔN chạy trên UI thread (ActivityLog mutate Entries qua uiPost) → thao tác
    /// ĐỒNG BỘ, KHÔNG await/lock. Xử lý PER-ITEM để tránh rebuild lặp khi chạy lâu: Add → append dòng khớp
    /// nguồn; Remove (cắt ring-buffer cap 500 / <see cref="ActivityLog.Clear(string)"/>) → gỡ đúng dòng đó khỏi
    /// filtered (O(1) mỗi dòng, không rebuild); chỉ Reset/Replace/Move hoặc chưa chọn tài khoản → dựng lại toàn bộ.
    /// </summary>
    private void OnLogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Chụp Email đang chọn MỘT LẦN (đồng bộ, không await) — không giữ tham chiếu SelectedRow.
        var email = SelectedRow?.Email;

        if (e.Action == NotifyCollectionChangedAction.Add && email is not null && e.NewItems is not null)
        {
            foreach (var item in e.NewItems)
            {
                if (item is LogEntry entry && entry.Source == email)
                {
                    FilteredLogEntries.Add(entry);
                }
            }

            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems is not null)
        {
            // Cap ring-buffer / Clear(source) remove từng entry: chỉ gỡ đúng các entry đó khỏi filtered
            // (Remove so theo value-equality của record LogEntry — 2 dòng value-trùng thì gỡ bản tương đương,
            // hiển thị không đổi). email null → foreach không khớp → filtered vốn rỗng.
            foreach (var item in e.OldItems)
            {
                if (item is LogEntry entry && entry.Source == email)
                {
                    FilteredLogEntries.Remove(entry);
                }
            }

            return;
        }

        RebuildFilteredLog();
    }

    /// <summary>
    /// Dựng lại <see cref="FilteredLogEntries"/> từ log CHUNG theo tài khoản đang chọn (<c>Source == Email</c>).
    /// Chụp Email MỘT LẦN vào biến cục bộ; toàn bộ ĐỒNG BỘ trên UI thread (không await xen giữa — bài học
    /// <c>viewmodel-mutable-field-after-await</c>). Chưa chọn tài khoản → panel rỗng.
    /// </summary>
    private void RebuildFilteredLog()
    {
        var email = SelectedRow?.Email;
        FilteredLogEntries.Clear();
        if (email is null)
        {
            return;
        }

        foreach (var entry in _services.Log.Entries)
        {
            if (entry.Source == email)
            {
                FilteredLogEntries.Add(entry);
            }
        }
    }

    /// <summary>Nạp lại danh sách từ DB, giữ lựa chọn/form nếu bản ghi còn tồn tại.</summary>
    public void Reload()
    {
        var selectId = _editingId ?? SelectedRow?.Id;
        _all = _services.Accounts.GetAll();
        RefreshList(selectId);
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
        foreach (var account in _all.Where(a => PassesFilter(a, SearchText)))
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
    }

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
    }

    /// <summary>
    /// Toggle tick của ĐÚNG một dòng (dùng khi bấm dòng/checkbox ở view). GIỮ khả năng tick nhiều: chỉ đổi
    /// dòng này, các dòng khác không đụng. KHÔNG đụng <see cref="SelectedRow"/> — việc chọn dòng (đổ Chi tiết
    /// + log) do <c>ListBox.SelectedItem</c> tự lo; tách bạch để reselect/Reload programmatic KHÔNG toggle tick.
    /// </summary>
    public void ToggleRowTick(AccountRowViewModel row) => row.IsSelected = !row.IsSelected;

    /// <summary>"Dừng đã chọn" — dừng phiên của mọi tài khoản đang tick (Stop tự no-op nếu không có phiên).</summary>
    [RelayCommand]
    private void StopSelected()
    {
        foreach (var row in Accounts.Where(r => r.IsSelected).ToList())
        {
            _services.Sessions.Stop(row.Id);
        }

        UpdateSelectedSessionStatus();
    }

    /// <summary>"Dừng tất cả" — dừng mọi phiên đang chạy (đóng &amp; kill hết Brave).</summary>
    [RelayCommand]
    private async Task StopAllAsync()
    {
        await _services.Sessions.StopAllAsync();
        UpdateSelectedSessionStatus();
    }

    [RelayCommand]
    private void Save()
    {
        // User đăng nhập: có thể là email HOẶC tên đăng nhập bất kỳ (vd shopee_user01).
        // Chỉ bắt buộc không rỗng và không trùng; KHÔNG ép định dạng email nữa.
        var user = EditEmail?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(user))
        {
            ErrorMessage = "Tên đăng nhập (user) không được để trống.";
            return;
        }

        if (string.IsNullOrEmpty(EditPassword))
        {
            ErrorMessage = "Mật khẩu không được để trống.";
            return;
        }

        var duplicated = _all.Any(a =>
            a.Id != (_editingId ?? -1) &&
            string.Equals(a.Email, user, StringComparison.OrdinalIgnoreCase));
        if (duplicated)
        {
            ErrorMessage = "Tài khoản này đã tồn tại ở một tài khoản khác.";
            return;
        }

        ErrorMessage = null;

        Account account;
        if (IsNew || _editingId is null)
        {
            account = new Account
            {
                Email = user,
                Password = EditPassword,
                Phone = NullIfEmpty(EditPhone),
                Cookie = NullIfEmpty(EditCookie),
                Note = NullIfEmpty(EditNote),
                ProxyKey = NullIfEmpty(EditProxyKey),
                PickupAddress = EditPickupAddress,
                Status = EditStatus
            };
            _services.Accounts.Insert(account);
        }
        else
        {
            var existing = _services.Accounts.GetById(_editingId.Value);
            if (existing is null)
            {
                // Đã bị xóa ở đâu đó — báo lỗi và làm mới danh sách.
                ErrorMessage = "Không tìm thấy tài khoản để cập nhật (có thể đã bị xóa).";
                Reload();
                return;
            }

            existing.Email = user;
            existing.Password = EditPassword;
            existing.Phone = NullIfEmpty(EditPhone);
            existing.Cookie = NullIfEmpty(EditCookie);
            existing.Note = NullIfEmpty(EditNote);
            existing.ProxyKey = NullIfEmpty(EditProxyKey);
            existing.PickupAddress = EditPickupAddress;
            existing.Status = EditStatus;
            _services.Accounts.Update(existing);
            account = existing;
        }

        // Trạng thái nhất quán ngay sau khi ghi: form đang giữ đúng bản ghi vừa lưu.
        IsNew = false;
        _editingId = account.Id;

        // Nạp lại toàn bộ từ DB (lấy CreatedAt/UpdatedAt chuẩn).
        _all = _services.Accounts.GetAll();
        var saved = _all.FirstOrDefault(a => a.Id == account.Id);

        // Nếu bộ lọc hiện tại đang ẩn bản ghi vừa lưu → xóa từ khóa để nó luôn hiển thị và chọn được.
        if (saved != null && !PassesFilter(saved, SearchText))
        {
            _isRefreshing = true;
            SearchText = string.Empty;
            _isRefreshing = false;
        }

        RefreshList(account.Id);

        if (saved != null)
        {
            LoadIntoForm(saved);
            IsEditing = true;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsNew)
        {
            IsNew = false;
            IsEditing = false;
            ClearForm();
        }
        else if (_editingId is long id)
        {
            var record = _all.FirstOrDefault(a => a.Id == id) ?? _services.Accounts.GetById(id);
            if (record != null)
            {
                LoadIntoForm(record);
            }
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (SelectedRow is null)
        {
            return;
        }

        var target = SelectedRow.Account;
        var ok = await DialogService.ConfirmAsync(
            "Xóa tài khoản",
            $"Bạn có chắc muốn xóa tài khoản \"{target.Email}\"? Thao tác này không thể hoàn tác.");
        if (!ok)
        {
            return;
        }

        _services.Accounts.Delete(target.Id);
        IsNew = false;
        _isRefreshing = true;
        SelectedRow = null;
        _isRefreshing = false;
        IsEditing = false;
        ClearForm();
        Reload();
    }

    [RelayCommand]
    private void ToggleShowPassword() => ShowPassword = !ShowPassword;

    /// <summary>Dừng phiên của tài khoản đang chọn (đóng &amp; kill Brave của phiên đó, không ảnh hưởng phiên khác).</summary>
    [RelayCommand]
    private void Stop()
    {
        if (_editingId is null)
        {
            return;
        }

        _services.Sessions.Stop(_editingId.Value);
        UpdateSelectedSessionStatus();
    }

    /// <summary>
    /// "Sync" (TRỌN GÓI) — nút hành động chính màn Tài khoản: mở trang bán hàng (nếu chưa mở), kiểm tra đơn
    /// mới, xử lý đơn nếu có, rồi sync đơn hàng về máy + Google Sheet. Nút LUÔN bật khi đang xem tài khoản đã
    /// lưu: phiên đã chạy → chạy chuỗi ngay (đường cũ); phiên CHƯA mở → tự mở trang bán hàng, CHỜ sẵn sàng rồi
    /// mới chạy (xem <see cref="RunOrAutoStartAsync"/>). Tiến trình/kết quả từng bước hiển thị tự nhiên qua
    /// StatusText của phiên (đổ về <see cref="BusyStatus"/>).
    /// </summary>
    [RelayCommand]
    private Task SyncFull()
    {
        // Chụp accountId + email TRƯỚC mọi await (mẫu nút đơn cũ) — bám theo tài khoản đang mở trên form.
        if (_editingId is not long accountId)
        {
            return Task.CompletedTask;
        }

        var email = _services.Accounts.GetById(accountId)?.Email ?? EditEmail;
        return RunOrAutoStartAsync(accountId, email, "sync trọn gói", s => s.SyncFullAsync());
    }

    /// <summary>Nhãn nguồn log cho các thông báo cấp-BATCH (không thuộc một shop cụ thể) — ghi file &amp; phân
    /// biệt với log per-account (per-account dùng email của shop).</summary>
    private const string BatchLogSource = "Hàng loạt";

    /// <summary>Đang có một lượt "Sync đã chọn" (hàng loạt) chạy — guard chống bấm đúp cấp batch.</summary>
    private bool _syncSelectedRunning;

    /// <summary>
    /// "Sync đã chọn" (HÀNG LOẠT, TRỌN GÓI) — với MỌI tài khoản đang tick: phiên đã chạy → chạy chuỗi trọn
    /// gói ngay; phiên chưa mở → tự mở trang bán hàng, chờ sẵn sàng rồi chạy (dùng chung
    /// <see cref="RunOrAutoStartAsync"/> per-account với action <see cref="IAccountSession.SyncFullAsync"/> =
    /// kiểm tra → xử lý đơn nếu có → sync). Các tài khoản chạy SONG SONG, độc lập lỗi; guard
    /// <see cref="_syncSelectedRunning"/> chặn bấm đúp cả loạt.
    /// </summary>
    [RelayCommand]
    private async Task SyncSelectedAsync()
    {
        if (_syncSelectedRunning)
        {
            return;
        }

        _syncSelectedRunning = true;
        try
        {
            await RunSelectedBatchAsync("sync trọn gói", s => s.SyncFullAsync());
        }
        finally
        {
            _syncSelectedRunning = false;
        }
    }

    /// <summary>
    /// Chạy <paramref name="action"/> HÀNG LOẠT cho MỌI tài khoản đang tick, dùng chung luồng per-account
    /// <see cref="RunOrAutoStartAsync"/> (tự mở phiên nếu chưa mở → chờ sẵn sàng → hành động).
    /// <para>
    /// Chụp danh sách <c>(accountId, email)</c> các dòng tick MỘT LẦN — TRƯỚC mọi await (bài học
    /// <c>viewmodel-mutable-field-after-await</c>): KHÔNG giữ tham chiếu <see cref="AccountRowViewModel"/> qua
    /// await, chỉ dùng cặp đã chụp. Rỗng → log "Chưa tick tài khoản nào." rồi thôi. Các tài khoản chạy SONG
    /// SONG (<see cref="Task.WhenAll(IEnumerable{Task})"/>); MỖI lượt tự bọc try/catch + log lỗi theo email của
    /// shop đó → một shop lỗi/timeout KHÔNG phá các shop khác (OCE khi app đóng cũng thoát sạch). Xong hết →
    /// log tổng kết. Guard bấm-đúp per-account tái dùng <see cref="_autoStartingIds"/> trong RunOrAutoStartAsync.
    /// </para>
    /// </summary>
    private async Task RunSelectedBatchAsync(string actionName, Func<IAccountSession, Task<bool>> action)
    {
        // Chụp (accountId, email) của các dòng ĐANG tick MỘT LẦN, trước mọi await.
        var targets = Accounts
            .Where(r => r.IsSelected)
            .Select(r => (Id: r.Id, Email: r.Email))
            .ToList();

        if (targets.Count == 0)
        {
            _services.Log.Append(BatchLogSource, "Chưa tick tài khoản nào.");
            return;
        }

        // Mỗi tài khoản một lượt per-account ĐỘC LẬP LỖI, chạy song song.
        var runs = targets.Select(async target =>
        {
            try
            {
                await RunOrAutoStartAsync(target.Id, target.Email, actionName, action);
            }
            catch (Exception ex)
            {
                // Một shop lỗi/timeout KHÔNG phá các shop khác — nuốt + log theo email của shop đó.
                _services.Log.Append(target.Email, $"Lỗi khi {actionName}: {ex.Message}");
            }
        });

        await Task.WhenAll(runs);

        _services.Log.Append(BatchLogSource, $"Đã {actionName} xong {targets.Count} tài khoản đã chọn.");
    }

    /// <summary>
    /// Các tài khoản đang trong một lượt "tự mở phiên rồi chạy hành động" (Sync/Kiểm tra) — GUARD chống bấm
    /// đúp: khi một lượt còn đang chờ phiên sẵn sàng, mọi lượt bấm mới CHO CÙNG tài khoản (bất kỳ nút nào
    /// trong hai nút) bị bỏ qua nhẹ nhàng. Chỉ đụng trên UI thread (RelayCommand chạy trên UI thread và luồng
    /// KHÔNG dùng ConfigureAwait(false) nên mọi continuation ở lại UI thread) → HashSet thường là đủ.
    /// </summary>
    private readonly HashSet<long> _autoStartingIds = new();

    /// <summary>
    /// Luồng per-account dùng chung cho nút "Sync" (trọn gói) VÀ lệnh HÀNG LOẠT
    /// (<see cref="SyncSelectedAsync"/> gọi cho từng tài khoản đã tick):
    /// phiên ĐANG chạy → chạy <paramref name="action"/> ngay (đường cũ, giữ nguyên hành vi); phiên CHƯA mở →
    /// tự "Mở trang bán hàng" (qua manager, chạy nền) rồi CHỜ phiên "sẵn sàng thao tác" (đăng nhập xong + đọc
    /// số đơn lần đầu — xem <see cref="IsSessionReadyForActions"/>) tối đa 5 phút mới chạy hành động.
    /// <para>
    /// <paramref name="accountId"/> + <paramref name="email"/> do NGƯỜI GỌI chụp TRƯỚC mọi await (bài học
    /// <c>viewmodel-mutable-field-after-await</c>) — nút đơn chụp từ <c>_editingId</c>, nút hàng loạt chụp từng
    /// dòng tick. Toàn bộ luồng bám theo TÀI KHOẢN ĐÓ (session theo accountId), KHÔNG đọc lại
    /// SelectedRow/_editingId để quyết định. So <c>_editingId == accountId</c> về sau CHỈ để quyết có cập nhật ô
    /// hiển thị chung (BusyStatus) hay không (người dùng có thể đã chuyển chọn sang tài khoản khác). KHÔNG dùng
    /// ConfigureAwait(false) để mọi continuation ở lại UI thread → set BusyStatus + đụng guard HashSet an toàn.
    /// </para>
    /// </summary>
    private async Task RunOrAutoStartAsync(
        long accountId, string email, string actionName, Func<IAccountSession, Task<bool>> action)
    {
        // accountId + email đã do NGƯỜI GỌI chụp trước mọi await — hàm này KHÔNG đọc _editingId để lấy chúng.

        // Phiên đã SẴN SÀNG (đăng nhập xong + đọc số lần đầu của lần mở hiện tại) → chạy hành động ngay như
        // cũ. Dùng cờ ReadyForActions (KHÔNG chỉ State==Running): phiên đang "Đang tự đăng nhập..." vẫn là
        // Running nhưng cờ chưa bật → KHÔNG chạy ngay, rơi xuống luồng auto-start để CHỜ sẵn sàng (chống Lỗi 2).
        var existing = _services.Sessions.Get(accountId);
        if (existing is { ReadyForActions: true })
        {
            await action(existing);
            return;
        }

        // Phiên chưa sẵn sàng (null/Stopped/Error/Opening/đang-login) → tự mở (idempotent) rồi chờ sẵn sàng.
        // Guard chống bấm đúp: đã có lượt auto-start cho tài khoản này đang chờ → bỏ qua nhẹ nhàng NHƯNG để
        // lại DẤU VẾT log (kẻo bấm lượt 2 giữa lúc đang chờ mở phiên bị nuốt âm thầm → tưởng đã chạy).
        if (!_autoStartingIds.Add(accountId))
        {
            _services.Log.Append(email,
                $"Đang chờ mở phiên cho tài khoản này — bỏ qua lượt {actionName} lần này (thử lại sau khi phiên sẵn sàng).");
            return;
        }

        try
        {
            _services.Log.Append(email, $"Phiên chưa mở — đang mở trang bán hàng trước khi {actionName}...");

            // Mở phiên qua manager (chạy nền; idempotent nếu đang Opening) — đúng đường "Mở trang bán hàng".
            var session = _services.Sessions.Start(accountId);
            UpdateSelectedSessionStatus(); // cập nhật nút/hiển thị ngay

            // Chờ phiên sẵn sàng (đăng nhập xong + đọc số đơn lần đầu), tối đa 5 phút.
            var ready = await WaitForSessionReadyAsync(session, TimeSpan.FromMinutes(5));
            switch (ready)
            {
                case SessionReadyResult.Ready:
                    await action(session);
                    break;

                case SessionReadyResult.Ended:
                    // Phiên chuyển Stopped/Error giữa chừng — báo lý do rõ (LastError nếu có, không thì StatusText).
                    var endMsg = $"Không mở được phiên — {session.LastError ?? session.StatusText ?? "phiên đã dừng"}";
                    _services.Log.Append(email, endMsg);
                    if (_editingId == accountId)
                    {
                        BusyStatus = endMsg;
                    }
                    break;

                case SessionReadyResult.Timeout:
                    const string timeoutMsg =
                        "Phiên chưa sẵn sàng sau 5 phút (có thể cần đăng nhập tay) — thử lại sau.";
                    _services.Log.Append(email, timeoutMsg);
                    if (_editingId == accountId)
                    {
                        BusyStatus = timeoutMsg;
                    }
                    break;
            }
        }
        finally
        {
            _autoStartingIds.Remove(accountId);
        }
    }

    /// <summary>Kết quả chờ phiên "sẵn sàng thao tác" (xem <see cref="WaitForSessionReadyAsync"/>).</summary>
    private enum SessionReadyResult
    {
        /// <summary>Phiên đã sẵn sàng (đăng nhập xong + đọc được số đơn lần đầu).</summary>
        Ready,

        /// <summary>Phiên kết thúc giữa chừng (Stopped/Error) — không mở được.</summary>
        Ended,

        /// <summary>Quá thời hạn chờ mà vẫn chưa sẵn sàng (có thể cần đăng nhập tay).</summary>
        Timeout
    }

    /// <summary>
    /// Phiên "SẴN SÀNG THAO TÁC" theo CỜ TƯỜNG MINH <see cref="IAccountSession.ReadyForActions"/> của phiên.
    /// <b>Căn cứ:</b> cờ đó chỉ bật <c>true</c> tại đúng điểm sau khi luồng tự-đăng-nhập (<c>TryHumanLoginAsync</c>,
    /// đã await xong) hoàn tất VÀ đọc được số "Chờ Lấy Hàng" lần đầu của lần mở hiện tại — và được ĐẶT LẠI
    /// false ở đầu mỗi lần mở/relaunch + khi Stopped/Error (xem <c>AccountSession._readyForActions</c>). KHÔNG
    /// suy từ <c>ToShipCount != null</c> nữa vì số đơn không reset khi relaunch → dễ "sẵn sàng ảo" ngay trong
    /// lúc đang đăng nhập lại. Vẫn kèm <c>state == Running</c> làm lớp chốt (phòng cờ lỡ sót). Hàm thuần (test được).
    /// </summary>
    public static bool IsSessionReadyForActions(SessionState state, bool readyForActions)
        => state == SessionState.Running && readyForActions;

    /// <summary>
    /// Chờ tới khi <paramref name="session"/> "sẵn sàng thao tác" (<see cref="IsSessionReadyForActions"/>),
    /// poll mỗi <paramref name="pollMs"/> ms, tối đa <paramref name="timeout"/>. Phiên chuyển Stopped/Error
    /// giữa chừng → trả <see cref="SessionReadyResult.Ended"/> NGAY (không chờ hết giờ). CHỈ dựa vào tham số
    /// <paramref name="session"/> (đã chụp trước await) — KHÔNG đọc SelectedRow/_editingId. KHÔNG dùng
    /// ConfigureAwait(false) để giữ continuation trên UI thread cho phần gọi cập nhật BusyStatus an toàn.
    /// </summary>
    private static async Task<SessionReadyResult> WaitForSessionReadyAsync(
        IAccountSession session, TimeSpan timeout, int pollMs = 1000)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var state = session.State;
            if (state is SessionState.Stopped or SessionState.Error)
            {
                return SessionReadyResult.Ended;
            }

            if (IsSessionReadyForActions(state, session.ReadyForActions))
            {
                return SessionReadyResult.Ready;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return SessionReadyResult.Timeout;
            }

            await Task.Delay(pollMs);
        }
    }

    /// <summary>
    /// Xử lý sự kiện đổi trạng thái của các phiên (có thể đến từ thread nền) — marshal về UI thread rồi
    /// đổ trạng thái phiên của tài khoản đang chọn vào ô hiển thị + cập nhật nút.
    /// </summary>
    private void OnSessionsChanged() => RunOnUi(() =>
    {
        // Đổ trạng thái phiên vào TỪNG dòng (chấm chạy / "Chờ lấy: N") + cập nhật ô hiển thị của form.
        SyncAllRows();
        UpdateSelectedSessionStatus();
    });

    /// <summary>
    /// Đồng bộ trạng thái phiên vào mọi dòng đang hiển thị. LUÔN chạy trên UI thread (gọi từ
    /// <see cref="RunOnUi"/>) — chỉ đọc <see cref="Accounts"/> và set thuộc tính row, KHÔNG cấu trúc lại
    /// ObservableCollection từ thread nền.
    /// </summary>
    private void SyncAllRows()
    {
        foreach (var row in Accounts)
        {
            row.SyncFromSession(_services.Sessions.Get(row.Id));
        }
    }

    /// <summary>
    /// Một phiên nền vừa lưu cookie vào DB cho <paramref name="accountId"/> — marshal về UI thread để dựng
    /// lại danh sách (ObservableCollection chỉ được đụng trên UI thread) và cập nhật form nếu đang mở đúng
    /// tài khoản đó.
    /// </summary>
    private void OnSessionCookieSaved(long accountId) => RunOnUi(() => RefreshAfterCookieSaved(accountId));

    /// <summary>Chạy <paramref name="action"/> trên UI thread (chạy ngay nếu đã ở UI thread).</summary>
    private static void RunOnUi(Action action)
    {
        var ui = Avalonia.Threading.Dispatcher.UIThread;
        if (ui.CheckAccess())
        {
            action();
        }
        else
        {
            ui.Post(action);
        }
    }

    /// <summary>Đổ trạng thái/số đơn của phiên theo tài khoản ĐANG CHỌN vào ô hiển thị; cập nhật nút mở/dừng.</summary>
    private void UpdateSelectedSessionStatus()
    {
        var id = _editingId ?? SelectedRow?.Id;
        var session = id is long sid ? _services.Sessions.Get(sid) : null;

        BusyStatus = session?.StatusText;
        OrderStatus = FormatOrderStatus(session?.ToShipCount);

        OnPropertyChanged(nameof(CanStopSeller));
        OnPropertyChanged(nameof(CanSyncOrders));
    }

    /// <summary>Định dạng dòng theo dõi đơn "Chờ Lấy Hàng" từ số đọc được (null = ẩn).</summary>
    private static string? FormatOrderStatus(int? count)
    {
        if (count is not int n)
        {
            return null;
        }

        return n > 0
            ? $"Chờ Lấy Hàng: {n} đơn — vẫn theo dõi mỗi 30'."
            : "Chờ Lấy Hàng: 0 — kiểm lại sau 30'.";
    }

    /// <summary>
    /// Sau khi một phiên nền đã ghi cookie vào DB cho <paramref name="accountId"/>, CẬP NHẬT TẠI CHỖ — KHÔNG
    /// dựng lại cả danh sách. Danh sách không hiển thị cookie nên không cần rebuild; rebuild ở đây (sự kiện
    /// <c>CookieSaved</c> bắn liên tục khi nhiều phiên đăng nhập + theo dõi 30') sẽ xóa tick người dùng và
    /// đảo thứ tự "nổi lên đầu". Chỉ cần: (1) cập nhật cookie/UpdatedAt lên đúng instance <see cref="Account"/>
    /// đang có trong <c>_all</c> (row bọc CHÍNH instance này → Save sau không ghi đè cookie về null), (2) nếu
    /// đang MỞ đúng tài khoản đó thì cập nhật form. Chạy trên UI thread (gọi từ <see cref="RunOnUi"/>).
    /// </summary>
    private void RefreshAfterCookieSaved(long accountId)
    {
        var fresh = _services.Accounts.GetById(accountId);
        if (fresh is null)
        {
            return; // tài khoản đã bị xóa — không có gì để cập nhật
        }

        // Cập nhật cookie/UpdatedAt trên instance đang có trong _all (row bọc chính instance này) → GIỮ tick
        // + thứ tự (không đụng ObservableCollection).
        var cached = _all.FirstOrDefault(a => a.Id == accountId);
        if (cached is not null)
        {
            cached.Cookie = fresh.Cookie;
            cached.UpdatedAt = fresh.UpdatedAt;
        }

        // Đang mở đúng tài khoản đó → cập nhật form (EditCookie đổi → HasCookie/CookieSizeText tự cập nhật).
        if (_editingId == accountId)
        {
            EditCookie = fresh.Cookie ?? string.Empty;
            UpdatedAtText = FormatDate(fresh.UpdatedAt);
        }
    }

    /// <summary>
    /// Chọn proxy thủ công kế tiếp theo round-robin BỀN. Nay dùng chung chỉ số của
    /// <see cref="AccountSessionManager"/> (một nguồn duy nhất, chia sẻ giữa các phiên song song).
    /// </summary>
    public ProxyEntry? NextManualProxy(IReadOnlyList<ProxyEntry> manual)
        => _services.Sessions.NextManualProxy(manual);

    /// <summary>Kết quả của thao tác lưu cookie đã bắt được vào tài khoản.</summary>
    public enum SaveCookieResult
    {
        /// <summary>JSON không chứa cookie nào (người dùng có thể chưa đăng nhập xong).</summary>
        NoCookie,

        /// <summary>Không còn tài khoản targetId trong DB (có thể đã bị xóa).</summary>
        AccountMissing,

        /// <summary>Đã ghi cookie vào tài khoản.</summary>
        Saved
    }

    /// <summary>
    /// Ghi chuỗi cookie JSON đã bắt được vào ĐÚNG tài khoản <paramref name="targetId"/>. KHÔNG đọc lại
    /// <c>_editingId</c> nên không bị ảnh hưởng khi người dùng đổi chọn/tạo mới trong lúc chờ browser
    /// (chống race ghi nhầm/crash). Tách khỏi Playwright để test được ở mức ViewModel.
    /// </summary>
    /// <remarks>
    /// Luôn dựng lại danh sách (<see cref="RefreshList"/>) để instance trong <see cref="Accounts"/> có
    /// cookie mới — tránh mất cookie khi người dùng chọn lại tài khoản (instance cũ có Cookie rỗng rồi
    /// bị Save ghi đè về null). Chỉ cập nhật FORM và kéo lựa chọn về targetId khi người dùng VẪN đang
    /// mở đúng tài khoản đó; nếu đã chuyển đi thì vẫn lưu DB cho targetId nhưng giữ nguyên form/lựa chọn.
    /// </remarks>
    public SaveCookieResult SaveCapturedCookie(long targetId, string cookieJson)
    {
        if (CookieJson.Deserialize(cookieJson).Count == 0)
        {
            return SaveCookieResult.NoCookie;
        }

        var acc = _services.Accounts.GetById(targetId);
        if (acc is null)
        {
            return SaveCookieResult.AccountMissing;
        }

        acc.Cookie = cookieJson;
        _services.Accounts.Update(acc);

        // Làm mới cache trước khi dựng lại danh sách.
        _all = _services.Accounts.GetAll();

        if (_editingId == targetId)
        {
            // Người dùng vẫn đang mở tài khoản này → cập nhật form + chọn lại instance mới có cookie.
            EditCookie = cookieJson;
            UpdatedAtText = FormatDate(acc.UpdatedAt);
            RefreshList(targetId);
        }
        else
        {
            // Đã chuyển sang tài khoản khác / đang tạo mới → dựng lại danh sách (để instance của
            // targetId có cookie) nhưng giữ nguyên lựa chọn & form hiện tại.
            RefreshList(_editingId ?? SelectedRow?.Id);
        }

        return SaveCookieResult.Saved;
    }

    private void LoadIntoForm(Account a)
    {
        _editingId = a.Id;
        EditEmail = a.Email;
        EditPassword = a.Password;
        EditPhone = a.Phone ?? string.Empty;
        EditCookie = a.Cookie ?? string.Empty;
        EditNote = a.Note ?? string.Empty;
        EditProxyKey = a.ProxyKey ?? string.Empty;
        // Giá trị lạ/null (bản ghi cũ hoặc ngoài danh sách) → về mặc định, tránh ComboBox trống.
        EditPickupAddress = PickupAddressOptions.Contains(a.PickupAddress ?? "")
            ? a.PickupAddress!
            : DefaultPickupAddress;
        EditStatus = a.Status;
        CreatedAtText = FormatDate(a.CreatedAt);
        UpdatedAtText = FormatDate(a.UpdatedAt);
        ErrorMessage = null;
        ShowPassword = false;
        UpdateSelectedSessionStatus();
    }

    private void ClearForm()
    {
        _editingId = null;
        EditEmail = string.Empty;
        EditPassword = string.Empty;
        EditPhone = string.Empty;
        EditCookie = string.Empty;
        EditNote = string.Empty;
        EditProxyKey = string.Empty;
        EditPickupAddress = DefaultPickupAddress;
        EditStatus = AccountStatus.ChuaKiemTra;
        CreatedAtText = null;
        UpdatedAtText = null;
        ErrorMessage = null;
        ShowPassword = false;
        UpdateSelectedSessionStatus();
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FormatDate(DateTime utc)
        => utc == default ? string.Empty : utc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
}
