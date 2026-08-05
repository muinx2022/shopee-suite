using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.App.ViewModels;

/// <summary>
/// <b>Form Chi tiết</b> của màn Tài khoản (panel phải): các ô nhập, danh sách lựa chọn cố định, và ba thao tác
/// ghi dữ liệu — Lưu / Hủy (khôi phục) / nạp-xóa form.
/// <para>Là phần <c>partial</c> của <see cref="AccountsViewModel"/> — property công khai vẫn nằm trên VM chính
/// (XAML bind thẳng <c>EditEmail</c>/<c>EditPassword</c>/… và <c>SaveCommand</c>/<c>CancelCommand</c>).</para>
/// </summary>
public partial class AccountsViewModel
{
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
    private bool _isEditing;

    [ObservableProperty]
    private bool _isNew;

    [ObservableProperty]
    private string _editEmail = string.Empty;

    [ObservableProperty]
    private string _editPassword = string.Empty;

    [ObservableProperty]
    private string _editCookie = string.Empty;

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

    /// <summary>Panel phải hiện chữ mờ khi không ở chế độ xem/sửa.</summary>
    public bool ShowPlaceholder => !IsEditing;

    /// <summary>True nếu tài khoản đang có cookie đăng nhập — dùng để hiện trạng thái gọn ("đã có/chưa có")
    /// thay cho ô hiển thị chuỗi cookie thô (đỡ dài form).</summary>
    public bool HasCookie => !string.IsNullOrWhiteSpace(EditCookie);

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

    partial void OnEditCookieChanged(string value) => OnPropertyChanged(nameof(HasCookie));

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
            // Phone/Note/ProxyKey KHÔNG có ô nhập trên form (đã bỏ) → tài khoản mới để null như mặc định model.
            account = new Account
            {
                Email = user,
                Password = EditPassword,
                Cookie = NullIfEmpty(EditCookie),
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

            // Phone/Note/ProxyKey KHÔNG có ô nhập trên form → KHÔNG gán lại, giữ NGUYÊN giá trị vừa đọc từ DB
            // (existing đến từ GetById). Gán qua form rỗng sẽ XOÁ dữ liệu người dùng đã có trong bảng.
            existing.Email = user;
            existing.Password = EditPassword;
            existing.Cookie = NullIfEmpty(EditCookie);
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

    [RelayCommand]
    private void ToggleShowPassword() => ShowPassword = !ShowPassword;

    [RelayCommand]
    private void ToggleShowVerifyEmailPassword() => ShowVerifyEmailPassword = !ShowVerifyEmailPassword;

    private void LoadIntoForm(Account a)
    {
        _editingId = a.Id;
        EditEmail = a.Email;
        EditPassword = a.Password;
        EditCookie = a.Cookie ?? string.Empty;
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
        EditCookie = string.Empty;
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
