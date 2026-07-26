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

    /// <summary>Tab "Kết quả" (cột tiến độ): shop mà phiên của TỪNG tài khoản đang/vừa check — nhãn shop + có
    /// đang check hay không. Nhớ theo tài khoản (không phải một ô duy nhất) để chuyển qua lại giữa nhiều tài
    /// khoản đang chạy vẫn thấy đúng chấm của tài khoản đang mở. Chỉ ghi/đọc trên UI thread (xem
    /// <see cref="OnShopCheckChanged"/> đã marshal qua <see cref="RunOnUi"/>) nên không cần khóa.</summary>
    private readonly Dictionary<long, (string ShopLabel, bool IsChecking)> _shopCheck = new();

    /// <summary>Tab "Kết quả" (cột tiến độ): NHÃN các shop đã kiểm tra XONG trong LƯỢT CHẠY hiện tại của từng tài
    /// khoản — nguồn của dấu tick. Trạng thái của lượt chạy nên chỉ sống trong bộ nhớ (KHÔNG lưu DB) và bị xóa
    /// sạch khi lượt mới bắt đầu. So khớp nhãn không phân biệt hoa/thường. Chỉ ghi/đọc trên UI thread (xem
    /// <see cref="OnShopCheckChanged"/>/<see cref="OnShopListChanged"/> đã marshal qua <see cref="RunOnUi"/>) nên
    /// không cần khóa.</summary>
    private readonly Dictionary<long, HashSet<string>> _shopDaCheck = new();

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

        // Panel log dùng TextBox chỉ-đọc bind LogText → cập nhật chuỗi mỗi khi FilteredLogEntries đổi (để copy được).
        FilteredLogEntries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(LogText));

        // Có nguồn NGOÀI màn này thêm tài khoản (sync shop từ BigSeller Insert dòng mới) → nghe để tự nạp lại
        // danh sách, thấy shop mới ngay không cần đổi màn. Sự kiện có thể đến từ thread nền → marshal về UI
        // thread (RunOnUi) trước khi đụng ObservableCollection.
        _services.AccountsChanged += OnAccountsChanged;

        // Vừa chuẩn bị xong 1 đơn → số ở tab "Kết quả" của tài khoản đó vừa tăng trong CSDL. Nghe để nạp lại
        // NGAY (chỉ khi đúng tài khoản đang mở), thay vì bắt người dùng đổi tài khoản/đổi ngày mới thấy số mới.
        _services.PrepareCountChanged += OnPrepareCountChanged;

        // Phiên vừa đọc được danh sách shop → dựng lưới tab "Kết quả" NGAY. Không có cái này thì mở app + chọn
        // tài khoản TRƯỚC khi phiên đọc shop sẽ thấy lưới TRỐNG mãi (chỉ hết khi bấm sang tk khác rồi bấm lại).
        _services.ShopListChanged += OnShopListChanged;

        // Phiên vào/ra một shop → cột tiến độ của tab "Kết quả" chuyển chấm + bật/tắt vòng quay. Cũng từ thread
        // nền của phiên → marshal về UI thread (RunOnUi) trước khi đụng ResultRows.
        _services.ShopCheckChanged += OnShopCheckChanged;

        // Nạp cờ "Xóa profile và tạo lại" từ Settings (bền qua restart). Setter tự LƯU nên chặn ghi ngược
        // trong lúc nạp bằng _loadingSettings.
        _loadingSettings = true;
        XoaProfileTaoLai = _services.Settings.GetSyncFreshProfile();
        TuDongXacNhan = _services.Settings.GetAutoConfirmEmail();
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

    /// <summary>Toàn bộ nhật ký đang hiển thị dưới dạng MỘT chuỗi (mỗi dòng một Display) — để panel log dùng TextBox
    /// chỉ-đọc (bôi đen + Ctrl+C / chuột phải Copy được, khác ListBox không chọn text được). Cập nhật mỗi khi
    /// <see cref="FilteredLogEntries"/> đổi (đăng ký ở ctor).</summary>
    public string LogText => string.Join("\n", FilteredLogEntries.Select(e => e.Display));

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

    /// <summary>
    /// "Truy cập TK" (nút trên dòng TK chưa xác nhận): CHỌN tài khoản đó (đổ Chi tiết + nhật ký) rồi TỰ MỞ phiên
    /// trình duyệt để người dùng xác minh tay trên cửa sổ Brave. Phiên đang chạy sẵn → chỉ chọn + báo (KHÔNG mở
    /// trùng). Mở phiên bằng ĐÚNG đường sẵn có <see cref="AccountSessionManager.Start"/> (idempotent) — không chế
    /// đường mở mới. Gọi từ code-behind (giống <see cref="ToggleRowTick"/>).
    /// </summary>
    public void TruyCapTk(AccountRowViewModel row)
    {
        // Chọn dòng: OnSelectedRowChanged tự nạp form + đưa cửa sổ Brave (nếu có) ra trước.
        SelectedRow = row;

        var id = row.Id;
        var email = row.Email;
        if (_services.Sessions.IsRunning(id))
        {
            const string msg = "Phiên đang mở — xác minh trên cửa sổ Brave của tài khoản này.";
            _services.Log.Append(email, msg);
            BusyStatus = msg;
            return;
        }

        _services.Log.Append(email, "Truy cập TK: mở trang bán hàng để xác minh tay...");
        _services.Sessions.Start(id);
        UpdateSelectedSessionStatus();
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

    /// <summary>Email xác minh (hộp thư Hotmail/Outlook nhận mail xác minh Shopee — để trống = không dùng).</summary>
    [ObservableProperty]
    private string _editVerifyEmail = string.Empty;

    /// <summary>Mật khẩu hộp thư email xác minh (để trống = không dùng).</summary>
    [ObservableProperty]
    private string _editVerifyEmailPassword = string.Empty;

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

    /// <summary>Hiện/ẩn mật khẩu email xác minh (nút 👁 riêng của card "EMAIL XÁC MINH").</summary>
    [ObservableProperty]
    private bool _showVerifyEmailPassword;

    /// <summary>Dòng hướng dẫn/trạng thái hiển thị (đổ từ phiên của tài khoản đang chọn; null = ẩn).</summary>
    [ObservableProperty]
    private string? _busyStatus;

    /// <summary>Trạng thái theo dõi đơn "Chờ Lấy Hàng" (đổ từ phiên của tài khoản đang chọn; null = ẩn).</summary>
    [ObservableProperty]
    private string? _orderStatus;

    /// <summary>Tab "Kết quả": NGÀY đang lọc (mặc định hôm nay). Đổi ngày → <see cref="LoadResults"/> nạp lại số
    /// chuẩn bị hàng của ngày đó. DatePicker trả <c>DateTimeOffset?</c> — dùng kiểu KHÔNG null nên xóa trắng lịch
    /// KHÔNG ghi được về đây (giữ giá trị cũ).</summary>
    [ObservableProperty]
    private DateTimeOffset _resultDate = DateTimeOffset.Now;

    /// <summary>Tab "Kết quả": các dòng Shop | số Chuẩn bị hàng của NGÀY đang lọc — MỌI shop của tài khoản (kể cả
    /// shop 0 đơn). Dựng lại trong <see cref="LoadResults"/> khi đổi tài khoản chọn / đổi ngày.</summary>
    public ObservableCollection<ShopPrepareRow> ResultRows { get; } = new();

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
    /// Cho nút "Chạy" khi đang xem/sửa một tài khoản ĐÃ LƯU (có Id) — KHÔNG phụ thuộc phiên đang chạy. Bấm =
    /// MỞ PHIÊN (đăng nhập subaccount rồi tự lặp qua các shop). Tài khoản mới chưa lưu (IsNew) → tắt nút.
    /// </summary>
    public bool CanRun => IsEditing && !IsNew && _editingId is not null;

    /// <summary>Nguồn SÁNG/TẮT của nút 🗑: bật khi đang bôi đậm một dòng (<see cref="SelectedRow"/> — xóa dòng
    /// đó, hành vi cũ) HOẶC có ≥1 dòng đang hiển thị được tick (xóa hàng loạt theo tick). Notify lại tại mọi
    /// điểm tick/lựa chọn đổi (<see cref="ToggleRowTick"/>, <see cref="SelectAll"/>, <see cref="Reload"/>,
    /// <see cref="OnSelectedRowChanged"/>) để nút cập nhật kịp.</summary>
    public bool CanDelete => SelectedRow is not null || Accounts.Any(r => r.IsSelected);

    /// <summary>Id của tài khoản đang được nạp trong form (null = form trống / tạo mới).</summary>
    private long? _editingId;

    partial void OnIsEditingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowPlaceholder));
        OnPropertyChanged(nameof(CanStopSeller));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanRun));
    }

    partial void OnIsNewChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStopSeller));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanRun));
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
        }
        else if (!IsNew)
        {
            IsEditing = false;
            ClearForm();
        }

        // Tab "Kết quả": nạp lưới Shop|Chuẩn bị hàng theo tài khoản vừa chọn (bỏ chọn → clear). Đặt SAU khi
        // form/_editingId đã đồng bộ (dùng SelectedRow.Id).
        LoadResults();
    }

    /// <summary>Đổi NGÀY lọc ở tab "Kết quả" → nạp lại số chuẩn bị hàng của ngày mới.</summary>
    partial void OnResultDateChanged(DateTimeOffset value) => LoadResults();

    /// <summary>
    /// Dựng lại <see cref="ResultRows"/> cho tab "Kết quả": MỌI shop của tài khoản đang chọn (từ
    /// <c>account_shops</c>) LEFT JOIN số đơn chuẩn bị hàng của <see cref="ResultDate"/> (từ <c>prepare_daily</c>) —
    /// shop không có đơn trong ngày → 0. Shop CÓ đơn trong ngày nhưng CHƯA có trong danh sách shop (lỡ đọc shop-list)
    /// → vẫn thêm để không sót. Chưa chọn tài khoản → lưới rỗng. Chạy trên UI thread (gọi từ setter/OnSelectedRowChanged).
    /// </summary>
    private void LoadResults()
    {
        ResultRows.Clear();
        if (SelectedRow?.Id is not long accountId)
        {
            return;
        }

        var day = ResultDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var shops = _services.Results.GetShops(accountId);
        var counts = _services.Results.GetPreparedByDay(accountId, day);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (shopLogin, shopName) in shops)
        {
            seen.Add(shopLogin);
            var name = string.IsNullOrWhiteSpace(shopName) ? shopLogin : shopName;
            ResultRows.Add(new ShopPrepareRow(name, shopLogin, counts.GetValueOrDefault(shopLogin, 0)));
        }

        // Shop có đơn nhưng chưa nằm trong account_shops → UNION thêm (nhãn = chính shop_login) để không sót đếm.
        foreach (var kv in counts)
        {
            if (!seen.Contains(kv.Key))
            {
                ResultRows.Add(new ShopPrepareRow(kv.Key, kv.Key, kv.Value));
            }
        }

        // Dòng vừa dựng lại là dòng MỚI (cờ tiến độ về mặc định) → áp lại tick/vòng quay. Bắt buộc: hàm này chạy
        // sau MỖI đơn chuẩn bị xong (PrepareCountChanged), thiếu bước này là tick nhấp nháy/biến mất khi đang chạy.
        ApplyShopCheckFlags();
    }

    /// <summary>
    /// Áp cờ cột tiến độ (<see cref="ShopPrepareRow.IsChecking"/>/<see cref="ShopPrepareRow.DaKiemTra"/>) lên các
    /// dòng đang hiển thị theo shop mà phiên của TÀI KHOẢN ĐANG MỞ đang check + tập shop đã check xong lượt này.
    /// Không có tài khoản đang mở / tài khoản đó chưa chạy shop nào → xóa sạch cờ. Chạy trên UI thread.
    /// </summary>
    private void ApplyShopCheckFlags()
    {
        (string ShopLabel, bool IsChecking) state = default;
        HashSet<string>? daCheck = null;
        var hasState = false;
        if (SelectedRow?.Id is long accountId)
        {
            hasState = _shopCheck.TryGetValue(accountId, out state);
            _shopDaCheck.TryGetValue(accountId, out daCheck);
        }

        foreach (var row in ResultRows)
        {
            var current = hasState && MatchesShopLabel(row, state.ShopLabel);
            row.IsChecking = current && state.IsChecking;
            row.DaKiemTra = DaKiemTraShop(daCheck, row);
        }
    }

    /// <summary>
    /// Dòng lưới có nằm trong tập shop ĐÃ kiểm tra xong của lượt chạy (<paramref name="daCheck"/>) không. Phải
    /// DUYỆT tập rồi khớp bằng <see cref="MatchesShopLabel"/> (không tra khóa trực tiếp): nhãn phiên gửi về có thể
    /// lệch hoa/thường - khoảng trắng so với <c>account_shops</c>, và dòng lưới còn khớp được qua tên hiển thị.
    /// Tài khoản chưa có tập nào → false cho mọi dòng.
    /// </summary>
    private static bool DaKiemTraShop(HashSet<string>? daCheck, ShopPrepareRow row)
    {
        if (daCheck is null)
        {
            return false;
        }

        foreach (var label in daCheck)
        {
            if (MatchesShopLabel(row, label))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Dòng lưới có ứng với nhãn shop <paramref name="label"/> phiên báo về không. Nhãn phiên gửi là KHÓA shop
    /// (<c>LoginName</c>, rỗng thì <c>ShopName</c>) nên khớp <see cref="ShopPrepareRow.ShopLogin"/> là chính; vẫn
    /// nhận cả <see cref="ShopPrepareRow.ShopName"/> phòng dữ liệu cũ lưu lệch. So sánh bỏ khoảng trắng thừa +
    /// KHÔNG phân biệt hoa/thường (nhãn từ phiên và tên trong <c>account_shops</c> có thể lệch hoa/thường).
    /// </summary>
    private static bool MatchesShopLabel(ShopPrepareRow row, string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return false;
        }

        return Same(row.ShopLogin, label) || Same(row.ShopName, label);

        static bool Same(string? a, string? b)
            => !string.IsNullOrWhiteSpace(a)
               && string.Equals(a.Trim(), b!.Trim(), StringComparison.OrdinalIgnoreCase);
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
                VerifyEmail = EditVerifyEmail?.Trim() ?? "",
                VerifyEmailPassword = EditVerifyEmailPassword ?? "",
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
            existing.VerifyEmail = EditVerifyEmail?.Trim() ?? "";
            existing.VerifyEmailPassword = EditVerifyEmailPassword ?? "";
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

    [RelayCommand]
    private void ToggleShowPassword() => ShowPassword = !ShowPassword;

    [RelayCommand]
    private void ToggleShowVerifyEmailPassword() => ShowVerifyEmailPassword = !ShowVerifyEmailPassword;

    /// <summary>Dừng phiên của tài khoản đang chọn (đóng &amp; kill Brave của phiên đó, không ảnh hưởng phiên khác).
    /// Chạy được CẢ khi chỉ có phiên "chạy thử bridge" (không có phiên production): vẫn hủy + đóng trình duyệt.</summary>
    [RelayCommand]
    private void Stop()
    {
        var wasBridge = _bridgeRunning;

        // Hủy lát cắt bridge (cancel _bridgeCts → đóng cả trình duyệt điều khiển Playwright lẫn trình duyệt sạch)
        // + đóng cửa sổ POC. Chạy KỂ CẢ khi _editingId null (chỉ có bridge, chưa mở phiên production nào).
        TryKillPoc();

        if (_editingId is long accountId)
        {
            _services.Sessions.Stop(accountId);
            UpdateSelectedSessionStatus();
        }

        if (wasBridge)
        {
            var email = (_editingId is long id ? _services.Accounts.GetById(id)?.Email : null) ?? EditEmail;
            _services.Log.Append(email, "Đã dừng chạy thử + đóng trình duyệt.");
        }
    }

    /// <summary>
    /// "Chạy" — nút hành động chính màn Tài khoản (mô hình 1 subaccount = nhiều shop): MỞ PHIÊN cho tài khoản
    /// đang xem (khởi động Brave → đăng nhập subaccount → tự lặp qua các shop). Idempotent qua
    /// <see cref="AccountSessionManager.Start"/> (đang chạy thì thôi, không mở trùng). Vòng lặp shop tự chạy
    /// trong RunAsync sau đăng nhập nên KHÔNG gọi <c>SyncFullAsync</c> thủ công (tránh giẫm vòng lặp). Phiên
    /// đang chạy vòng lặp shop → chỉ log rồi thôi.
    /// </summary>
    [RelayCommand]
    private void Run()
    {
        // Chụp accountId + email — bám theo tài khoản đang mở trên form.
        if (_editingId is not long accountId)
        {
            return;
        }

        var email = _services.Accounts.GetById(accountId)?.Email ?? EditEmail;

        // Phiên đang chạy vòng lặp shop → không mở lại (Start vốn idempotent, nhưng báo cho rõ rồi thôi).
        if (_services.Sessions.Get(accountId) is { IsShopLoopRunning: true })
        {
            _services.Log.Append(email, "Đang chạy rồi.");
            return;
        }

        TryKillPoc(); // đóng cửa sổ POC "mở sạch" (nếu còn) trước khi phiên production launch — tránh khoá hồ sơ chung.
        _services.Log.Append(email, "Chạy: mở phiên — đăng nhập rồi tự lặp qua các shop...");
        _services.Sessions.Start(accountId); // mở phiên; vòng lặp shop tự chạy trong RunAsync
        UpdateSelectedSessionStatus();
    }

    /// <summary>Tiến trình trình duyệt SẠCH (không CDP) đang mở cho tài khoản đang chọn; null = không có.</summary>
    private System.Diagnostics.Process? _pocProcess;

    /// <summary>Phiên cầu nối GĐ1 đang chạy (WebSocket ↔ extension) cho tài khoản đang chọn; null = không có.</summary>
    private OrdersBridgeSession? _bridgeSession;

    /// <summary>Nguồn huỷ cho lát cắt cầu nối đang chạy (■ Dừng → cancel).</summary>
    private System.Threading.CancellationTokenSource? _bridgeCts;

    /// <summary>Đang chạy lát cắt cầu nối → chặn bấm lại (tránh mở trùng phiên/khoá hồ sơ chung).</summary>
    private bool _bridgeRunning;

    /// <summary>
    /// "🧪 Chạy thử (đăng nhập + shop)" — GĐ2 CẦU NỐI extension↔C#: mở trình duyệt SẠCH (KHÔNG Playwright/CDP,
    /// KHÔNG remote-debugging-port, KHÔNG proxy) với ĐÚNG hồ sơ persistent của tài khoản đang xem → mở
    /// <c>subaccount.shopee.com</c> kèm hash <c>#_od_ws=&lt;port&gt;</c>; extension nối WebSocket rồi: tự điền form
    /// đăng nhập subaccount → CHỜ user nhập mã (mở hộp thư Playwright riêng cho user tự đọc mã) → SSO sang
    /// "Kênh Người bán" → <c>/portal/shop</c> → chạy lát cắt: đọc shop → mở "Chi tiết" shop đầu bằng trusted click
    /// (kỳ vọng KHÔNG captcha) → đọc số "Chờ Lấy Hàng". Kết quả đổ ra panel log.
    /// Gate như CanRun (đang xem 1 acc đã lưu). Phiên production của acc đang chạy → từ chối (đụng khoá hồ sơ chung).
    /// </summary>
    [RelayCommand]
    private async Task ChayThuBridge()
    {
        if (_editingId is not long accountId)
        {
            return;
        }

        var acc = _services.Accounts.GetById(accountId);
        var email = acc?.Email ?? EditEmail;

        // Lát cắt cũ còn chạy → không mở chồng (một phiên/lần test).
        if (_bridgeRunning)
        {
            _services.Log.Append(email, "Đang chạy thử (bridge) rồi — đợi xong hoặc bấm ■ Dừng.");
            return;
        }

        // Đang có phiên production (Playwright) trên hồ sơ này → không mở (Chromium chỉ cho 1 tiến trình/hồ sơ).
        if (_services.Sessions.IsRunning(accountId))
        {
            const string msg = "Đang có phiên chạy — bấm ■ Dừng trước khi Chạy thử (bridge).";
            _services.Log.Append(email, msg);
            BusyStatus = msg;
            return;
        }

        _bridgeRunning = true;
        OnPropertyChanged(nameof(CanStop)); // bật nút ■ Dừng trong lúc chạy thử bridge
        try
        {
            TryKillPoc(); // đóng cửa sổ/phiên cũ (nếu còn) trước khi mở mới — tránh khoá hồ sơ

            // Công thức hồ sơ Y HỆT AccountSession: baseDir = thư mục Database.Path; kind theo browserChoice ở Cài đặt.
            var baseDir = System.IO.Path.GetDirectoryName(_services.Database.Path) ?? ".";
            var browserChoice = _services.Settings.GetBrowserChoice();
            var browserKind = BrowserLocator.ResolveBrowserKind(browserChoice);
            var userDataDir = BrowserProfilePaths.ForAccount(baseDir, accountId, browserKind);

            // GĐ3: thư mục lưu phiếu (Cài đặt) + tỉnh địa chỉ lấy hàng (theo account, mặc định trong session).
            var invoiceDir = _services.Settings.GetInvoiceFolder();
            var province = acc?.PickupAddress;

            _bridgeCts = new System.Threading.CancellationTokenSource();
            var session = new OrdersBridgeSession(userDataDir, browserChoice,
                m => _services.Log.Append(email, m), invoiceDir, province);
            _bridgeSession = session;

            _services.Log.Append(email,
                "Chạy thử (đăng nhập + shop): đăng nhập subaccount bằng trình duyệt điều khiển → chờ bạn nhập mã → đóng → mở lại sạch + extension → đọc shop.");
            BusyStatus = "Đang chạy thử (đăng nhập + shop)...";

            var login = new OrdersLoginParams(
                acc?.Email ?? EditEmail,
                acc?.Password ?? string.Empty,
                acc?.VerifyEmail,
                acc?.VerifyEmailPassword);

            OrdersBridgeSliceResult result;
            try
            {
                result = await session.RunLoginThenSliceAsync(login, _bridgeCts.Token);
            }
            finally
            {
                _pocProcess = session.Process; // để TryKillPoc / ■ Dừng đóng được cửa sổ
            }

            if (result.Captcha)
            {
                var m = "Chạy thử (bridge): PHÁT HIỆN captcha/verify — kiến trúc CHƯA né được, cần soi lại.";
                _services.Log.Append(email, m);
                BusyStatus = m;
            }
            else if (result.Error is not null)
            {
                _services.Log.Append(email, "Chạy thử (bridge) chưa xong: " + result.Error);
                BusyStatus = "Chạy thử (bridge): " + result.Error;
            }
            else
            {
                var line =
                    $"Chạy thử (bridge) OK: {result.Shops.Count} shop; shop đầu id={result.FirstShopId}; " +
                    $"Chờ Lấy Hàng={result.ToShipCount?.ToString() ?? "?"}; đọc {result.OrdersCount} đơn" +
                    (result.SlipsSaved > 0 ? $"; lưu {result.SlipsSaved} phiếu" : string.Empty) +
                    " — KHÔNG captcha.";
                _services.Log.Append(email, line);
                BusyStatus = line;
            }
        }
        catch (System.OperationCanceledException)
        {
            _services.Log.Append(email, "Đã hủy chạy thử (bridge).");
            BusyStatus = "Đã hủy chạy thử (bridge).";
        }
        catch (System.Exception ex)
        {
            _services.Log.Append(email, "Lỗi chạy thử (bridge): " + ex.Message);
            BusyStatus = "Lỗi chạy thử (bridge): " + ex.Message;
        }
        finally
        {
            _bridgeRunning = false;
            OnPropertyChanged(nameof(CanStop)); // tắt lại nút ■ Dừng nếu không còn phiên nào
            try { _bridgeSession?.Dispose(); } catch { /* bỏ qua */ }
            _bridgeSession = null;
            try { _bridgeCts?.Dispose(); } catch { /* bỏ qua */ }
            _bridgeCts = null;
        }
    }

    /// <summary>Nút "■ Dừng" bật khi có phiên production đang chạy HOẶC đang chạy thử bridge (để hủy được cả hai).</summary>
    public bool CanStop => CanStopSeller || _bridgeRunning;

    /// <summary>Kill tiến trình trình duyệt sạch + huỷ lát cắt cầu nối đang mở (nếu có) — giải phóng khoá hồ sơ
    /// dùng chung với phiên production.</summary>
    private void TryKillPoc()
    {
        try { _bridgeCts?.Cancel(); } catch { /* bỏ qua */ }

        // Trong lúc lát cắt đang chạy, tiến trình nằm ở _bridgeSession.Process (_pocProcess chỉ set sau khi xong).
        var bridgeProc = _bridgeSession?.Process;
        try { if (bridgeProc is { HasExited: false }) bridgeProc.Kill(entireProcessTree: true); }
        catch { /* bỏ qua */ }

        try { if (_pocProcess is { HasExited: false }) _pocProcess.Kill(entireProcessTree: true); }
        catch { /* bỏ qua */ }
        _pocProcess = null;
    }

    /// <summary>Nhãn nguồn log cho các thông báo cấp-BATCH (không thuộc một shop cụ thể) — ghi file &amp; phân
    /// biệt với log per-account (per-account dùng email của shop).</summary>
    private const string BatchLogSource = "Hàng loạt";

    /// <summary>
    /// "Chạy đã chọn" (HÀNG LOẠT) — với MỌI tài khoản đang tick: MỞ PHIÊN (<see cref="AccountSessionManager.Start"/>,
    /// idempotent). Mỗi phiên tự đăng nhập subaccount rồi lặp qua các shop của nó (RunAsync) nên KHÔNG chạy hành
    /// động thủ công (Sync/Kiểm tra) — vòng lặp shop tự làm. Chụp danh sách (id, email) các dòng tick MỘT LẦN —
    /// KHÔNG giữ tham chiếu <see cref="AccountRowViewModel"/>. Rỗng → log "Chưa tick tài khoản nào." rồi thôi;
    /// phiên đang chạy vòng lặp shop → bỏ qua (log "Đang chạy rồi.").
    /// </summary>
    [RelayCommand]
    private void RunSelected()
    {
        // Chụp (id, email) của các dòng ĐANG tick MỘT LẦN.
        var targets = Accounts
            .Where(r => r.IsSelected)
            .Select(r => (Id: r.Id, Email: r.Email))
            .ToList();

        if (targets.Count == 0)
        {
            _services.Log.Append(BatchLogSource, "Chưa tick tài khoản nào.");
            return;
        }

        foreach (var target in targets)
        {
            // Phiên đang chạy vòng lặp shop → không mở lại (Start idempotent, nhưng báo cho rõ).
            if (_services.Sessions.Get(target.Id) is { IsShopLoopRunning: true })
            {
                _services.Log.Append(target.Email, "Đang chạy rồi.");
                continue;
            }

            _services.Sessions.Start(target.Id); // mở phiên; vòng lặp shop tự chạy trong RunAsync
        }

        UpdateSelectedSessionStatus();
        _services.Log.Append(BatchLogSource, $"Đã mở phiên chạy cho {targets.Count} tài khoản đã chọn.");
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
    /// TẬP tài khoản đổi từ NGOÀI màn này (vd sync shop BigSeller Insert dòng mới) → marshal về UI thread rồi
    /// <see cref="Reload"/> để danh sách đón dòng mới ngay. <see cref="Reload"/> đã GIỮ lựa chọn/form/tick hiện
    /// tại (chọn lại theo <c>_editingId</c>/SelectedRow, khôi phục tick theo Id) nên ngữ nghĩa không đổi.
    /// </summary>
    private void OnAccountsChanged() => RunOnUi(Reload);

    /// <summary>
    /// Số "chuẩn bị hàng" của một tài khoản vừa tăng → nạp lại lưới tab "Kết quả" nếu ĐÚNG tài khoản đang mở.
    /// Tài khoản khác thì bỏ qua (đang chạy nhiều tk cùng lúc mà nạp hết là phí). Sự kiện đến từ THREAD NỀN của
    /// phiên → marshal về UI thread trước khi đụng <c>ResultRows</c>.
    /// <para>Chỉ nạp khi ngày đang lọc là HÔM NAY: số vừa cộng luôn thuộc hôm nay, người dùng đang xem ngày cũ
    /// thì lưới của họ không đổi — nạp lại chỉ tổ nháy màn.</para>
    /// </summary>
    /// <summary>
    /// Phiên vừa đọc được danh sách shop của một tài khoản → dựng lại lưới tab "Kết quả" nếu ĐÚNG tài khoản
    /// đang mở. KHÔNG lọc theo ngày (khác <see cref="OnPrepareCountChanged"/>): danh sách shop không phụ thuộc
    /// ngày, xem ngày cũ vẫn phải thấy đủ shop.
    /// <para>Cũng là mốc RESET dấu tick: đọc xong danh sách shop = phiên sắp lặp qua từng shop, tức LƯỢT CHẠY MỚI
    /// bắt đầu → xóa tập shop "đã kiểm tra" của tài khoản đó để lượt mới không kế thừa tick của lượt trước.</para>
    /// </summary>
    private void OnShopListChanged(long accountId) => RunOnUi(() =>
    {
        _shopDaCheck.Remove(accountId);

        if (SelectedRow is not null && SelectedRow.Id == accountId)
        {
            LoadResults();
        }
    });

    private void OnPrepareCountChanged(long accountId) => RunOnUi(() =>
    {
        if (SelectedRow is null || SelectedRow.Id != accountId)
        {
            return;
        }
        if (ResultDate.Date != DateTimeOffset.Now.Date)
        {
            return;
        }
        LoadResults();
    });

    /// <summary>
    /// Phiên vừa BẮT ĐẦU (<paramref name="checking"/> = true) hoặc XONG (false) việc check shop
    /// <paramref name="shopLabel"/> của tài khoản <paramref name="accountId"/> → cập nhật cột tiến độ tab "Kết quả".
    /// <list type="bullet">
    /// <item>bắt đầu → nhớ shop mới ⇒ vòng quay CHUYỂN sang shop đó ngay;</item>
    /// <item>xong → tắt vòng quay + ghi nhãn shop vào tập "đã kiểm tra" của lượt chạy ⇒ shop đó nhận dấu tick và
    /// GIỮ tick tới hết lượt (kể cả shop lỗi/captcha/bỏ qua — vẫn tính là đã kiểm tra qua).</item>
    /// </list>
    /// Nhớ cho MỌI tài khoản nhưng chỉ vẽ lại lưới khi đúng tài khoản đang mở. Sự kiện đến từ THREAD NỀN của
    /// phiên → marshal về UI thread trước khi đụng <c>ResultRows</c>.
    /// </summary>
    private void OnShopCheckChanged(long accountId, string shopLabel, bool checking) => RunOnUi(() =>
    {
        if (string.IsNullOrWhiteSpace(shopLabel))
        {
            return;
        }

        if (checking)
        {
            _shopCheck[accountId] = (shopLabel, true);
        }
        else if (_shopCheck.TryGetValue(accountId, out var prev))
        {
            // Xong shop: GIỮ nhãn đang nhớ, chỉ hạ cờ đang-check.
            _shopCheck[accountId] = (prev.ShopLabel, false);
        }
        else
        {
            _shopCheck[accountId] = (shopLabel, false);
        }

        if (!checking)
        {
            // Shop vừa xong → vào tập "đã kiểm tra" của lượt chạy (nguồn dấu tick), tạo tập nếu tài khoản chưa có.
            if (!_shopDaCheck.TryGetValue(accountId, out var daCheck))
            {
                daCheck = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _shopDaCheck[accountId] = daCheck;
            }
            daCheck.Add(shopLabel);
        }

        if (SelectedRow?.Id == accountId)
        {
            ApplyShopCheckFlags();
        }
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
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanRun));
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
        EditVerifyEmail = a.VerifyEmail ?? string.Empty;
        EditVerifyEmailPassword = a.VerifyEmailPassword ?? string.Empty;
        EditStatus = a.Status;
        CreatedAtText = FormatDate(a.CreatedAt);
        UpdatedAtText = FormatDate(a.UpdatedAt);
        ErrorMessage = null;
        ShowPassword = false;
        ShowVerifyEmailPassword = false;
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
        EditVerifyEmail = string.Empty;
        EditVerifyEmailPassword = string.Empty;
        EditStatus = AccountStatus.ChuaKiemTra;
        CreatedAtText = null;
        UpdatedAtText = null;
        ErrorMessage = null;
        ShowPassword = false;
        ShowVerifyEmailPassword = false;
        UpdateSelectedSessionStatus();
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FormatDate(DateTime utc)
        => utc == default ? string.Empty : utc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
}

/// <summary>
/// Một dòng lưới tab "Kết quả": tên Shop (hiển thị) + số đơn đã Chuẩn bị hàng của ngày đang lọc + cột tiến độ
/// (vòng quay khi đang check shop đó / dấu tick khi đã check xong shop đó trong lượt chạy).
/// <para>
/// Là LỚP quan sát được (không còn <c>record</c> bất biến) vì các cờ tiến độ đổi TẠI CHỖ trong lúc chạy — dựng
/// lại dòng mỗi lần đổi cờ sẽ làm lưới nháy. Kéo theo: hết value-equality, so sánh dòng là so THAM CHIẾU.
/// </para>
/// </summary>
public sealed partial class ShopPrepareRow : ObservableObject
{
    /// <param name="shopName">Tên hiển thị ở cột "Shop" (<c>account_shops.shop_name</c>, thiếu thì chính login).</param>
    /// <param name="shopLogin">KHÓA shop (<c>account_shops.shop_login</c> = khóa <c>prepare_daily</c>) — dùng để khớp
    /// nhãn shop mà phiên báo về; KHÁC <paramref name="shopName"/> khi shop có tên hiển thị riêng.</param>
    /// <param name="preparedCount">Số đơn đã Chuẩn bị hàng trong ngày đang lọc.</param>
    public ShopPrepareRow(string shopName, string shopLogin, int preparedCount)
    {
        ShopName = shopName;
        ShopLogin = shopLogin;
        PreparedCount = preparedCount;
    }

    /// <summary>Tên shop hiển thị ở cột "Shop" (không đổi trong đời dòng).</summary>
    public string ShopName { get; }

    /// <summary>Khóa shop dùng khớp với nhãn phiên báo về (không đổi trong đời dòng).</summary>
    public string ShopLogin { get; }

    /// <summary>Số đơn đã Chuẩn bị hàng của ngày đang lọc.</summary>
    [ObservableProperty]
    private int _preparedCount;

    /// <summary>Shop này đã được kiểm tra XONG trong lượt chạy hiện tại (kể cả lỗi/bỏ qua) — nguồn của dấu tick.
    /// Xóa sạch khi lượt chạy mới bắt đầu (phiên đọc lại danh sách shop).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTick))]
    private bool _daKiemTra;

    /// <summary>Đang check chính shop này (vòng quay + chữ "đang kiểm tra…" thay cho số).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTick))]
    private bool _isChecking;

    /// <summary>Hiện dấu TICK: đã kiểm tra xong shop này NHƯNG không còn quay (mỗi dòng chỉ một biểu tượng — đang
    /// quay thì vòng quay thế chỗ tick).</summary>
    public bool ShowTick => DaKiemTra && !IsChecking;
}
