using System.Windows;
using Shopee.Suite.Infrastructure;

namespace Shopee.Suite.Modules.Accounts;

/// <summary>
/// Cửa sổ modal "Import tài khoản / proxy". Kết quả trả về <see cref="AccountsViewModel"/> qua
/// <c>DialogResult</c> (Import → true, Hủy/X → false/null) + 2 property nội dung.
/// </summary>
public partial class ImportAccountsWindow : Window
{
    public string Logins { get; private set; } = "";
    public string ProxyKeys { get; private set; } = "";

    public ImportAccountsWindow()
    {
        InitializeComponent();
        this.FitOnOpen();   // màn nhỏ hơn 720×560 → thu vừa vùng làm việc thay vì tràn ra ngoài
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Logins = LoginsBox.Text ?? "";
        ProxyKeys = KeysBox.Text ?? "";
        CloseWith(true);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => CloseWith(false);

    /// <summary>Đóng kèm kết quả. <c>DialogResult</c> chỉ gán được khi mở bằng <c>ShowDialog()</c>; đường
    /// fallback <c>Show()</c> của <c>WindowHost</c> (chưa có cửa sổ chính) thì đóng thẳng.</summary>
    private void CloseWith(bool result)
    {
        try { DialogResult = result; }
        catch (InvalidOperationException) { Close(); }
    }
}
