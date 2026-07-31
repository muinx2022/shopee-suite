using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Shopee.Suite.Infrastructure;

/// <summary>
/// Ép cửa sổ nằm gọn trong VÙNG LÀM VIỆC (WorkingArea = màn hình trừ taskbar) của màn chứa nó, dành cho máy
/// có màn nhỏ hơn kích thước khai trong XAML (điển hình 1440×900 @100% → WorkingArea ~1440×860). Chỉ THU
/// NHỎ, không bao giờ phóng to trên màn lớn; máy màn to mở y như cũ, không đụng gì.
/// <para>
/// WPF không có API vùng làm việc theo màn chứa cửa sổ → dùng <see cref="System.Windows.Forms.Screen"/>
/// (csproj bật UseWindowsForms chỉ vì việc này). WorkingArea là PIXEL VẬT LÝ còn Width/Height/Left/Top của
/// WPF là DIP → mọi phép so sánh phải chia cho tỉ lệ DPI.
/// </para>
/// </summary>
public static class WindowFit
{
    /// <summary>Lề an toàn mỗi phía (DIP) để cửa sổ không dính sát mép màn / taskbar.</summary>
    private const double SafeMargin = 8;

    /// <summary>
    /// Gắn clamp chạy ĐÚNG MỘT LẦN lúc cửa sổ mở (gọi trong ctor, sau InitializeComponent). Phải đợi
    /// <see cref="Window.SourceInitialized"/> vì lúc ctor cửa sổ chưa có HWND → chưa biết nó nằm ở màn nào.
    /// </summary>
    /// <param name="maximizeIfTooSmall">
    /// true (cửa sổ CHÍNH) → màn không chứa nổi kích thước XAML thì sau khi kẹp còn maximize luôn cho dùng
    /// hết chỗ; false (cửa sổ phụ) → chỉ kẹp, giữ dáng cửa sổ thường.
    /// </param>
    public static void FitOnOpen(this Window window, bool maximizeIfTooSmall = false)
    {
        void OnSourceInitialized(object? sender, EventArgs e)
        {
            window.SourceInitialized -= OnSourceInitialized;   // một lần duy nhất: user tự kéo/khôi phục sau đó thì kệ user
            window.FitToWorkingArea(maximizeIfTooSmall);
        }

        window.SourceInitialized += OnSourceInitialized;
    }

    /// <summary>
    /// Thu cửa sổ về vừa WorkingArea rồi canh giữa trong đó. API màn hình lỗi (remote desktop, cấu hình lạ)
    /// → bỏ qua, giữ nguyên kích thước XAML, KHÔNG chặn mở app.
    /// </summary>
    public static void FitToWorkingArea(this Window window, bool maximizeIfTooSmall = false)
    {
        try
        {
            if (window.WindowState != WindowState.Normal) return;                     // đang maximize/minimize → thôi
            if (double.IsNaN(window.Width) || double.IsNaN(window.Height)) return;    // cửa sổ SizeToContent → không đụng

            var handle = new WindowInteropHelper(window).Handle;
            var screen = handle != IntPtr.Zero
                ? System.Windows.Forms.Screen.FromHandle(handle)                       // màn CHỨA cửa sổ…
                : System.Windows.Forms.Screen.PrimaryScreen;                           // …không có HWND thì màn chính
            if (screen is null) return;

            var working = screen.WorkingArea;
            var scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
            if (scale <= 0) scale = 1;

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
            // Manual để WPF không canh lại lần nữa bằng số cũ.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = working.X / scale + Math.Max(0, (working.Width / scale - w) / 2);
            window.Top = working.Y / scale + Math.Max(0, (working.Height / scale - h) / 2);

            // Màn nhỏ (đến mức phải kẹp) thì dùng hết chỗ luôn — không ngưỡng số cứng, cứ kẹp thật là maximize.
            if (maximizeIfTooSmall) window.WindowState = WindowState.Maximized;
        }
        catch
        {
            // API màn hình lỗi → giữ kích thước XAML. Cửa sổ có thể tràn nhưng app vẫn mở được.
        }
    }
}
