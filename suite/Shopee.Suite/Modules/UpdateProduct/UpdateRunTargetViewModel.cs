using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Shopee.Core.BigSeller;

namespace Shopee.Suite.Modules.UpdateProduct;

/// <summary>
/// Một "đích chạy" = 1 tài khoản BigSeller + 1 shop (↔ sheet) của tk đó. Tick chọn nhiều tk chạy SONG
/// SONG; mỗi tk có cookie/profile/map cột RIÊNG (map nằm trên shop đã chọn) và CẤU HÌNH CHẠY RIÊNG
/// (từ dòng / đến dòng / số worker) — hiển thị ở panel chi tiết bên phải, giống Shopee Scrape.
/// </summary>
public sealed partial class UpdateRunTargetViewModel : ObservableObject
{
    public BigSellerAccount Account { get; }
    public ObservableCollection<BigSellerShop> Shops { get; }

    [ObservableProperty] private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SheetName), nameof(ShopChosen))]
    private BigSellerShop? _selectedShop;

    // ── Cấu hình chạy RIÊNG cho từng tk BigSeller (sửa ở panel chi tiết) ──
    /// <summary>Bắt đầu từ dòng nào của sheet (≥2 vì dòng 1 là header).</summary>
    [ObservableProperty] private int _startRow = 2;
    /// <summary>Đến dòng (0 = hết).</summary>
    [ObservableProperty] private int _endRow;
    /// <summary>Số worker (lane) cho Import to store.</summary>
    [ObservableProperty] private int _importWorkers = 1;
    /// <summary>Số worker (lane) cho Update product.</summary>
    [ObservableProperty] private int _updateWorkers = 1;
    /// <summary>Reload trang listing mỗi N giây.</summary>
    [ObservableProperty] private int _listingReloadSeconds = 20;

    public UpdateRunTargetViewModel(BigSellerAccount account)
    {
        Account = account;
        Shops = new ObservableCollection<BigSellerShop>(account.Shops);
    }

    public string DisplayName => Account.DisplayName;
    public string CookieStatus => Account.HasCookie ? "✓ cookie" : "⚠ chưa cookie";
    public string WorkbookPath => Account.WorkbookPath;
    public string SheetName => SelectedShop?.ShopeeDataSheet ?? "";
    public bool ShopChosen => SelectedShop is not null;
}
