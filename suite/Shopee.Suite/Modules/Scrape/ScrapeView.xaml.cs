using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Shopee.Suite.Modules.Scrape;

public partial class ScrapeView : UserControl
{
    public ScrapeView() => InitializeComponent();

    /// <summary>
    /// Click vào THÂN dòng → luôn CHỌN dòng (xem config bên phải). Khi RẢNH: thân dòng tự TICK (không
    /// untick). Khi ĐANG CHẠY: thân dòng CHỈ xem config, KHÔNG đổi tick (việc chạy/dừng đi qua checkbox).
    /// Click trúng checkbox → để <see cref="Checkbox_Click"/> xử lý.
    /// </summary>
    private void TargetRow_Click(object sender, MouseButtonEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        var item = FindAncestor<ListBoxItem>(src);
        if (item?.DataContext is not ScrapeTargetViewModel vm) return;

        // Luôn CHỌN dòng (hiện config bên phải) — cho cả click checkbox lẫn thân dòng, mọi trạng thái.
        item.IsSelected = true;

        // Click trúng checkbox → đã có Checkbox_Click lo (toggle khi rảnh / xác nhận khi đang chạy).
        if (FindAncestor<CheckBox>(src) is not null) return;

        // Click thân dòng: chỉ tự tick khi đang RẢNH. Đang chạy thì giữ nguyên tick (chỉ xem config).
        var busy = (Root.DataContext as ScrapeViewModel)?.IsBusy == true;
        if (!busy) vm.IsSelected = true;
    }

    /// <summary>
    /// Click checkbox: khi RẢNH để toggle mặc định (tick/untick để chọn chạy lần sau). Khi ĐANG CHẠY →
    /// chặn toggle mặc định, hỏi xác nhận rồi chạy/dừng RIÊNG tk đó (các tk khác không ảnh hưởng).
    /// </summary>
    private void Checkbox_Click(object sender, RoutedEventArgs e)
    {
        if (Root.DataContext is not ScrapeViewModel vm) return;
        if (sender is not CheckBox cb || cb.DataContext is not ScrapeTargetViewModel t) return;
        if (!vm.IsBusy) return;   // rảnh → để toggle mặc định + Persist chạy như cũ

        e.Handled = true;
        // ToggleButton đã LẬT IsChecked trước khi Click chạy → binding đã đẩy vào IsSelected. Hoàn lại
        // trạng thái TRƯỚC click; VM sẽ tự set lại IsSelected sau khi người dùng XÁC NHẬN.
        var preClick = !(cb.IsChecked == true);
        t.IsSelected = preClick;
        cb.IsChecked = preClick;
        _ = vm.ToggleAccountDuringRun(t);       // hỏi xác nhận → chạy/dừng riêng tk đó
    }

    /// <summary>Click 1 dòng trong lưới tiến trình → đưa cửa sổ Brave của process đó lên trước toàn bộ.</summary>
    private void Instance_Click(object sender, MouseButtonEventArgs e)
    {
        if (Root.DataContext is not ScrapeViewModel vm) return;
        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.Item is ScrapeInstanceViewModel inst)
            vm.BringInstanceToFront(inst);
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
