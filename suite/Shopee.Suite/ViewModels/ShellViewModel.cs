using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Shopee.Suite.Modules.Accounts;
using Shopee.Suite.Modules.BigSeller;
using Shopee.Suite.Modules.CheckAccount;
using Shopee.Suite.Modules.Scrape;
using Shopee.Suite.Modules.Search;
using Shopee.Suite.Modules.Settings;
using Shopee.Suite.Modules.UpdateProduct;

namespace Shopee.Suite.ViewModels;

/// <summary>
/// ViewModel gốc của shell: danh sách module trên sidebar + module đang hiển thị. Mọi module
/// ViewModel được khởi tạo một lần và giữ sống suốt vòng đời app, nên phiên đang chạy của một
/// module không bị mất khi người dùng chuyển sang module khác rồi quay lại.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    public ObservableCollection<ModuleItem> Modules { get; }

    [ObservableProperty] private ModuleItem? _selected;

    private readonly WelcomeViewModel _welcome;

    /// <summary>ViewModel đang hiển thị: module được chọn, hoặc màn hình Welcome khi chưa chọn gì.</summary>
    public object Current => Selected?.ViewModel ?? _welcome;

    public ShellViewModel()
    {
        Modules =
        [
            new ModuleItem("Tài khoản & Proxy", "👤", "Kho tài khoản Shopee dùng chung",
                new AccountsViewModel()),
            new ModuleItem("BigSeller", "🗂", "Workbook + shop + cookie dùng chung",
                new BigSellerViewModel()),
            new ModuleItem("Shopee Scrape", "🧭", "Quản lý nhiều Brave + scrape",
                new ScrapeViewModel()),
            new ModuleItem("Shopee Search", "📊", "Thống kê tìm kiếm sản phẩm",
                new SearchViewModel()),
            new ModuleItem("Bigseller Update Product", "🏷", "Đổi tên sản phẩm hàng loạt",
                new UpdateProductViewModel()),
            new ModuleItem("Check Shopee Account", "🔐", "Kiểm tra tài khoản Shopee",
                new CheckAccountViewModel()),
            new ModuleItem("Cài đặt", "⚙", "AI provider / model / API key",
                new SettingsViewModel()),
        ];

        _welcome = new WelcomeViewModel(this);
        _selected = null; // mặc định: màn hình Welcome, không focus module nào
    }

    partial void OnSelectedChanged(ModuleItem? value) => OnPropertyChanged(nameof(Current));
}
