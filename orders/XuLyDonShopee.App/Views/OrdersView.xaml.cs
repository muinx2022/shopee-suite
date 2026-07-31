using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using XuLyDonShopee.App.ViewModels;

namespace XuLyDonShopee.App.Views;

/// <summary>
/// Màn "Đơn hàng". Ngoài binding MVVM, code-behind lo phần thao tác con trỏ trên <see cref="DataGrid"/>:
/// double-click MỘT dòng → mở hộp thoại thông tin cơ bản của đơn + cho đổi trạng thái
/// (<see cref="OrdersViewModel.EditOrderStatusAsync"/>).
/// </summary>
public partial class OrdersView : UserControl
{
    public OrdersView()
    {
        InitializeComponent();
        // Avalonia dùng DataGrid.CellPointerPressed + ClickCount==2; WPF không có sự kiện đó → dùng
        // MouseDoubleClick (bubble từ ô lên lưới) rồi tự leo cây trực quan tìm dòng.
        OrdersGrid.MouseDoubleClick += OnGridDoubleClick;
    }

    /// <summary>
    /// Double-click CHUỘT TRÁI trên một dòng → lấy <see cref="OrderRowViewModel"/> của dòng rồi mở hộp thoại
    /// thông tin + đổi trạng thái. Bấm vào vùng trống / đầu cột (không leo được tới dòng nào) → bỏ qua.
    /// </summary>
    private async void OnGridDoubleClick(object? sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (FindRow(e.OriginalSource as DependencyObject)?.Item is not OrderRowViewModel row)
        {
            return;
        }

        if (DataContext is not OrdersViewModel vm)
        {
            return;
        }

        await vm.EditOrderStatusAsync(row);
    }

    /// <summary>Leo cây TRỰC QUAN từ phần tử bị bấm lên tới <see cref="DataGridRow"/> chứa nó (null nếu
    /// không nằm trong dòng nào).</summary>
    private static DataGridRow? FindRow(DependencyObject? source)
    {
        while (source is not null and not DataGridRow)
        {
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return source as DataGridRow;
    }
}
