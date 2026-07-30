using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
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
        _services.OrdersChanged += () => Dispatcher.UIThread.Post(RefreshStatus);
        // Vòng chờ đẩy quét xong một lượt (thread nền của worker) → cập nhật đoạn "⏳ Chờ đẩy".
        _services.PendingOutboxChanged += () => Dispatcher.UIThread.Post(RefreshOutboxPending);
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
        OutboxPendingTooltip =
            $"Hàng còn chờ đẩy: {p.Orders} đơn lên Hub · {p.Slips} phiếu · {p.SheetRows} dòng Google Sheet · "
            + $"{p.SoldCounts} lượt đếm Đã bán · {p.ReturnCodes} mã trả hàng.\nTự đẩy lại mỗi 2 phút khi kết nối được.";
    }

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
