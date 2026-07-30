using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Shopee.Suite.Modules.Accounts;

public partial class AccountsView : UserControl
{
    public AccountsView() => InitializeComponent();

    /// <summary>Bấm đúp 1 acc → mở Brave bằng profile acc tại trang captcha (hoặc trang chủ) để giải tay.
    /// Chỉ tính khi bấm trúng MỘT DÒNG DỮ LIỆU: bấm đúp lên header / vùng trống dưới danh sách / thanh cuộn
    /// cũng bắn <c>MouseDoubleClick</c> ở WPF (khác <c>DoubleTapped</c> của Avalonia).</summary>
    private void AccountsGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindRow(e.OriginalSource as DependencyObject) is null) return;
        if (DataContext is AccountsViewModel vm && vm.Selected is not null
            && vm.OpenForCheckCommand.CanExecute(vm.Selected))
            vm.OpenForCheckCommand.Execute(vm.Selected);
    }

    /// <summary>Đi ngược cây visual tìm dòng chứa điểm bấm; không có = bấm ngoài vùng dòng.</summary>
    private static DataGridRow? FindRow(DependencyObject? d)
    {
        while (d is not null and not DataGridRow)
            d = d is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : (d as FrameworkContentElement)?.Parent;
        return d as DataGridRow;
    }

    /// <summary>Rời màn (chuyển module / đóng app) → đóng Brave check đang mở, khỏi rò cửa sổ login.</summary>
    private void OnUnloaded(object sender, RoutedEventArgs e) => (DataContext as AccountsViewModel)?.KillCheckBrowser();
}
