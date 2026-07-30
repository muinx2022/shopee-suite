using Avalonia;
using Avalonia.Controls;

namespace Shopee.Suite.Infrastructure;

/// <summary>
/// Ép cửa sổ nằm gọn trong VÙNG LÀM VIỆC (WorkingArea = màn hình trừ taskbar) của màn chứa nó, dành cho máy
/// có màn nhỏ hơn kích thước khai trong XAML (điển hình 1440×900 @100% → WorkingArea ~1440×860). Chỉ THU
/// NHỎ, không bao giờ phóng to trên màn lớn; máy màn to mở y như cũ, không đụng gì.
/// </summary>
public static class WindowFit
{
    /// <summary>Lề an toàn mỗi phía (DIP) để cửa sổ không dính sát mép màn / taskbar.</summary>
    private const double SafeMargin = 8;

    /// <summary>
    /// Gắn clamp chạy ĐÚNG MỘT LẦN lúc cửa sổ mở (gọi trong ctor, sau InitializeComponent). Phải đợi
    /// <see cref="Window.Opened"/> vì lúc ctor cửa sổ chưa gắn platform → <c>Screens</c> còn null.
    /// </summary>
    /// <param name="maximizeIfTooSmall">
    /// true (cửa sổ CHÍNH) → màn không chứa nổi kích thước XAML thì sau khi kẹp còn maximize luôn cho dùng
    /// hết chỗ; false (cửa sổ phụ) → chỉ kẹp, giữ dáng cửa sổ thường.
    /// </param>
    public static void FitOnOpen(this Window window, bool maximizeIfTooSmall = false)
    {
        void OnOpened(object? sender, EventArgs e)
        {
            window.Opened -= OnOpened;   // một lần duy nhất: user tự kéo/khôi phục sau đó thì kệ user
            window.FitToWorkingArea(maximizeIfTooSmall);
        }

        window.Opened += OnOpened;
    }

    /// <summary>
    /// Thu cửa sổ về vừa WorkingArea rồi canh giữa trong đó. Screens chưa sẵn sàng / API lỗi (remote desktop,
    /// nền tảng lạ) → bỏ qua, giữ nguyên kích thước XAML, KHÔNG chặn mở app.
    /// </summary>
    public static void FitToWorkingArea(this Window window, bool maximizeIfTooSmall = false)
    {
        try
        {
            if (window.WindowState != WindowState.Normal) return;                     // đang maximize/minimize → thôi
            if (double.IsNaN(window.Width) || double.IsNaN(window.Height)) return;    // cửa sổ SizeToContent → không đụng

            var screens = window.Screens;
            if (screens is null) return;
            var screen = screens.ScreenFromWindow(window) ?? screens.Primary;          // màn CHỨA cửa sổ, không phải luôn Primary
            if (screen is null) return;

            // WorkingArea là PIXEL còn Width/Height là DIP → mọi so sánh phải chia Scaling.
            var working = screen.WorkingArea;
            var scale = screen.Scaling > 0 ? screen.Scaling : 1;
            var maxW = Math.Max(window.MinWidth, working.Width / scale - SafeMargin * 2);
            var maxH = Math.Max(window.MinHeight, working.Height / scale - SafeMargin * 2);

            var w = Math.Min(window.Width, maxW);
            var h = Math.Min(window.Height, maxH);
            // Kích thước XAML đã lọt màn → không kẹp, không maximize, giữ nguyên cả vị trí CenterScreen.
            if (w >= window.Width && h >= window.Height) return;

            // KẸP TRƯỚC rồi mới maximize (thứ tự bắt buộc): kích thước "khôi phục" của cửa sổ chính là số ta
            // gán ở đây, nên user bấm nút khôi phục sẽ về bản đã vừa màn — không bật lại 1500×940 tràn màn.
            window.Width = w;
            window.Height = h;

            // CenterScreen đã canh theo kích thước CŨ → phải canh lại tay theo kích thước mới. Chuyển sang
            // Manual để Avalonia không canh lại lần nữa bằng số cũ.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint(
                working.X + (int)Math.Max(0, (working.Width - w * scale) / 2),
                working.Y + (int)Math.Max(0, (working.Height - h * scale) / 2));

            // Màn nhỏ (đến mức phải kẹp) thì dùng hết chỗ luôn — không ngưỡng số cứng, cứ kẹp thật là maximize.
            if (maximizeIfTooSmall) window.WindowState = WindowState.Maximized;
        }
        catch
        {
            // Screens API lỗi → giữ kích thước XAML. Cửa sổ có thể tràn nhưng app vẫn mở được.
        }
    }
}
