using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.App.ViewModels;

/// <summary>Một mục điều hướng trên sidebar (nhãn + icon).</summary>
public record NavItem(string Label, string Icon);

/// <summary>
/// ViewModel cửa sổ chính: điều hướng giữa các màn hình Tài khoản / Đơn hàng / Thống kê.
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly AccountsViewModel _accountsVm;
    private readonly OrdersViewModel _ordersVm;
    private readonly OrderStatisticsViewModel _statisticsVm;
    private readonly SettingsViewModel _settingsVm;

    public MainViewModel(AppServices services)
    {
        _services = services;
        _accountsVm = new AccountsViewModel(services);
        _ordersVm = new OrdersViewModel(services);
        _statisticsVm = new OrderStatisticsViewModel(services);
        _settingsVm = new SettingsViewModel(services);
        _currentViewModel = _accountsVm;

        // Kho đơn đổi (phiên sync ghi xong, CÓ THỂ từ thread nền) → cập nhật số đơn ở thanh trạng thái.
        // Marshal về UI thread vì các property bind chỉ được đụng trên UI thread. VM sống suốt vòng đời app.
        _services.OrdersChanged += () => UiDispatch.Post(RefreshStatus);
        // Vòng chờ đẩy quét xong một lượt (thread nền của worker) → cập nhật đoạn "⏳ Chờ đẩy".
        _services.PendingOutboxChanged += () => UiDispatch.Post(RefreshOutboxPending);
        RefreshStatus();
        RefreshOutboxPending();
    }

    // ── 3 màn con + màn Cài đặt (read-only) để shell suite ráp lên dải Ribbon. Màn Cài đặt của đơn hàng
    //    KHÔNG còn trong NavItems (đã dời sang tab Cài đặt chung), nhưng VM vẫn sống để tab đó dùng. ──
    /// <summary>Màn "Tài khoản" (module đơn hàng).</summary>
    public AccountsViewModel AccountsVm => _accountsVm;
    /// <summary>Màn "Đơn hàng".</summary>
    public OrdersViewModel OrdersVm => _ordersVm;
    /// <summary>Màn "Thống kê" tổng hợp ảnh chụp kho đơn trên máy.</summary>
    public OrderStatisticsViewModel StatisticsVm => _statisticsVm;
    /// <summary>Màn "Cài đặt" của đơn hàng — nhúng vào màn Cài đặt GỘP của suite.</summary>
    public SettingsViewModel SettingsVm => _settingsVm;

    /// <summary>Các màn con của module đơn hàng (đã LÊN dải Ribbon; bỏ "Cài đặt" — dời sang tab Cài đặt chung).</summary>
    public ObservableCollection<NavItem> NavItems { get; } = new()
    {
        new NavItem("Tài khoản", "◵"),
        new NavItem("Đơn hàng", "▤"),
        new NavItem("Thống kê", "▥")
    };

    [ObservableProperty]
    private int _selectedNavIndex;

    [ObservableProperty]
    private ViewModelBase _currentViewModel;

    // ===== Thanh trạng thái đáy (số tài khoản / đơn / trình duyệt) =====

    [ObservableProperty]
    private string _statusAccountsText = "";

    [ObservableProperty]
    private string _statusOrdersText = "";

    [ObservableProperty]
    private string _statusBrowserText = "";

    // ===== Đoạn "⏳ Chờ đẩy" của thanh trạng thái (vòng chờ đẩy — HubOutboxWorker) =====

    /// <summary>Còn hàng tồn chờ đẩy? Thanh trạng thái CHỈ hiện đoạn "⏳ Chờ đẩy" khi true.</summary>
    [ObservableProperty]
    private bool _hasOutboxPending;

    [ObservableProperty]
    private string _outboxPendingText = "";

    /// <summary>Tooltip tách theo từng loại đích (đơn / phiếu / dòng sheet / lượt đếm / mã trả hàng).</summary>
    [ObservableProperty]
    private string _outboxPendingTooltip = "";

    /// <summary>Đọc lại số tồn của vòng chờ đẩy. Gọi ở ctor và mỗi khi worker quét xong một lượt.</summary>
    public void RefreshOutboxPending()
    {
        var p = _services.PendingOutbox;
        HasOutboxPending = p.Tong > 0;
        OutboxPendingText = $"⏳ Chờ đẩy: {p.Tong}";
        OutboxPendingTooltip = MoTaTonTooltip(p);
    }

    /// <summary>
    /// HÀM THUẦN (test được) dựng tooltip của badge "⏳ Chờ đẩy": MỖI loại đích một DÒNG, và <b>bỏ hẳn dòng nào
    /// bằng 0</b> — badge chỉ hiện khi còn tồn, nên liệt kê cả mấy loại đã sạch chỉ làm loãng đúng con số đang kẹt.
    /// Mọi loại đều 0 (badge đang ẩn) → chỉ còn dòng nhắc nhịp đẩy lại.
    /// </summary>
    internal static string MoTaTonTooltip(OutboxPending p)
    {
        var dong = new List<string> { $"Hàng còn chờ đẩy: {p.Tong}" };
        if (p.Orders > 0) dong.Add($"• {p.Orders} đơn lên Hub");
        if (p.Slips > 0) dong.Add($"• {p.Slips} phiếu lên Hub");
        if (p.SheetRows > 0) dong.Add($"• {p.SheetRows} dòng Google Sheet");
        if (p.SoldCounts > 0) dong.Add($"• {p.SoldCounts} lượt đếm Đã bán");
        if (p.ReturnCodes > 0) dong.Add($"• {p.ReturnCodes} mã trả hàng");
        dong.Add("Tự đẩy lại mỗi 2 phút khi kết nối được — bấm để xem đơn kết thúc đang kẹt.");
        return string.Join("\n", dong);
    }

    /// <summary>
    /// Bấm badge "⏳ Chờ đẩy" → mở màn chẩn đoán "đơn kết thúc chưa dọn được" (H2.5). VM của màn được dựng MỚI
    /// mỗi lần mở (nó tự quét trong ctor) rồi bỏ đi khi đóng — không giữ trạng thái giữa hai lần mở.
    /// </summary>
    [RelayCommand]
    private void MoChanDoanDon() => DialogService.ShowChanDoanDon(new ChanDoanDonViewModel(_services));

    /// <summary>Đọc lại 3 số liệu cho thanh trạng thái đáy. Gọi ở ctor, khi đổi màn, và sau khi kho đơn đổi.
    /// <para>Đếm proxy đã BỎ cùng cụm proxy runtime (module đi thẳng IP máy).</para></summary>
    public void RefreshStatus()
    {
        StatusAccountsText = $"{_services.Accounts.GetAll().Count} tài khoản";
        StatusOrdersText = $"{_services.Orders.Count()} đơn hàng";
        StatusBrowserText = "Trình duyệt: " + BrowserChoices.VnLabel(_services.Settings.GetBrowserChoice());
    }

    partial void OnSelectedNavIndexChanged(int value)
    {
        // Màn Thống kê sống suốt vòng đời app: cho nó biết đang hiện hay đang ẩn để lúc ẩn thì bỏ qua việc quét kho
        // đơn + hỏi Hub mỗi lượt sync (case 2 bên dưới vẫn Reload nên mở lên là số tươi).
        _statisticsVm.DangHienTrenMan = value == 2;
        switch (value)
        {
            case 0:
                _accountsVm.Reload();
                CurrentViewModel = _accountsVm;
                break;
            case 1:
                _ordersVm.Reload();
                CurrentViewModel = _ordersVm;
                break;
            case 2:
                _statisticsVm.Reload();
                CurrentViewModel = _statisticsVm;
                break;
        }

        RefreshStatus();
    }
}
