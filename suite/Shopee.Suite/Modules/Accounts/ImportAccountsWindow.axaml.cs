using Avalonia.Controls;
using Avalonia.Interactivity;
using Shopee.Suite.Infrastructure;

namespace Shopee.Suite.Modules.Accounts;

public partial class ImportAccountsWindow : Window
{
    public string Logins { get; private set; } = "";
    public string ProxyKeys { get; private set; } = "";

    public ImportAccountsWindow()
    {
        InitializeComponent();
        this.FitOnOpen();   // màn nhỏ hơn 720×560 → thu vừa vùng làm việc thay vì tràn ra ngoài
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        Logins = LoginsBox.Text ?? "";
        ProxyKeys = KeysBox.Text ?? "";
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
