using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace XuLyDonShopee.App.Views;

/// <summary>Màn "Thống kê" — ảnh chụp kho đơn (thẻ số + 4 lưới phân bổ). Thuần binding, trừ một việc KHÔNG làm
/// được bằng binding: trả bánh xe chuột về cho trang khi lưới đã hết chỗ cuộn (xem
/// <see cref="LuoiXemSo_PreviewMouseWheel"/>).</summary>
public partial class OrderStatisticsView : UserControl
{
    /// <summary>Sai số khi so vị trí cuộn với hai biên (0 và <c>ScrollableHeight</c>) — offset của
    /// <see cref="ScrollViewer"/> là <see cref="double"/> nên so bằng <c>==</c> có thể trượt vài phần nghìn pixel
    /// và làm bánh xe kẹt ở đúng đáy lưới.</summary>
    private const double SaiSoBienCuon = 0.5;

    public OrderStatisticsView() => InitializeComponent();

    /// <summary>
    /// Lăn chuột trên MỘT trong 4 lưới thống kê. 4 lưới chiếm gần hết thân màn mà <see cref="DataGrid"/> NUỐT sạch
    /// bánh xe chuột (kể cả khi nó không có gì để cuộn) → người dùng đặt trỏ lên lưới là trang đứng im.
    /// <para>Luật: lưới CÒN chỗ cuộn theo đúng chiều đang lăn ⇒ để nguyên (giữ khả năng cuộn trong lưới dài);
    /// HẾT chỗ (hoặc không có gì để cuộn) ⇒ chặn rồi bắn lại sự kiện lên phần tử CHA để
    /// <see cref="ScrollViewer"/> của trang nhận.</para>
    /// </summary>
    private void LuoiXemSo_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject goc)
        {
            return;
        }

        var trong = TimScrollViewerCon(goc);
        if (trong is not null && ConChoCuonTheoChieuLan(trong.VerticalOffset, trong.ScrollableHeight, e.Delta))
        {
            return; // lưới vẫn cuộn được theo chiều này → đừng cướp bánh xe của nó
        }

        if (VisualTreeHelper.GetParent(goc) is not UIElement cha)
        {
            return; // chưa gắn cây hiển thị (vd đang dựng) → không có ai để chuyển tiếp
        }

        e.Handled = true;
        cha.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = cha
        });
    }

    /// <summary>
    /// HÀM THUẦN (test được, không cần dựng UI): lưới còn cuộn được theo chiều đang lăn không.
    /// <paramref name="delta"/> &gt; 0 = lăn LÊN (còn cuộn khi chưa ở đỉnh); &lt; 0 = lăn XUỐNG (còn cuộn khi chưa
    /// tới đáy). Không có gì để cuộn (<paramref name="tamCuon"/> ≤ 0) ⇒ false ngay — chuyển tiếp cho trang.
    /// </summary>
    internal static bool ConChoCuonTheoChieuLan(double viTri, double tamCuon, int delta)
    {
        if (tamCuon <= 0 || delta == 0)
        {
            return false;
        }

        return delta < 0
            ? viTri < tamCuon - SaiSoBienCuon
            : viTri > SaiSoBienCuon;
    }

    /// <summary>Tìm <see cref="ScrollViewer"/> NỘI BỘ của một lưới (template của <see cref="DataGrid"/> dựng ra nó)
    /// bằng cách duyệt cây hiển thị. Chưa dựng template / không có → null.</summary>
    private static ScrollViewer? TimScrollViewerCon(DependencyObject goc)
    {
        var soCon = VisualTreeHelper.GetChildrenCount(goc);
        for (var i = 0; i < soCon; i++)
        {
            var con = VisualTreeHelper.GetChild(goc, i);
            if (con is ScrollViewer sv)
            {
                return sv;
            }

            var sau = TimScrollViewerCon(con);
            if (sau is not null)
            {
                return sau;
            }
        }

        return null;
    }
}
