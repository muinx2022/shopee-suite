using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace Shopee.Suite.Behaviors;

/// <summary>
/// Gắn một collection log (ObservableCollection&lt;string&gt;) vào một <see cref="TextBox"/> read-only để
/// hiển thị log mà VẪN chọn/copy được (Ctrl+C, Ctrl+A, menu chuột phải Copy/Select All) — thay cho
/// ItemsControl chỉ xem. Tự nối dòng mới + cuộn xuống cuối; Reset (Clear) thì xoá hết.
/// Dùng: <c>&lt;TextBox Style="{StaticResource LogTextBox}" b:LogText.Source="{Binding LogLines}" /&gt;</c>
/// </summary>
public static class LogText
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(IEnumerable), typeof(LogText), new PropertyMetadata(null, OnSourceChanged));

    public static IEnumerable? GetSource(DependencyObject d) => (IEnumerable?)d.GetValue(SourceProperty);
    public static void SetSource(DependencyObject d, IEnumerable? value) => d.SetValue(SourceProperty, value);

    // Giữ handler theo từng TextBox để gỡ đăng ký khi đổi nguồn (tránh rò + nối nhầm log của VM cũ).
    private static readonly DependencyProperty HandlerProperty = DependencyProperty.RegisterAttached(
        "Handler", typeof(NotifyCollectionChangedEventHandler), typeof(LogText), new PropertyMetadata(null));

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box)
            return;

        if (e.OldValue is INotifyCollectionChanged oldNotify &&
            box.GetValue(HandlerProperty) is NotifyCollectionChangedEventHandler oldHandler)
            oldNotify.CollectionChanged -= oldHandler;

        box.Clear();

        if (e.NewValue is not IEnumerable items)
        {
            box.SetValue(HandlerProperty, null);
            return;
        }

        // Hiển thị lại các dòng đã có (vd quay lại module sau khi đã chạy).
        foreach (var line in items)
            box.AppendText(line + "\n");
        box.ScrollToEnd();

        if (e.NewValue is INotifyCollectionChanged notify)
        {
            void Handler(object? _, NotifyCollectionChangedEventArgs args)
            {
                if (args.Action == NotifyCollectionChangedAction.Reset)
                {
                    box.Clear();
                    return;
                }
                if (args.NewItems is null)
                    return;
                foreach (var item in args.NewItems)
                    box.AppendText(item + "\n");
                box.ScrollToEnd();
            }

            box.SetValue(HandlerProperty, (NotifyCollectionChangedEventHandler)Handler);
            notify.CollectionChanged += Handler;
        }
    }
}
