using System.Windows;

namespace Shopee.Suite.Behaviors;

/// <summary>
/// Huy hiệu "✓ đã xong" đè góc trên-phải một nút thao tác (4 cột op của lưới shop ở màn Workspace).
/// <para>
/// Sinh ra ở đợt G để 4 cột SCRAPE · IMPORT · UPDATE · TÊN SP dùng CHUNG một template: phần vẽ huy hiệu nằm
/// trong <c>ControlTemplate</c> của style <c>wsAction</c> (WorkspaceView.xaml), còn hai thứ RIÊNG của từng cột
/// — cờ "đã xong" và tooltip của huy hiệu — truyền vào bằng hai attached property này:
/// </para>
/// <code>
/// &lt;Button Style="{StaticResource wsAction}"
///         b:OpBadgeAssist.Done="{Binding ScrapeDone}"
///         b:OpBadgeAssist.Tip="Scrape đã xong (Hub)" /&gt;
/// </code>
/// <para>
/// Thuần hiển thị: KHÔNG gắn handler, không đụng nghiệp vụ — template tự đọc bằng
/// <c>TemplateBinding</c> + <c>Trigger</c>. Dùng <c>TemplateBinding</c> (không phải
/// <c>{Binding (b:…), RelativeSource=TemplatedParent}</c>) vì path dạng chuỗi có prefix chỉ phân giải LÚC CHẠY
/// theo xmlns của file khai chỗ dùng — bài học đã ghi ở <see cref="WatermarkAssist"/>.
/// </para>
/// </summary>
public static class OpBadgeAssist
{
    /// <summary>Op của dòng này đã được đánh dấu xong (sổ Hub) → hiện huy hiệu ✓.</summary>
    public static readonly DependencyProperty DoneProperty =
        DependencyProperty.RegisterAttached("Done", typeof(bool), typeof(OpBadgeAssist),
            new PropertyMetadata(false));

    public static bool GetDone(DependencyObject d) => (bool)d.GetValue(DoneProperty);
    public static void SetDone(DependencyObject d, bool value) => d.SetValue(DoneProperty, value);

    /// <summary>Tooltip RIÊNG của huy hiệu ✓ (khác tooltip của nút — nút nói "bấm để chạy/dừng").</summary>
    public static readonly DependencyProperty TipProperty =
        DependencyProperty.RegisterAttached("Tip", typeof(string), typeof(OpBadgeAssist),
            new PropertyMetadata(null));

    public static string? GetTip(DependencyObject d) => (string?)d.GetValue(TipProperty);
    public static void SetTip(DependencyObject d, string? value) => d.SetValue(TipProperty, value);
}
