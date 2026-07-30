using Avalonia.Controls;
using Avalonia.Interactivity;
using Shopee.Suite.Infrastructure;

namespace Shopee.Suite.Modules.Scrape;

public partial class ScrapeStatsWindow : Window
{
    public ScrapeStatsWindow()
    {
        InitializeComponent();
        this.FitOnOpen();   // màn nhỏ hơn 760×560 → thu vừa vùng làm việc thay vì tràn ra ngoài
    }

    public ScrapeStatsWindow(ScrapeStatsViewModel vm) : this()   // qua ctor rỗng để dùng chung clamp màn hình
    {
        DataContext = vm;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
