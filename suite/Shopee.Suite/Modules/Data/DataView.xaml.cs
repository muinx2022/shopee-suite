using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Shopee.Suite.Infrastructure;

namespace Shopee.Suite.Modules.Data;

public partial class DataView : UserControl
{
    public DataView()
    {
        InitializeComponent();
        // Nạp option + trang 1 khi View hiển thị lần đầu (ctor VM không I/O; EnsureLoaded tự chốt 1 lần).
        Loaded += (_, _) => (DataContext as DataViewModel)?.EnsureLoaded();
        // Bấm BẤT KỲ ĐÂU trên dòng lưới = tick/untick chọn (như /data trên hub). Dùng bản PREVIEW (tunnel) +
        // handledEventsToo vì DataGrid tự đánh dấu handled sự kiện chuột khi xử lý selection của nó.
        RowsGrid.AddHandler(PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler(OnGridTapped), handledEventsToo: true);
    }

    // Đi ngược cây visual từ điểm bấm: trúng nút ✏ / chính checkbox → phần tử đó tự xử lý (đừng toggle chồng);
    // còn lại tìm DataGridRow → đảo cờ chọn của dòng (DataRowItem báo VM → engine).
    private static void OnGridTapped(object sender, MouseButtonEventArgs e)
    {
        var d = e.OriginalSource as DependencyObject;
        while (d is not null and not DataGridRow)
        {
            if (d is ButtonBase) return;   // Button (✏) và CheckBox đều là ButtonBase
            d = VisualTreeSearch.GetParent(d);   // bắc cầu visual ⇄ content element (Infrastructure)
        }
        if (d is DataGridRow row && row.Item is DataRowItem item)
            item.IsSelected = !item.IsSelected;
    }
}
