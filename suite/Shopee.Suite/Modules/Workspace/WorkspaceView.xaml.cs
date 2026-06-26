using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Shopee.Suite.Modules.Workspace;

public partial class WorkspaceView : UserControl
{
    public WorkspaceView() => InitializeComponent();

    /// <summary>Click 1 dòng shop → chọn shop đó (1 shop/account) để tô đậm + đặt làm shop chạy mặc định.</summary>
    private void ShopGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Root.DataContext is not WorkspaceViewModel vm) return;
        if (sender is DataGrid { SelectedItem: WorkspaceShopViewModel shop })
            vm.PickShopCommand.Execute(shop);
    }

    /// <summary>Click 1 dòng tiến trình (tab Scrape) → đưa cửa sổ Brave của process đó lên trước.</summary>
    private void Instance_Click(object sender, MouseButtonEventArgs e)
    {
        if (Root.DataContext is not WorkspaceViewModel vm) return;
        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.Item is Scrape.ScrapeInstanceViewModel inst)
            vm.Scrape.BringInstanceToFront(inst);
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T t) return t;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }
}
