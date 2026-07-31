using System.Collections.Concurrent;
using System.Windows.Media;

namespace Shopee.Suite.Infrastructure;

/// <summary>
/// Brush dùng trong VIEWMODEL (cột trạng thái, pill tiến độ, màu theo job…). MỌI brush do ViewModel tạo
/// PHẢI đi qua đây vì WPF chỉ cho phép chạm <see cref="Freezable"/> từ luồng tạo ra nó — brush dựng trên
/// luồng nền mà không <c>Freeze()</c> sẽ ném "must be created on the same thread" lúc render (build KHÔNG
/// bắt được lỗi này). <see cref="From"/> parse + Freeze + cache theo mã màu nên gọi bao nhiêu lần cũng rẻ.
/// </summary>
public static class AppBrushes
{
    private static readonly ConcurrentDictionary<string, SolidColorBrush> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Brush đặc từ mã màu "#RRGGBB"/"#AARRGGBB" — đã Freeze, dùng được từ mọi luồng.</summary>
    public static SolidColorBrush From(string hex) => Cache.GetOrAdd(hex, h =>
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(h));
        brush.Freeze();
        return brush;
    });

    /// <summary>Brush đặc từ 3 kênh RGB — đã Freeze.</summary>
    public static SolidColorBrush From(byte r, byte g, byte b) =>
        From($"#{r:X2}{g:X2}{b:X2}");

    // ══════════ Brush NGỮ NGHĨA dùng chung cho ViewModel ══════════

    /// <summary>Xanh lá — trạng thái tốt / đang chạy ổn.</summary>
    public static SolidColorBrush Success { get; } = From("#00783C");

    /// <summary>Xanh dương — trạng thái "đã xong / đã dùng".</summary>
    public static SolidColorBrush Accent { get; } = From("#0078D7");

    /// <summary>Đỏ — lỗi / cảnh báo nặng.</summary>
    public static SolidColorBrush Danger { get; } = From("#C8463C");

    /// <summary>Xám — trạng thái trung tính / chưa dùng.</summary>
    public static SolidColorBrush Muted { get; } = From("#6E727A");

    /// <summary>Trong suốt (đã Freeze sẵn của WPF) — nền dòng mặc định.</summary>
    public static Brush Transparent => Brushes.Transparent;

    /// <summary>Đen (đã Freeze sẵn của WPF) — chữ mặc định của pill.</summary>
    public static Brush Black => Brushes.Black;
}
