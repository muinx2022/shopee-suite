using System.Windows;
using Shopee.Suite.Infrastructure;

namespace Shopee.Suite.Modules.CheckAccount;

/// <summary>Cửa sổ riêng bọc <see cref="CheckAccountView"/> — mở từ nút "Check Acc" trong màn Tài khoản &amp; Proxy.
/// Mở KHÔNG modal qua <c>WindowHost.Show</c> (AccountsViewModel.OpenCheckAccount) và nhận DataContext từ ngoài.</summary>
public partial class CheckAccountWindow : Window
{
    public CheckAccountWindow()
    {
        InitializeComponent();
        this.FitOnOpen();   // màn nhỏ hơn 940×760 → thu vừa vùng làm việc thay vì tràn ra ngoài
    }
}
