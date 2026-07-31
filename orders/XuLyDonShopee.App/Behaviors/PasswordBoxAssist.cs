using System.Windows;
using System.Windows.Controls;

namespace XuLyDonShopee.App.Behaviors;

/// <summary>
/// Cho phép BIND <see cref="PasswordBox"/>.Password (WPF cố ý không cho bind trực tiếp) — bản RIÊNG của
/// module Đơn hàng (chiều ref là suite → orders nên không dùng chung được bản
/// <c>Shopee.Suite.Behaviors.PasswordBoxAssist</c>; logic giữ y hệt).
/// <para>
/// Mẫu chuẩn dùng CẶP attached property: bật <c>BindPassword=True</c> để GẮN SẴN handler (ngay cả khi mật
/// khẩu rỗng) + đồng bộ đầu, và <c>BoundPassword</c> để bind 2 chiều. Guard <c>Updating</c> chặn vòng lặp
/// feedback (gõ → ghi VM → đừng set lại box làm nhảy con trỏ). Nhờ gắn-sẵn + đồng-bộ-đầu, đổi tài khoản sẽ
/// reset đúng ô (không "dính" dấu chấm sang acc khác).
/// </para>
/// <para>Thay <c>TextBox PasswordChar="●" RevealPassword="{Binding …}"</c> của bản Avalonia: ở WPF, ô mật
/// khẩu là PasswordBox (che) đặt CHỒNG với một TextBox (hiện) — nút con mắt đổi Visibility giữa hai ô, cả
/// hai cùng bind một property của ViewModel.</para>
/// </summary>
public static class PasswordBoxAssist
{
    public static readonly DependencyProperty BoundPasswordProperty = DependencyProperty.RegisterAttached(
        "BoundPassword", typeof(string), typeof(PasswordBoxAssist),
        new FrameworkPropertyMetadata(string.Empty, OnBoundPasswordChanged));

    public static readonly DependencyProperty BindPasswordProperty = DependencyProperty.RegisterAttached(
        "BindPassword", typeof(bool), typeof(PasswordBoxAssist), new PropertyMetadata(false, OnBindPasswordChanged));

    private static readonly DependencyProperty UpdatingProperty = DependencyProperty.RegisterAttached(
        "Updating", typeof(bool), typeof(PasswordBoxAssist), new PropertyMetadata(false));

    public static string GetBoundPassword(DependencyObject d) => (string)d.GetValue(BoundPasswordProperty);
    public static void SetBoundPassword(DependencyObject d, string v) => d.SetValue(BoundPasswordProperty, v);
    public static bool GetBindPassword(DependencyObject d) => (bool)d.GetValue(BindPasswordProperty);
    public static void SetBindPassword(DependencyObject d, bool v) => d.SetValue(BindPasswordProperty, v);
    private static bool GetUpdating(DependencyObject d) => (bool)d.GetValue(UpdatingProperty);
    private static void SetUpdating(DependencyObject d, bool v) => d.SetValue(UpdatingProperty, v);

    // VM → box (đổi tài khoản / nạp giá trị). Bỏ qua khi đang cập nhật do người gõ (guard) để khỏi nhảy con trỏ.
    private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box || !GetBindPassword(box) || GetUpdating(box)) return;
        box.PasswordChanged -= HandlePasswordChanged;
        var np = (string)e.NewValue ?? "";
        if (box.Password != np) box.Password = np;
        box.PasswordChanged += HandlePasswordChanged;
    }

    // Bật/tắt bind: GẮN SẴN handler + đồng bộ đầu (để mật khẩu rỗng cũng hoạt động).
    private static void OnBindPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box) return;
        if ((bool)e.OldValue) box.PasswordChanged -= HandlePasswordChanged;
        if ((bool)e.NewValue)
        {
            box.Password = GetBoundPassword(box) ?? "";
            box.PasswordChanged += HandlePasswordChanged;
        }
    }

    // box → VM (người gõ). Guard Updating để việc set BoundPassword không kích OnBoundPasswordChanged set lại box.
    private static void HandlePasswordChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        SetUpdating(box, true);
        SetBoundPassword(box, box.Password);
        SetUpdating(box, false);
    }
}
