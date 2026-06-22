using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Shopee.Core.BigSeller;

namespace Shopee.Suite.Modules.BigSeller;

/// <summary>Bao một <see cref="BigSellerAccount"/>: thông tin account + danh sách shop + danh
/// sách sheet đọc từ workbook để chọn cho từng shop.</summary>
public sealed class BigSellerAccountItemViewModel : ObservableObject
{
    public BigSellerAccount Model { get; }

    public ObservableCollection<BigSellerShopViewModel> Shops { get; } = [];
    /// <summary>Tên các sheet đọc từ workbook (đổ vào combo chọn sheet của shop).</summary>
    public ObservableCollection<string> SheetOptions { get; } = [];

    public BigSellerAccountItemViewModel(BigSellerAccount model)
    {
        Model = model;
        foreach (var s in model.Shops) Shops.Add(new BigSellerShopViewModel(s));
        RefreshSheets();
    }

    // Mọi chỉnh sửa account tự LƯU ngay (tránh mất khi chuyển module / khởi động lại).
    private static void Persist() => BigSellerStore.Shared.Save();

    public string Label
    {
        get => Model.Label;
        set { if (Model.Label != value) { Model.Label = value; OnChanged(nameof(Label), nameof(DisplayName)); Persist(); } }
    }

    public string Email
    {
        get => Model.Email;
        set { if (Model.Email != value) { Model.Email = value; OnChanged(nameof(Email), nameof(DisplayName)); Persist(); } }
    }

    public string WorkbookPath
    {
        get => Model.WorkbookPath;
        set { if (Model.WorkbookPath != value) { Model.WorkbookPath = value; OnPropertyChanged(); RefreshSheets(); Persist(); } }
    }

    public string CookieFile
    {
        get => Model.CookieFile;
        set { if (Model.CookieFile != value) { Model.CookieFile = value; OnChanged(nameof(CookieFile), nameof(CookieStatus)); Persist(); } }
    }

    public string DisplayName => Model.DisplayName;
    public int ShopCount => Shops.Count;
    public string CookieStatus => Model.HasCookie ? "✓ Đã có cookie BigSeller" : "⚠ Chưa đăng nhập BigSeller";

    public BigSellerShopViewModel AddShop()
    {
        var shop = new BigSellerShop { Name = "Shop mới" };
        Model.Shops.Add(shop);
        var vm = new BigSellerShopViewModel(shop);
        Shops.Add(vm);
        OnPropertyChanged(nameof(ShopCount));
        return vm;
    }

    public void RemoveShop(BigSellerShopViewModel shop)
    {
        Model.Shops.Remove(shop.Model);
        Shops.Remove(shop);
        OnPropertyChanged(nameof(ShopCount));
    }

    public void RefreshSheets()
    {
        // Danh sách mong muốn = sheet trong workbook + sheet đang gán cho các shop (để combo
        // SelectedItem luôn khớp, không bị reset null).
        var desired = WorkbookSheets.ListSheetNames(Model.WorkbookPath);
        foreach (var s in Model.Shops)
            if (!string.IsNullOrWhiteSpace(s.ShopeeDataSheet) && !desired.Contains(s.ShopeeDataSheet))
                desired.Add(s.ShopeeDataSheet);

        // Cập nhật TĂNG DẦN (KHÔNG Clear) — nếu Clear thì combo mất giá trị giữa chừng → ghi null
        // ngược + auto-save làm MẤT sheet đã chọn. Chỉ xoá cái thừa, thêm cái thiếu.
        for (var i = SheetOptions.Count - 1; i >= 0; i--)
            if (!desired.Contains(SheetOptions[i])) SheetOptions.RemoveAt(i);
        foreach (var name in desired)
            if (!SheetOptions.Contains(name)) SheetOptions.Add(name);
    }

    public void NotifyCookieChanged() => OnPropertyChanged(nameof(CookieStatus));

    private void OnChanged(params string[] names)
    {
        foreach (var n in names) OnPropertyChanged(n);
    }
}
