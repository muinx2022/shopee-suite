using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Shopee.Suite.Modules.Accounts;

public partial class ImportAccountsWindow : Window
{
    public string Logins { get; private set; } = "";
    public string ProxyKeys { get; private set; } = "";

    public ImportAccountsWindow() => InitializeComponent();

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        Logins = LoginsBox.Text ?? "";
        ProxyKeys = KeysBox.Text ?? "";
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
