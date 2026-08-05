using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using XuLyDonShopee.App.ViewModels;

namespace XuLyDonShopee.App.Views;

/// <summary>
/// Màn "Đơn hàng". Ngoài binding MVVM, code-behind lo phần thao tác con trỏ trên <see cref="DataGrid"/>:
/// double-click MỘT dòng → mở hộp thoại thông tin cơ bản của đơn + cho đổi trạng thái
/// (<see cref="OrdersViewModel.EditOrderStatusAsync"/>), và ĐỒNG BỘ ẨN/HIỆN 6 CỘT PHỤ theo menu chuột phải
/// (<see cref="DataGridColumn"/> KHÔNG nằm trong cây trực quan nên không bind Visibility thẳng vào VM được —
/// đây là chỗ duy nhất của WPF bắt buộc phải làm bằng tay).
/// </summary>
public partial class OrdersView : UserControl
{
    public OrdersView()
    {
        InitializeComponent();
        // Avalonia dùng DataGrid.CellPointerPressed + ClickCount==2; WPF không có sự kiện đó → dùng
        // MouseDoubleClick (bubble từ ô lên lưới) rồi tự leo cây trực quan tìm dòng.
        OrdersGrid.MouseDoubleClick += OnGridDoubleClick;
        // VM được gắn SAU khi dựng view (DataTemplate của MainView) → bám theo DataContextChanged.
        // VM sống SUỐT ĐỜI app còn view bị DataTemplate dựng MỚI mỗi lượt vào màn và KHÔNG đổi DataContext
        // khi bị vứt — nếu chỉ gỡ PropertyChanged trong DataContextChanged thì view cũ bị VM giữ sống mãi
        // (rò 1 view + DataGrid 15 cột mỗi lượt điều hướng). Gỡ ở Unloaded mới đúng vòng đời.
        DataContextChanged += OnViewDataContextChanged;
        Loaded += (_, _) => { if (DataContext is OrdersViewModel vm) Subscribe(vm); };
        Unloaded += (_, _) => { if (DataContext is OrdersViewModel vm) vm.PropertyChanged -= OnVmPropertyChanged; };
    }

    // ═══════════ Ẩn/hiện cột phụ ═══════════

    private void OnViewDataContextChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is OrdersViewModel previous)
        {
            previous.PropertyChanged -= OnVmPropertyChanged;
        }

        if (e.NewValue is not OrdersViewModel vm)
        {
            return;
        }

        // Chưa vào cây thì KHÔNG đăng ký (Loaded sẽ lo) — view bị vứt trước khi load không được phép
        // bám vào VM; vẫn áp trạng thái cột để view dựng ra đúng ngay từ đầu.
        if (IsLoaded) Subscribe(vm);
        else ApplyColumnVisibility(vm);
    }

    /// <summary>Đăng ký nghe ShowCol* (gỡ trước khi gắn để Loaded/DataContextChanged không đăng ký đôi)
    /// rồi áp trạng thái cột hiện tại.</summary>
    private void Subscribe(OrdersViewModel vm)
    {
        vm.PropertyChanged -= OnVmPropertyChanged;
        vm.PropertyChanged += OnVmPropertyChanged;
        ApplyColumnVisibility(vm);
    }

    /// <summary>Chỉ quan tâm 6 property ShowCol* — property khác của VM bắn liên tục (mỗi lượt sync) nên
    /// không đụng cột cho rẻ.</summary>
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is OrdersViewModel vm && e.PropertyName is not null && e.PropertyName.StartsWith("ShowCol"))
        {
            ApplyColumnVisibility(vm);
        }
    }

    private void ApplyColumnVisibility(OrdersViewModel vm)
    {
        ColPhanLoai.Visibility = Show(vm.ShowColPhanLoai);
        ColDonTraHang.Visibility = Show(vm.ShowColDonTraHang);
        ColUocTinh.Visibility = Show(vm.ShowColUocTinh);
        ColThanhToan.Visibility = Show(vm.ShowColThanhToan);
        ColDvvc.Visibility = Show(vm.ShowColDvvc);
        ColSyncLuc.Visibility = Show(vm.ShowColSyncLuc);

        // Collapsed (không phải Hidden): cột ẩn PHẢI nhả hết chỗ, không thì bảng vẫn rộng nguyên.
        static Visibility Show(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;
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
