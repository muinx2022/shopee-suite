using System.Windows;
using System.Windows.Media;

namespace Shopee.Suite.Infrastructure;

/// <summary>
/// Leo NGƯỢC cây từ một phần tử lên tổ tiên — thay <c>FindAncestorOfType&lt;T&gt;()</c> của Avalonia.
/// <para>
/// WPF có hai cây song song: phần tử vẽ được (<see cref="Visual"/>) đi bằng
/// <see cref="VisualTreeHelper.GetParent"/>, còn nội dung chữ (<c>Run</c>, <c>Hyperlink</c>… —
/// <see cref="FrameworkContentElement"/>) KHÔNG nằm trong cây visual nên phải nhảy qua
/// <see cref="FrameworkContentElement.Parent"/>. Bấm chuột vào chữ trong ô lưới cho ra
/// <c>OriginalSource</c> kiểu content element → thiếu bước bắc cầu này là mất luôn dòng lưới.
/// </para>
/// </summary>
public static class VisualTreeSearch
{
    /// <summary>Cha trực tiếp của <paramref name="node"/>, bắc cầu visual ⇄ content element; null khi hết cây.</summary>
    public static DependencyObject? GetParent(DependencyObject? node) => node switch
    {
        null => null,
        Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(node),
        _ => (node as FrameworkContentElement)?.Parent,
    };

    /// <summary>Tổ tiên gần nhất kiểu <typeparamref name="T"/> (tính cả chính <paramref name="node"/>); null nếu không có.</summary>
    public static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T) node = GetParent(node);
        return node as T;
    }
}
