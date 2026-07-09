using Avalonia.Controls;
using Avalonia.Interactivity;
using Shopee.Core.BigSeller;

namespace Shopee.Suite.Modules.UpdateProduct;

public partial class ColumnMapWindow : Window
{
    private readonly ColumnMapEditViewModel _vm = null!;   // ctor rỗng chỉ cho XAML designer; runtime luôn qua ctor(shop) nên _vm non-null khi OnOk/OnCancel.

    public ColumnMapWindow() => InitializeComponent();

    public ColumnMapWindow(BigSellerShop shop)
    {
        InitializeComponent();
        _vm = new ColumnMapEditViewModel(shop);
        DataContext = _vm;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        _vm.Apply();
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
