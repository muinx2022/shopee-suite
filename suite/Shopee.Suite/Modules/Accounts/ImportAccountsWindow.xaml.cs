using System.Windows;

namespace Shopee.Suite.Modules.Accounts;

public partial class ImportAccountsWindow : Window
{
    public string Logins { get; private set; } = "";
    public string ProxyKeys { get; private set; } = "";

    public ImportAccountsWindow() => InitializeComponent();

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Logins = LoginsBox.Text;
        ProxyKeys = KeysBox.Text;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
