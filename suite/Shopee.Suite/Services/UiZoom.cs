using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Shopee.Core.Infrastructure;
using Shopee.Suite.Infrastructure;

namespace Shopee.Suite.Services;

/// <summary>
/// THU PHÓNG giao diện toàn app (Ctrl + / Ctrl − / Ctrl 0), kiểu trình duyệt.
/// <para>
/// Cách làm: MỘT <see cref="ScaleTransform"/> dùng chung gán vào <see cref="FrameworkElement.LayoutTransform"/>
/// của phần tử gốc từng cửa sổ. Đây là transform tầng LAYOUT (không phải RenderTransform) nên chữ, khoảng
/// cách, nút, lưới… đều đo lại theo tỉ lệ mới — chữ vẫn nét chứ không phóng ảnh. Vì transform dùng chung,
/// đổi <c>ScaleX/ScaleY</c> là mọi cửa sổ đang mở cập nhật ngay, không cần khởi động lại.
/// </para>
/// <para>
/// Gắn qua <see cref="EventManager.RegisterClassHandler(Type, RoutedEvent, Delegate)"/> cho
/// <c>typeof(Window)</c> nên MỌI cửa sổ WPF của tiến trình đều dính — kể cả hộp thoại của module đơn hàng
/// (<c>XuLyDonShopee.App</c>) vốn không biết gì về lớp này.
/// </para>
/// </summary>
public static class UiZoom
{
    /// <summary>Transform DÙNG CHUNG. <see cref="Transform"/> là Freezable nên gắn được cho nhiều phần tử —
    /// miễn KHÔNG bao giờ gọi Freeze() (freeze là hết đổi được mức).</summary>
    private static readonly ScaleTransform Scale = new(1, 1);

    /// <summary>Kích thước KHAI BÁO gốc của một cửa sổ phụ, chụp lần đầu cửa sổ được gắn. Mỗi lần đổi mức thì
    /// gán lại <c>gốc × zoom</c> (không nhân dồn). ConditionalWeakTable để cửa sổ đóng rồi thì tự thu hồi.</summary>
    private sealed record BaseSize(double Width, double Height, double MinWidth, double MinHeight,
                                   double MaxWidth, double MaxHeight);

    private static readonly ConditionalWeakTable<Window, BaseSize> Bases = new();

    /// <summary>Mức thu phóng hiện hành (1.0 = 100%).</summary>
    public static double Current { get; private set; } = UiZoomStore.Default;

    /// <summary>Bắn sau mỗi lần mức đổi — thanh trạng thái + màn Cài đặt nghe để cập nhật hiển thị.</summary>
    public static event Action? Changed;

    private static bool _installed;

    /// <summary>
    /// Cài một lần lúc app khởi động (TRƯỚC khi dựng cửa sổ chính): nạp mức đã lưu + đăng ký 2 class handler
    /// cho mọi <see cref="Window"/> (Loaded → gắn transform; PreviewKeyDown → phím tắt).
    /// </summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        Current = UiZoomStore.Shared.Current;
        Scale.ScaleX = Scale.ScaleY = Current;

        // Loaded là routed event DIRECT → class handler này chỉ chạy cho chính cửa sổ, không phải mọi phần tử con.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnWindowLoaded));

        // PreviewKeyDown TUNNEL từ gốc xuống → cửa sổ nhận trước TextBox/DataGrid, nên phím tắt vẫn ăn khi con
        // trỏ đang ở trong ô nhập liệu.
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(OnPreviewKeyDown), handledEventsToo: false);
    }

    /// <summary>Đặt mức thu phóng (kẹp vào dải hợp lệ), áp ngay cho mọi cửa sổ đang mở + lưu xuống đĩa.</summary>
    public static void Set(double zoom)
    {
        var target = UiZoomStore.Sanitize(zoom);
        if (Math.Abs(target - Current) < 1e-6) return;

        Current = target;
        Scale.ScaleX = Scale.ScaleY = target;

        foreach (var w in OpenWindows())
        {
            ApplyTextMode(w);
            ApplySize(w);
        }

        try { UiZoomStore.Shared.Save(target); } catch { /* không ghi được cấu hình thì vẫn phóng được phiên này */ }
        Changed?.Invoke();
    }

    /// <summary>Tăng (+1) / giảm (−1) một nấc.</summary>
    public static void Step(int direction) => Set(UiZoomStore.Next(Current, direction));

    /// <summary>Về 100%.</summary>
    public static void Reset() => Set(UiZoomStore.Default);

    /// <summary>Nhãn phần trăm hiện hành ("125%").</summary>
    public static string PercentText => UiZoomStore.Percent(Current);

    // ══════════════════ Gắn cho từng cửa sổ ══════════════════

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window w) return;
        try { Attach(w); } catch { /* không phóng được một cửa sổ thì thôi, đừng chết app */ }
    }

    private static void Attach(Window w)
    {
        if (w.Content is FrameworkElement root)
        {
            // Cửa sổ tự đặt LayoutTransform riêng (hiện chưa có) → tôn trọng, không ghi đè.
            if (root.LayoutTransform is null or MatrixTransform { Matrix.IsIdentity: true })
                root.LayoutTransform = Scale;
        }

        ApplyTextMode(w);

        if (IsMainWindow(w)) return;   // cửa sổ chính: chỉ phóng NỘI DUNG, không đụng kích thước khung

        if (!Bases.TryGetValue(w, out _))
            Bases.Add(w, new BaseSize(w.Width, w.Height, w.MinWidth, w.MinHeight, w.MaxWidth, w.MaxHeight));

        // HOÃN một nhịp: lúc Loaded, cửa sổ mới mở còn đang trong lượt tự-đo (SizeToContent) và sẽ GHI ĐÈ
        // Width/Height bằng kích thước HWND hiện tại — đặt kích thước ngay ở đây sẽ bị nuốt (đã đo: hộp thoại
        // Width 440 ở mức 130% ra 468 = đúng MinWidth mới, tức bản ghi đè bị kẹp lên). Chờ hàng đợi rảnh rồi
        // mới áp thì số của ta là số sau cùng.
        w.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try { ApplySize(w); } catch { }
        }));
    }

    /// <summary>
    /// Chế độ dựng chữ: <c>Display</c> khớp pixel nguyên (nét ở 100%) nhưng lệch nét khi nhân tỉ lệ lẻ →
    /// mức ≠ 100% chuyển sang <c>Ideal</c>. XAML khai Display, ta chỉ đè khi thật sự đang phóng.
    /// </summary>
    private static void ApplyTextMode(Window w) =>
        TextOptions.SetTextFormattingMode(w, Math.Abs(Current - 1.0) < 1e-6
            ? TextFormattingMode.Display
            : TextFormattingMode.Ideal);

    /// <summary>
    /// Nhân kích thước KHAI BÁO của cửa sổ phụ theo mức phóng rồi kẹp lại vào vùng làm việc màn hình. Cần vì
    /// hộp thoại hay khai cứng <c>Width="720"</c>: nội dung phóng to mà khung giữ nguyên thì bị cắt chữ.
    /// Cửa sổ CHÍNH không đụng tới (người dùng thường maximize; tự đổi kích thước sẽ nhảy lung tung).
    /// </summary>
    private static void ApplySize(Window w)
    {
        if (IsMainWindow(w) || !Bases.TryGetValue(w, out var b)) return;

        // THỨ TỰ BẮT BUỘC: Min/Max TRƯỚC, Width/Height SAU. Đặt ngược lại thì lúc THU NHỎ, Width mới (nhỏ) bị
        // MinWidth cũ (lớn) kẹp lên rồi ghi ngược vào property → cửa sổ không bao giờ nhỏ lại được.
        // NaN = cửa sổ SizeToContent chiều đó (tự co theo nội dung đã phóng — khỏi đụng).
        // Infinity = Max* mặc định (không giới hạn) — nhân vào cũng vô nghĩa.
        if (!double.IsNaN(b.MinWidth)) w.MinWidth = b.MinWidth * Current;
        if (!double.IsNaN(b.MinHeight)) w.MinHeight = b.MinHeight * Current;
        if (!double.IsNaN(b.MaxWidth) && !double.IsInfinity(b.MaxWidth)) w.MaxWidth = b.MaxWidth * Current;
        if (!double.IsNaN(b.MaxHeight) && !double.IsInfinity(b.MaxHeight)) w.MaxHeight = b.MaxHeight * Current;
        if (!double.IsNaN(b.Width)) w.Width = b.Width * Current;
        if (!double.IsNaN(b.Height)) w.Height = b.Height * Current;

        // Phóng to xong có thể tràn ra ngoài màn → kẹp lại (hàm này tự bỏ qua cửa sổ SizeToContent).
        w.FitToWorkingArea();
    }

    private static bool IsMainWindow(Window w) => ReferenceEquals(w, Application.Current?.MainWindow);

    private static IEnumerable<Window> OpenWindows()
    {
        var windows = Application.Current?.Windows;
        if (windows is null) yield break;
        foreach (var o in windows)
            if (o is Window w) yield return w;
    }

    // ══════════════════ Phím tắt ══════════════════

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Chỉ Ctrl (cho phép kèm Shift vì "+" gõ bằng Ctrl+Shift+=). Có Alt/Win → không phải phím của ta.
        var mods = Keyboard.Modifiers;
        if ((mods & ModifierKeys.Control) == 0) return;
        if ((mods & (ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return;

        switch (e.Key)
        {
            case Key.OemPlus or Key.Add:
                Step(+1); break;
            case Key.OemMinus or Key.Subtract:
                Step(-1); break;
            case Key.D0 or Key.NumPad0:
                Reset(); break;
            default:
                return;   // KHÔNG phải phím của ta → để nguyên cho app xử lý (Ctrl+1…4 chuyển tab…)
        }

        e.Handled = true;
    }
}
