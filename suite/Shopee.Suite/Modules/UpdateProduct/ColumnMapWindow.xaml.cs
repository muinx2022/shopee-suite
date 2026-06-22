using System.Windows;
using Shopee.Core.BigSeller;

namespace Shopee.Suite.Modules.UpdateProduct;

public partial class ColumnMapWindow : Window
{
    private readonly ColumnMapEditViewModel _vm;

    public ColumnMapWindow(BigSellerShop shop)
    {
        InitializeComponent();
        _vm = new ColumnMapEditViewModel(shop);
        DataContext = _vm;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        _vm.Apply();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
