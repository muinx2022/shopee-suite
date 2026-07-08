using System.Collections;
using System.Collections.Specialized;
using System.Text;
using Avalonia;
using Avalonia.Controls;

namespace Shopee.Suite.Behaviors;

/// <summary>
/// Gắn 1 collection log (ObservableCollection&lt;string&gt;) vào 1 <see cref="TextBox"/> read-only để hiển thị
/// mà VẪN chọn/copy được — thay ItemsControl chỉ xem. Tự nối dòng mới + cuộn cuối; Reset (Clear) thì xoá hết.
/// Avalonia TextBox không có AppendText/ScrollToEnd → set Text + CaretIndex ở cuối. Dùng:
/// <c>&lt;TextBox Classes="log" b:LogText.Source="{Binding LogLines}" /&gt;</c>.
/// </summary>
public class LogText : AvaloniaObject
{
    public static readonly AttachedProperty<IEnumerable?> SourceProperty =
        AvaloniaProperty.RegisterAttached<LogText, TextBox, IEnumerable?>("Source");

    public static IEnumerable? GetSource(TextBox e) => e.GetValue(SourceProperty);
    public static void SetSource(TextBox e, IEnumerable? v) => e.SetValue(SourceProperty, v);

    // Giữ handler theo từng TextBox để gỡ đăng ký khi đổi nguồn (tránh rò + nối nhầm log của VM cũ).
    private static readonly AttachedProperty<NotifyCollectionChangedEventHandler?> HandlerProperty =
        AvaloniaProperty.RegisterAttached<LogText, TextBox, NotifyCollectionChangedEventHandler?>("Handler");

    static LogText()
    {
        SourceProperty.Changed.AddClassHandler<TextBox>(OnSourceChanged);
    }

    private static void OnSourceChanged(TextBox box, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyCollectionChanged oldNotify &&
            box.GetValue(HandlerProperty) is { } oldHandler)
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
        SetText(box, sb.ToString());
    }

    private static void SetText(TextBox box, string text)
    {
        box.Text = text;
        box.CaretIndex = text.Length;   // đưa con trỏ về cuối → TextBox tự cuộn xuống
    }
}
