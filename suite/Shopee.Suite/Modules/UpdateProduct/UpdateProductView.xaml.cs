using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Shopee.Suite.Modules.UpdateProduct;

public partial class UpdateProductView : UserControl
{
    public UpdateProductView() => InitializeComponent();

    /// <summary>
    /// Click vào THÂN dòng → vừa chọn dòng (hiện cấu hình bên phải) vừa TICK/UNTICK dòng đó.
    /// Click trúng checkbox → chỉ chọn dòng, để checkbox tự toggle tick. Giống Shopee Scrape.
    /// </summary>
    private void TargetRow_Click(object sender, MouseButtonEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        var item = FindAncestor<ListBoxItem>(src);
        if (item?.DataContext is not UpdateRunTargetViewModel vm) return;

        // Luôn CHỌN dòng (hiện cấu hình bên phải) — cho cả click checkbox lẫn thân dòng.
        item.IsSelected = true;

        // Click trúng checkbox → để checkbox TỰ toggle (đừng toggle thêm kẻo huỷ lẫn nhau).
        if (FindAncestor<CheckBox>(src) is not null) return;

        // Click thân dòng → TOGGLE tick.
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
