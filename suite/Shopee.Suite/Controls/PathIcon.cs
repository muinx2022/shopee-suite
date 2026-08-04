using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Shopee.Suite.Controls;

/// <summary>
/// Icon vector ĐƠN SẮC (path 24×24) tô theo <see cref="Control.Foreground"/> — WPF không có control icon
/// dạng này nên tự dựng. Giữ nguyên cú pháp dùng ở view
/// (<c>&lt;c:PathIcon Data="{StaticResource IconSave}" /&gt;</c>) và cơ chế "icon ăn theo màu chữ của nút"
/// mà theme đang dựa vào (style .primary/.danger/.success chỉ đổi Foreground của icon).
/// <para>
/// Template mặc định (Path + Stretch=Uniform, cỡ 12×12) khai trong <c>Themes/Theme.xaml</c> — merge ở cấp
/// Application nên mọi cửa sổ/việt dựng bằng code đều thấy.
/// </para>
/// </summary>
public sealed class PathIcon : Control
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(Geometry), typeof(PathIcon),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Hình vector cần vẽ (lấy từ <c>Themes/Icons.xaml</c> hoặc <c>AppIcons</c>).</summary>
    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }
}
