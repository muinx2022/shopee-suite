using CommunityToolkit.Mvvm.ComponentModel;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.App.ViewModels;

/// <summary>
/// Bọc một <see cref="Account"/> để hiển thị trong danh sách: giữ nguyên dữ liệu tài khoản (passthrough
/// <see cref="Id"/>/<see cref="Email"/>/<see cref="Status"/> cho template avatar + chấm trạng thái cũ) và
/// thêm phần trạng thái ĐA PHIÊN của Plan B: tick chọn nhiều (<see cref="IsSelected"/>), chấm "đang chạy"
/// (<see cref="IsRunning"/>) và dòng "Chờ lấy: N" (<see cref="ToShipText"/>) đổ từ phiên.
/// <para>
/// ViewModel đổ trạng thái vào row bằng <see cref="SyncFromSession"/> trên UI thread (không bind trực tiếp
/// vào phiên để tránh phức tạp vòng đời) — nên phần này thuần và test được.
/// </para>
/// </summary>
public partial class AccountRowViewModel : ObservableObject
{
    public AccountRowViewModel(Account account)
    {
        Account = account;
    }

    /// <summary>Bản ghi tài khoản nguồn (chỗ nào cần <see cref="Account"/> thì đọc qua đây).</summary>
    public Account Account { get; }

    /// <summary>Id tài khoản — dùng để reselect theo Id và tra phiên tương ứng.</summary>
    public long Id => Account.Id;

    /// <summary>User đăng nhập — cho template (avatar chữ cái + dòng email).</summary>
    public string Email => Account.Email;

    /// <summary>Trạng thái tài khoản — cho chấm màu status cũ.</summary>
    public AccountStatus Status => Account.Status;

    /// <summary>Tick chọn nhiều (dùng cho "Chọn toàn bộ" / "Chạy đã chọn" / "Dừng đã chọn").</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Trạng thái phiên đang chạy của tài khoản này (null = không có phiên).</summary>
    [ObservableProperty]
    private SessionState? _runState;

    /// <summary>Dòng "Chờ lấy: N" (null/rỗng khi chưa có số hoặc không chạy).</summary>
    [ObservableProperty]
    private string? _toShipText;

    /// <summary>True khi phiên đang chuẩn bị/đang chạy — cho chấm xanh "đang chạy" trên dòng.</summary>
    public bool IsRunning => RunState is SessionState.Opening or SessionState.Running;

    partial void OnRunStateChanged(SessionState? value) => OnPropertyChanged(nameof(IsRunning));

    /// <summary>
    /// Đổ trạng thái từ phiên (hoặc null nếu tài khoản không có phiên) vào row. Chỉ hiện "Chờ lấy: N" khi
    /// phiên đang chạy và đã đọc được số đơn; ngược lại để null (ẩn dòng).
    /// </summary>
    public void SyncFromSession(IAccountSession? s)
    {
        RunState = s?.State;
        ToShipText = s is { State: SessionState.Running or SessionState.Opening } && s.ToShipCount is int n
            ? $"Chờ lấy: {n}"
            : null;
    }
}
