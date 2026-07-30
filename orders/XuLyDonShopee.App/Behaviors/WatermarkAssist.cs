using System.Windows;
using System.Windows.Controls;

namespace XuLyDonShopee.App.Behaviors;

/// <summary>
/// Gợi ý mờ trong ô nhập rỗng — thay <c>TextBox.Watermark</c> của Avalonia (WPF không có sẵn placeholder).
/// Dùng: <c>&lt;TextBox b:WatermarkAssist.Watermark="Tìm theo email…" Style="{StaticResource bare}" /&gt;</c>.
/// <para>
/// Bản RIÊNG của module Đơn hàng (không tham chiếu được <c>Shopee.Suite.Behaviors.WatermarkAssist</c> —
/// chiều ref là suite → orders). Cơ chế y hệt bản suite: template của style <c>bare</c>
/// (<c>Styles/Controls.xaml</c>) hiện <c>PART_Watermark</c> khi <see cref="HasTextProperty"/> = false VÀ ô
/// chưa được focus. <see cref="HasTextProperty"/> CHỈ-ĐỌC, tự cập nhật theo
/// <see cref="TextBox.TextChanged"/> — view không set tay.
/// </para>
/// <para>CHỈ áp cho <see cref="TextBox"/>. Ô "gõ để lọc shop" (ComboBox IsEditable, thay AutoCompleteBox của
/// Avalonia) dùng template mặc định của WPF nên không chèn được PART_Watermark — màn Đơn hàng đặt gợi ý
/// bằng một TextBlock phủ, ẩn/hiện theo chuỗi đang lọc.</para>
/// </summary>
public static class WatermarkAssist
{
    public static readonly DependencyProperty WatermarkProperty =
        DependencyProperty.RegisterAttached("Watermark", typeof(string), typeof(WatermarkAssist),
            new PropertyMetadata(null, OnWatermarkChanged));

    public static string? GetWatermark(DependencyObject d) => (string?)d.GetValue(WatermarkProperty);
    public static void SetWatermark(DependencyObject d, string? value) => d.SetValue(WatermarkProperty, value);

    private static readonly DependencyPropertyKey HasTextKey =
        DependencyProperty.RegisterAttachedReadOnly("HasText", typeof(bool), typeof(WatermarkAssist),
            new PropertyMetadata(false));

    /// <summary>true khi ô đang có chữ → template ẩn gợi ý.</summary>
    public static readonly DependencyProperty HasTextProperty = HasTextKey.DependencyProperty;

    public static bool GetHasText(DependencyObject d) => (bool)d.GetValue(HasTextProperty);

    private static void OnWatermarkChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;

        // Gỡ trước rồi gắn lại: gán Watermark nhiều lần không nhân đôi handler.
        box.TextChanged -= OnTextChanged;
        if (e.NewValue is string s && s.Length > 0) box.TextChanged += OnTextChanged;
        UpdateHasText(box);
    }

    private static void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box) UpdateHasText(box);
    }

    private static void UpdateHasText(TextBox box) =>
        box.SetValue(HasTextKey, !string.IsNullOrEmpty(box.Text));
}
