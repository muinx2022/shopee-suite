using System.Windows;
using System.Windows.Controls;

namespace Shopee.Suite.Behaviors;

/// <summary>
/// Gợi ý mờ trong ô nhập rỗng (WPF không có sẵn <c>Watermark</c>/placeholder). Dùng:
/// <c>&lt;TextBox b:WatermarkAssist.Watermark="Tìm theo mã đơn…" /&gt;</c>.
/// <para>
/// Template TextBox (Themes/Theme.xaml) hiện <c>PART_Watermark</c> khi <see cref="HasTextProperty"/> = false
/// VÀ ô chưa được focus. <see cref="HasTextProperty"/> là thuộc tính CHỈ-ĐỌC do đây tự cập nhật theo
/// <see cref="TextBox.TextChanged"/> — view không set tay.
/// </para>
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

        // Gỡ trước rồi gắn lại: gán Watermark nhiều lần (đổi ngôn ngữ / template lại) không nhân đôi handler.
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
