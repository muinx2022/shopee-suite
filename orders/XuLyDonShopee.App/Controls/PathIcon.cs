using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace XuLyDonShopee.App.Controls;

/// <summary>
/// Icon vector ĐƠN SẮC (path 24×24) tô theo <see cref="Control.Foreground"/> — bản RIÊNG của module Đơn hàng.
/// <para>
/// Vì sao KHÔNG dùng chung <c>Shopee.Suite.Controls.PathIcon</c>: chiều tham chiếu project là
/// suite → orders (shell nạp DLL module), không được đảo lại. Bản này giữ y cú pháp dùng ở view
/// (<c>&lt;c:PathIcon Data="{DynamicResource IconSave}" /&gt;</c>) và cơ chế "icon ăn theo Foreground của nút"
/// mà các style <c>primary/success/danger</c> trong <c>Styles/Controls.xaml</c> dựa vào.
/// </para>
/// <para>
/// Template mặc định (Path + Stretch=Uniform, cỡ 12×12) khai trong <c>Styles/Controls.xaml</c> — file đó
/// được mỗi view/dialog của module merge vào Resources riêng (không đổ vào Application của suite).
/// </para>
/// </summary>
public sealed class PathIcon : Control
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(Geometry), typeof(PathIcon),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Hình vector cần vẽ (lấy từ kho icon dùng chung <c>Themes/Icons.xaml</c> của suite, merge ở
    /// cấp Application nên module tra được lúc chạy).</summary>
    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }
}
