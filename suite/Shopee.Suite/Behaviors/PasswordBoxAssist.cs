using System.Windows;
using System.Windows.Controls;

namespace Shopee.Suite.Behaviors;

/// <summary>
/// Cho phép BIND <see cref="PasswordBox"/>.Password (WPF cố ý không cho bind trực tiếp vì lý do bảo mật).
/// Dùng: <c>&lt;PasswordBox b:PasswordBoxAssist.BoundPassword="{Binding Password, Mode=TwoWay}" /&gt;</c>.
/// </summary>
public static class PasswordBoxAssist
{
    public static readonly DependencyProperty BoundPasswordProperty = DependencyProperty.RegisterAttached(
        "BoundPassword", typeof(string), typeof(PasswordBoxAssist),
        new FrameworkPropertyMetadata("", OnBoundPasswordChanged));

    private static readonly DependencyProperty AttachedProperty = DependencyProperty.RegisterAttached(
        "Attached", typeof(bool), typeof(PasswordBoxAssist), new PropertyMetadata(false));

    public static string GetBoundPassword(DependencyObject o) => (string)o.GetValue(BoundPasswordProperty);
    public static void SetBoundPassword(DependencyObject o, string v) => o.SetValue(BoundPasswordProperty, v);

    private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box) return;
        if (!(bool)box.GetValue(AttachedProperty))
        {
            box.SetValue(AttachedProperty, true);
            box.PasswordChanged += (_, _) => SetBoundPassword(box, box.Password);
        }
        var pw = (string)e.NewValue ?? "";
        if (box.Password != pw) box.Password = pw;   // đồng bộ khi VM đổi (vd đổi tk đang chọn), tránh vòng lặp
    }
}
