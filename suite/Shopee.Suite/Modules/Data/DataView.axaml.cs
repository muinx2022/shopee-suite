using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Shopee.Suite.Modules.Data;

public partial class DataView : UserControl
{
    public DataView()
    {
        InitializeComponent();
        // Nạp option + trang 1 khi View hiển thị lần đầu (ctor VM không I/O).
        Loaded += (_, _) => (DataContext as DataViewModel)?.EnsureLoaded();
        // Bấm BẤT KỲ ĐÂU trên dòng lưới = tick/untick chọn (như /data trên hub). handledEventsToo vì DataGrid có thể
        // đánh dấu handled pointer-event khi tự xử lý selection.
        RowsGrid.AddHandler(Gestures.TappedEvent, OnGridTapped, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    // Đi ngược cây visual từ điểm bấm: trúng nút ✏ / chính checkbox → phần tử đó tự xử lý (đừng toggle chồng);
    // còn lại tìm DataGridRow → đảo cờ chọn của dòng (DataRowItem báo VM → engine).
    private static void OnGridTapped(object? sender, TappedEventArgs e)
    {
        var v = e.Source as Avalonia.Visual;
        while (v is not null and not DataGridRow)
        {
            if (v is Button or CheckBox) return;
            v = v.GetVisualParent();
        }
        if (v is DataGridRow row && row.DataContext is DataRowItem item)
            item.IsSelected = !item.IsSelected;
    }
}
