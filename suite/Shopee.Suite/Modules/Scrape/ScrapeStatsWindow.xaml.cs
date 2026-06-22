using System.Windows;

namespace Shopee.Suite.Modules.Scrape;

public partial class ScrapeStatsWindow : Window
{
    public ScrapeStatsWindow(ScrapeStatsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
