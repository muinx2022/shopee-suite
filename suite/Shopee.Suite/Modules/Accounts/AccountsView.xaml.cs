using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Shopee.Suite.Modules.Accounts;

public partial class AccountsView : UserControl
{
    public AccountsView() => InitializeComponent();

    /// <summary>Bấm đúp 1 acc → mở Brave bằng profile acc tại trang captcha (hoặc trang chủ) để giải tay.</summary>
    private void AccountsGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is AccountsViewModel vm && vm.Selected is not null
            && vm.OpenForCheckCommand.CanExecute(vm.Selected))
            vm.OpenForCheckCommand.Execute(vm.Selected);
    }

    /// <summary>Rời màn (chuyển module / đóng app) → đóng Brave check đang mở, khỏi rò cửa sổ login.</summary>
    private void OnUnloaded(object sender, RoutedEventArgs e) => (DataContext as AccountsViewModel)?.KillCheckBrowser();
}
