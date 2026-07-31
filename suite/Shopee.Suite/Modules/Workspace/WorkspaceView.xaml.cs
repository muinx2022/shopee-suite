using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Shopee.Suite.Infrastructure;

namespace Shopee.Suite.Modules.Workspace;

public partial class WorkspaceView : UserControl
{
    public WorkspaceView()
    {
        InitializeComponent();
        // Bấm 1 dòng tiến trình (tab Theo dõi Scrape) → đưa cửa sổ Brave của process đó lên trước.
        // (Avalonia: `PointerReleased` trên DataGrid.) Dùng bản PREVIEW (tunnel) + handledEventsToo vì DataGrid
        // tự đánh dấu handled sự kiện chuột khi xử lý selection của nó.
        InstancesGrid.AddHandler(PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler(OnInstanceClick), handledEventsToo: true);
    }

    /// <summary>Click 1 dòng shop → chọn shop đó (1 shop/account) để tô đậm + đặt làm shop chạy mặc định.</summary>
    private void ShopGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not WorkspaceViewModel vm) return;
        if (sender is DataGrid { SelectedItem: WorkspaceShopViewModel shop })
            vm.PickShopCommand.Execute(shop);
    }

    private void OnInstanceClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not WorkspaceViewModel vm) return;
        var row = VisualTreeSearch.FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.DataContext is Scrape.ScrapeInstanceViewModel inst)
            vm.Scrape.BringInstanceToFront(inst);
    }
}
