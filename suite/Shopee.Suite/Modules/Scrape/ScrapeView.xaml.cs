using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Shopee.Suite.Modules.Scrape;

public partial class ScrapeView : UserControl
{
    public ScrapeView() => InitializeComponent();

    /// <summary>
    /// Click vào THÂN dòng (không phải checkbox) → vừa chọn dòng (xem detail bên phải) vừa TỰ TICK dòng đó.
    /// Click trúng checkbox → bỏ qua ở đây để checkbox tự toggle (tick/untick); dòng vẫn được chọn nhờ
    /// checkbox focusable. Nhờ vậy: click đâu cũng chọn dòng; muốn bỏ tick thì bấm lại checkbox.
    /// </summary>
    private void TargetRow_Click(object sender, MouseButtonEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        var item = FindAncestor<ListBoxItem>(src);
        if (item?.DataContext is not ScrapeTargetViewModel vm) return;

        // Luôn CHỌN dòng (hiện detail bên phải) — cho cả click checkbox lẫn thân dòng.
        item.IsSelected = true;

        // Click trúng checkbox → để checkbox TỰ toggle (đừng toggle thêm kẻo huỷ lẫn nhau).
        if (FindAncestor<CheckBox>(src) is not null) return;

        // Click thân dòng → TOGGLE tick (tick nếu chưa, bỏ tick nếu đang tick).
        vm.IsSelected = !vm.IsSelected;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T t) return t;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }
}
