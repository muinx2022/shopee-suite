using System.Windows;
using Shopee.Suite.Infrastructure;

namespace Shopee.Suite.Modules.Scrape;

/// <summary>Cửa sổ "Thống kê scrape" của tk BigSeller đang chọn: tiến độ theo sheet + nút xoá tiến độ.
/// Mở modal qua <c>WindowHost.ShowDialogAsync</c> (ScrapeViewModel.ShowStatsAsync); đóng bằng nút Đóng
/// (không trả kết quả — VM chỉ RefreshProgress sau khi cửa sổ tắt).</summary>
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

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
