using Avalonia.Controls;
using Avalonia.Interactivity;

namespace XuLyDonShopee.App.Views;

public partial class ImportProxyDialog : Window
{
    public ImportProxyDialog()
    {
        InitializeComponent();
    }

    private void OnImport(object? sender, RoutedEventArgs e) => Close(ProxyTextBox.Text ?? string.Empty);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close((string?)null);
}
