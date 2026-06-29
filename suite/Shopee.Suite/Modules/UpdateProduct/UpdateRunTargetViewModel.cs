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
        // Khôi phục shop đã chọn từ Id đã LƯU (gán field trực tiếp để không trigger Persist lúc dựng VM).
        _selectedShop = Shops.FirstOrDefault(s => s.Id == account.UpdateSelectedShopId);
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
            // Cấu hình chạy đọc theo shop đang chọn → báo đổi để UI nạp lại giá trị của shop mới.
            OnPropertyChanged(nameof(StartRow));
            OnPropertyChanged(nameof(EndRow));
            OnPropertyChanged(nameof(ImportWorkers));
            OnPropertyChanged(nameof(UpdateWorkers));
            OnPropertyChanged(nameof(ListingReloadSeconds));
            Persist();
        }
    }

    // ── Cấu hình chạy RIÊNG theo SHOP đã chọn (lưu trên model BigSellerShop) ──
    /// <summary>Bắt đầu từ dòng nào của sheet (≥2 vì dòng 1 là header).</summary>
    public int StartRow
    {
        get => SelectedShop?.StartRow ?? 2;
        set { if (SelectedShop is { } s && s.StartRow != value) { s.StartRow = value; OnPropertyChanged(); Persist(); } }
    }

    /// <summary>Đến dòng (0 = hết).</summary>
    public int EndRow
    {
        get => SelectedShop?.EndRow ?? 0;
        set { if (SelectedShop is { } s && s.EndRow != value) { s.EndRow = value; OnPropertyChanged(); Persist(); } }
    }

    /// <summary>Số worker (lane) cho Import to store.</summary>
    public int ImportWorkers
    {
        get => SelectedShop?.ImportWorkers ?? 1;
        set { if (SelectedShop is { } s && s.ImportWorkers != value) { s.ImportWorkers = value; OnPropertyChanged(); Persist(); } }
    }

    /// <summary>Số worker (lane) cho Update product.</summary>
    public int UpdateWorkers
    {
        get => SelectedShop?.UpdateWorkers ?? 1;
        set { if (SelectedShop is { } s && s.UpdateWorkers != value) { s.UpdateWorkers = value; OnPropertyChanged(); Persist(); } }
    }

    /// <summary>Reload trang listing mỗi N giây.</summary>
    public int ListingReloadSeconds
    {
        get => SelectedShop?.ListingReloadSeconds ?? 20;
        set { if (SelectedShop is { } s && s.ListingReloadSeconds != value) { s.ListingReloadSeconds = value; OnPropertyChanged(); Persist(); } }
    }

    public string DisplayName => Account.DisplayName;
    public string CookieStatus => Account.HasCookie ? "✓ cookie" : "⚠ chưa cookie";
    public string WorkbookPath => Account.WorkbookPath;
    public string SheetName => SelectedShop?.ShopeeDataSheet ?? "";
    public bool ShopChosen => SelectedShop is not null;
}
