using Avalonia.Controls;
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
        OrdersGrid.CellPointerPressed += OnCellPointerPressed;
    }

    /// <summary>
    /// Double-click (ClickCount==2, chuột trái) trên một dòng → lấy <see cref="OrderRowViewModel"/> của dòng
    /// (qua <c>e.Row.DataContext</c>) rồi mở hộp thoại thông tin + đổi trạng thái. Dòng không có data / view
    /// chưa gắn ViewModel → bỏ qua.
    /// </summary>
    private async void OnCellPointerPressed(object? sender, DataGridCellPointerPressedEventArgs e)
    {
        var pointer = e.PointerPressedEventArgs;
        if (pointer.ClickCount != 2)
        {
            return;
        }

        // Chỉ phản hồi double-click CHUỘT TRÁI (bỏ qua phải/giữa).
        if (!pointer.GetCurrentPoint(e.Cell).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.Row?.DataContext is not OrderRowViewModel row)
        {
            return;
        }

        if (DataContext is not OrdersViewModel vm)
        {
            return;
        }

        await vm.EditOrderStatusAsync(row);
    }
}
