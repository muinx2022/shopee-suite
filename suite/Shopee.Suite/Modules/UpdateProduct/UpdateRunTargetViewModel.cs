using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Shopee.Core.BigSeller;

namespace Shopee.Suite.Modules.UpdateProduct;

/// <summary>
/// Một "đích chạy" = 1 tài khoản BigSeller + 1 shop (↔ sheet) của tk đó. Tick chọn nhiều tk chạy SONG
/// SONG; mỗi tk có cookie/profile/map cột RIÊNG (map nằm trên shop đã chọn) và CẤU HÌNH CHẠY RIÊNG
/// (từ dòng / đến dòng / số worker) — hiển thị ở panel chi tiết bên phải, giống Shopee Scrape.
///
/// LƯU Ý: tick chọn + shop đang chọn + cấu hình chạy GIỜ ĐƯỢC LƯU xuống model (BigSellerStore) — trước
/// đây chỉ là UI-state nên đóng/mở lại app là mất ("không lưu thông tin đã set và chọn").
/// </summary>
public sealed class UpdateRunTargetViewModel : ObservableObject
{
    public BigSellerAccount Account { get; }
    public ObservableCollection<BigSellerShop> Shops { get; }

    public UpdateRunTargetViewModel(BigSellerAccount account)
    {
        Account = account;
        Shops = new ObservableCollection<BigSellerShop>(account.Shops);
        // Cấu hình CHẠY giờ ở mức account (Account.RunConfig) — migrate 1 lần từ nguồn cũ nếu chưa có.
        Shopee.Suite.Infrastructure.RunConfigMigration.EnsureRunConfig(account);
        // Khôi phục shop đã chọn từ Id đã LƯU; nếu chưa lưu (hoặc Id không còn khớp) thì MẶC ĐỊNH shop đầu —
        // GIỐNG ScrapeTargetViewModel. SelectedShop vẫn cần cho sheet/map/import; cấu hình chạy KHÔNG còn theo
        // shop nên đổi shop KHÔNG đổi giá trị. (Gán field trực tiếp để không trigger Persist lúc dựng VM.)
        _selectedShop = Shops.FirstOrDefault(s => s.Id == account.UpdateSelectedShopId) ?? Shops.FirstOrDefault();
    }

    private static void Persist() => BigSellerStore.Shared.Save();

    /// <summary>Tick chọn chạy Update/Import — LƯU vào model.</summary>
    public bool IsSelected
    {
        get => Account.UpdateRunSelected;
        set { if (Account.UpdateRunSelected != value) { Account.UpdateRunSelected = value; OnPropertyChanged(); Persist(); } }
    }

    private BigSellerShop? _selectedShop;
    /// <summary>Shop đang chọn (panel chi tiết) — LƯU Id vào model để khôi phục.</summary>
    public BigSellerShop? SelectedShop
    {
        get => _selectedShop;
        set
        {
            if (ReferenceEquals(_selectedShop, value)) return;
            _selectedShop = value;
            Account.UpdateSelectedShopId = value?.Id ?? "";
            OnPropertyChanged();
            OnPropertyChanged(nameof(SheetName));
            OnPropertyChanged(nameof(ShopChosen));
            // Cấu hình chạy GIỜ theo account (Account.RunConfig), KHÔNG theo shop → đổi shop KHÔNG đổi giá trị,
            // khỏi notify StartRow/EndRow/UpdateWorkers/ListingReloadSeconds.
            Persist();
        }
    }

    // ── Cấu hình CHẠY giờ ở MỨC ACCOUNT (Account.RunConfig) — proxy đọc/ghi thẳng RunConfig, DÙNG CHUNG với
    //    ScrapeTargetViewModel (cùng object) nên nhất quán. (UpdateWorkers ↔ Processes, ListingReloadSeconds ↔
    //    ReloadSeconds.) Setter LƯU xuống bigseller.json (BigSellerStore). RIÊNG-MÁY. ──
    private BigSellerRunConfig Cfg => Account.RunConfig ??= new BigSellerRunConfig();

    /// <summary>Bắt đầu từ dòng nào của sheet (≥2 vì dòng 1 là header).</summary>
    public int StartRow
    {
        get => Cfg.StartRow;
        set { if (Cfg.StartRow != value) { Cfg.StartRow = value; OnPropertyChanged(); Persist(); } }
    }

    /// <summary>Đến dòng (0 = hết).</summary>
    public int EndRow
    {
        get => Cfg.EndRow;
        set { if (Cfg.EndRow != value) { Cfg.EndRow = value; OnPropertyChanged(); Persist(); } }
    }

    /// <summary>Số worker (lane) — map RunConfig.Processes (dùng cho CẢ import lẫn update).</summary>
    public int UpdateWorkers
    {
        get => Cfg.Processes;
        set { if (Cfg.Processes != value) { Cfg.Processes = value; OnPropertyChanged(); Persist(); } }
    }

    /// <summary>Reload trang listing mỗi N giây — map RunConfig.ReloadSeconds.</summary>
    public int ListingReloadSeconds
    {
        get => Cfg.ReloadSeconds;
        set { if (Cfg.ReloadSeconds != value) { Cfg.ReloadSeconds = value; OnPropertyChanged(); Persist(); } }
    }

    public string DisplayName => Account.DisplayName;
    public string CookieStatus => Account.HasCookie ? "✓ cookie" : "⚠ chưa cookie";
    public string WorkbookPath => Account.WorkbookPath;
    public string SheetName => SelectedShop?.ShopeeDataSheet ?? "";
    public bool ShopChosen => SelectedShop is not null;
}
