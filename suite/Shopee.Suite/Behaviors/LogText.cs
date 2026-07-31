using System.Collections;
using System.Collections.Specialized;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace Shopee.Suite.Behaviors;

/// <summary>
/// Gắn 1 collection log (ObservableCollection&lt;string&gt;) vào 1 <see cref="TextBox"/> read-only để hiển thị
/// mà VẪN chọn/copy được — thay ItemsControl chỉ xem. Tự nối dòng mới + cuộn cuối; Reset (Clear) thì xoá hết.
/// Dùng: <c>&lt;TextBox Style="{StaticResource log}" b:LogText.Source="{Binding LogLines}" /&gt;</c>.
/// </summary>
public static class LogText
{
    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.RegisterAttached("Source", typeof(IEnumerable), typeof(LogText),
            new PropertyMetadata(null, OnSourceChanged));

    public static IEnumerable? GetSource(DependencyObject e) => (IEnumerable?)e.GetValue(SourceProperty);
    public static void SetSource(DependencyObject e, IEnumerable? v) => e.SetValue(SourceProperty, v);

    // Giữ handler theo từng TextBox để gỡ đăng ký khi đổi nguồn (tránh rò + nối nhầm log của VM cũ).
    private static readonly DependencyProperty HandlerProperty =
        DependencyProperty.RegisterAttached("Handler", typeof(NotifyCollectionChangedEventHandler), typeof(LogText),
            new PropertyMetadata(null));

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;

        if (e.OldValue is INotifyCollectionChanged oldNotify &&
            box.GetValue(HandlerProperty) is NotifyCollectionChangedEventHandler oldHandler)
            oldNotify.CollectionChanged -= oldHandler;

        var items = e.NewValue as IEnumerable;
        Rebuild(box, items);

        if (e.NewValue is INotifyCollectionChanged notify)
        {
            // Nguồn giờ là LogBuffer CÓ TRẦN (tự cắt dòng đầu khi vượt trần) → mỗi lần đổi cứ dựng lại TOÀN BỘ
            // text từ tập hiện tại. Vì tập ≤ trần (500 dòng) nên rẻ, và xử lý ĐÚNG cả khi cắt-đầu (Remove) —
            // trước đây chỉ nối thêm khi Add, bỏ qua Remove → text vẫn phình vô hạn dù collection đã cắt.
            void Handler(object? _, NotifyCollectionChangedEventArgs args) => Rebuild(box, items);

            box.SetValue(HandlerProperty, (NotifyCollectionChangedEventHandler)Handler);
            notify.CollectionChanged += Handler;
        }
        else
        {
            box.SetValue(HandlerProperty, null);
        }
    }

    private static void Rebuild(TextBox box, IEnumerable? items)
    {
        var sb = new StringBuilder();
        if (items is not null)
            foreach (var line in items) sb.Append(line).Append('\n');
        box.Text = sb.ToString();
        box.CaretIndex = box.Text.Length;
        box.ScrollToEnd();   // luôn dán đáy: dòng mới nhất phải tự lộ ra
    }
}
