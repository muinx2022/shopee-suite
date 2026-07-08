using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Shopee.Suite.Modules.Scrape;

public partial class ScrapeStatsWindow : Window
{
    public ScrapeStatsWindow() => InitializeComponent();

    public ScrapeStatsWindow(ScrapeStatsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
