using Avalonia.Controls;
using Avalonia.Interactivity;
using Shopee.Core.BigSeller;

namespace Shopee.Suite.Modules.UpdateProduct;

public partial class ColumnMapWindow : Window
{
    private readonly ColumnMapEditViewModel _vm;

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
