using Shopee.Suite.Views;

namespace Shopee.Suite.Modules.Scrape;

/// <summary>
/// Cửa sổ "Thống kê scrape" của tk BigSeller đang chọn — TẠM là placeholder (port ở ĐỢT 3). Giữ 2 hàm dựng
/// như bản cũ để <see cref="ScrapeViewModel"/> không phải sửa.
/// </summary>
public sealed class ScrapeStatsWindow : PortingWindow
{
    public ScrapeStatsWindow()
        : base("Thống kê scrape", "Thống kê scrape theo sheet", "đợt 3")
    {
    }

    public ScrapeStatsWindow(ScrapeStatsViewModel vm) : this()
    {
        DataContext = vm;
    }
}
