using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.App.ViewModels;

/// <summary>Một mục điều hướng trên sidebar (nhãn + icon).</summary>
public record NavItem(string Label, string Icon);

/// <summary>
/// ViewModel cửa sổ chính: điều hướng giữa các màn hình Tài khoản / Đơn hàng / Proxy / Cài đặt.
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly AccountsViewModel _accountsVm;
    private readonly OrdersViewModel _ordersVm;
    private readonly ProxiesViewModel _proxiesVm;
    private readonly SettingsViewModel _settingsVm;

    public MainViewModel(AppServices services)
    {
        _services = services;
        _accountsVm = new AccountsViewModel(services);
        _ordersVm = new OrdersViewModel(services);
        _proxiesVm = new ProxiesViewModel(services);
        _settingsVm = new SettingsViewModel(services);
        _currentViewModel = _accountsVm;

        // Kho đơn đổi (phiên sync ghi xong, CÓ THỂ từ thread nền) → cập nhật số đơn ở thanh trạng thái.
        // Marshal về UI thread vì các property bind chỉ được đụng trên UI thread. VM sống suốt vòng đời app.
        _services.OrdersChanged += () => Dispatcher.UIThread.Post(RefreshStatus);
        RefreshStatus();
    }

    // ── 4 màn con + màn Cài đặt (read-only) để shell suite ráp lên dải Ribbon. Màn Cài đặt của đơn hàng
    //    KHÔNG còn trong NavItems (đã dời sang tab Cài đặt chung), nhưng VM vẫn sống để tab đó dùng. ──
    /// <summary>Màn "Tài khoản" (module đơn hàng).</summary>
    public AccountsViewModel AccountsVm => _accountsVm;
    /// <summary>Màn "Đơn hàng".</summary>
    public OrdersViewModel OrdersVm => _ordersVm;
    /// <summary>Màn "Proxy".</summary>
    public ProxiesViewModel ProxiesVm => _proxiesVm;
    /// <summary>Màn "Cài đặt" của đơn hàng — nhúng vào màn Cài đặt GỘP của suite.</summary>
    public SettingsViewModel SettingsVm => _settingsVm;

    /// <summary>Các màn con của module đơn hàng (đã LÊN dải Ribbon; bỏ "Cài đặt" — dời sang tab Cài đặt chung).</summary>
    public ObservableCollection<NavItem> NavItems { get; } = new()
    {
        new NavItem("Tài khoản", "◵"),
        new NavItem("Đơn hàng", "▤"),
        new NavItem("Proxy", "⇄")
    };

    [ObservableProperty]
    private int _selectedNavIndex;

    [ObservableProperty]
    private ViewModelBase _currentViewModel;

    // ===== Thanh trạng thái đáy (số tài khoản / đơn / proxy / trình duyệt) =====

    [ObservableProperty]
    private string _statusAccountsText = "";

    [ObservableProperty]
    private string _statusOrdersText = "";

    [ObservableProperty]
    private string _statusProxiesText = "";

    [ObservableProperty]
    private string _statusBrowserText = "";

    /// <summary>Đọc lại 4 số liệu cho thanh trạng thái đáy. Gọi ở ctor, khi đổi màn, và sau khi kho đơn đổi.</summary>
    public void RefreshStatus()
    {
        StatusAccountsText = $"{_services.Accounts.GetAll().Count} tài khoản";
        StatusOrdersText = $"{_services.Orders.Count()} đơn hàng";
        StatusProxiesText = $"{_services.Proxies.GetAll().Count} proxy";
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
                _proxiesVm.Reload();
                CurrentViewModel = _proxiesVm;
                break;
        }

        RefreshStatus();
    }
}
