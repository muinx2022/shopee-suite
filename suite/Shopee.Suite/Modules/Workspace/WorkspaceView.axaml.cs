using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Shopee.Suite.Modules.Workspace;

public partial class WorkspaceView : UserControl
{
    public WorkspaceView() => InitializeComponent();

    /// <summary>Click 1 dòng shop → chọn shop đó (1 shop/account) để tô đậm + đặt làm shop chạy mặc định.</summary>
    private void ShopGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not WorkspaceViewModel vm) return;
        if (sender is DataGrid { SelectedItem: WorkspaceShopViewModel shop })
            vm.PickShopCommand.Execute(shop);
    }

    /// <summary>Click 1 dòng tiến trình (tab Scrape) → đưa cửa sổ Brave của process đó lên trước.</summary>
    private void Instance_Click(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is not WorkspaceViewModel vm) return;
        var row = (e.Source as Visual)?.FindAncestorOfType<DataGridRow>();
        if (row?.DataContext is Scrape.ScrapeInstanceViewModel inst)
            vm.Scrape.BringInstanceToFront(inst);
    }
}