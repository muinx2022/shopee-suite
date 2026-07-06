using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Shopee.Suite.Infrastructure;
using Shopee.Suite.Modules.Accounts;
using Shopee.Suite.Modules.BigSeller;
using Shopee.Suite.Modules.CheckAccount;
using Shopee.Suite.Modules.Fleet;
using Shopee.Suite.Modules.Scrape;
using Shopee.Suite.Modules.Search;
using Shopee.Suite.Modules.Settings;
using Shopee.Suite.Modules.UpdateProduct;
using Shopee.Suite.Modules.Workspace;

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
        // Tạo các ViewModel MỘT LẦN — màn gộp v1.1 (Workspace) DÙNG CHUNG đúng 3 VM BigSeller/Scrape/Update
        // với 3 màn cũ → state luôn đồng bộ, không xung đột (vẫn giữ 3 màn cũ để đối chiếu/dự phòng).
        var bigSeller = new BigSellerViewModel();
        var scrape = new ScrapeViewModel();
        var update = new UpdateProductViewModel();
        var search = new SearchViewModel();
        var workspace = new WorkspaceViewModel(bigSeller, scrape, update);

        // Giao việc đa máy: worker (client tự chạy việc Hub giao) + dispatcher (máy Hub tự đẩy việc).
        // Cả hai tự "ngủ" khi máy chưa có vai trò / chưa bật điều phối → không đổi hành vi 1 máy.
        var worker = new AssignmentWorker(scrape, update, search);
        // Dispatcher (tự đẩy việc) CHỈ chạy timer trên máy Hub — máy client khỏi chạy vô ích.
        if (Shopee.Core.Coordination.HubServerConfigStore.Shared.Current.Enabled) HubDispatcher.Shared.Start();

        // Scrape + Update đã GỘP vào "BigSeller Workspace" → ẩn khỏi sidebar (vẫn dùng chung scrape/update
        // bên dưới; view cũ giữ nguyên để đảo lại nếu cần).
        Modules =
        [
            new ModuleItem("BigSeller Workspace", "🧩", "Scrape · Import · Update theo từng shop",
                workspace),
            new ModuleItem("Cấu hình BigSeller", "🗂", "Tài khoản · workbook · cookie · shop · proxy",
                bigSeller),
            new ModuleItem("Shopee Search", "📊", "Thống kê tìm kiếm sản phẩm",
                search),
            new ModuleItem("Tài khoản & Proxy", "👤", "Kho tài khoản Shopee dùng chung",
                new AccountsViewModel()),
            new ModuleItem("Check Shopee Account", "🔐", "Kiểm tra tài khoản Shopee",
                new CheckAccountViewModel()),
            new ModuleItem("Trạng thái & Giao việc", "🖥", "Theo dõi máy + Hub giao việc cho từng máy (đa máy)",
                new FleetViewModel(worker)),
            new ModuleItem("Cài đặt", "⚙", "AI provider / model / API key",
                new SettingsViewModel()),
        ];

        // Workspace bấm "Đăng nhập / cấu hình" → nhảy sidebar sang tab BigSeller (không làm trùng 2 nơi).
        workspace.RequestNavigate = target =>
            Selected = Modules.FirstOrDefault(m => ReferenceEquals(m.ViewModel, target));

        _welcome = new WelcomeViewModel(this);
        _selected = null; // mặc định: màn hình Welcome, không focus module nào
    }

    partial void OnSelectedChanged(ModuleItem? value) => OnPropertyChanged(nameof(Current));
}
