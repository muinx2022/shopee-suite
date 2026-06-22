using CommunityToolkit.Mvvm.ComponentModel;
using Shopee.Core.BigSeller;

namespace Shopee.Suite.Modules.UpdateProduct;

/// <summary>
/// VM cho dialog "Map dữ liệu" của 1 shop: sửa ánh xạ field ↔ cột Excel trên bản nháp (chữ cột),
/// bấm OK mới ghi vào <see cref="BigSellerShop.ColumnMap"/> + lưu kho (Hủy = bỏ thay đổi).
/// </summary>
public sealed partial class ColumnMapEditViewModel : ObservableObject
{
    private readonly BigSellerShop _shop;

    [ObservableProperty] private string _productName;     // Tên gốc
    [ObservableProperty] private string _rewrittenName;   // Tên đã sửa
    [ObservableProperty] private string _sku;
    [ObservableProperty] private string _price;           // Giá
    [ObservableProperty] private string _itemId;
    [ObservableProperty] private string _link;

    public string ShopName => _shop.DisplayName;

    public ColumnMapEditViewModel(BigSellerShop shop)
    {
        _shop = shop;
        var m = shop.ColumnMap ??= new BigSellerColumnMap();
        _productName = ExcelColumn.ToLetter(m.ProductNameColumn);
        _rewrittenName = ExcelColumn.ToLetter(m.RewrittenNameColumn);
        _sku = ExcelColumn.ToLetter(m.SkuColumn);
        _price = ExcelColumn.ToLetter(m.PriceColumn);
        _itemId = ExcelColumn.ToLetter(m.ItemIdColumn);
        _link = ExcelColumn.ToLetter(m.LinkColumn);
    }

    /// <summary>Ghi bản nháp vào ColumnMap của shop + lưu kho dùng chung.</summary>
    public void Apply()
    {
        var m = _shop.ColumnMap ??= new BigSellerColumnMap();
        m.ProductNameColumn = ExcelColumn.FromLetter(ProductName);
        m.RewrittenNameColumn = ExcelColumn.FromLetter(RewrittenName);
        m.SkuColumn = ExcelColumn.FromLetter(Sku);
        m.PriceColumn = ExcelColumn.FromLetter(Price);
        m.ItemIdColumn = ExcelColumn.FromLetter(ItemId);
        m.LinkColumn = ExcelColumn.FromLetter(Link);
        BigSellerStore.Shared.Save();
    }
}
