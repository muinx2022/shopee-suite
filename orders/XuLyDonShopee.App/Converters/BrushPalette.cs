using System;
using System.Collections.Concurrent;
using System.Windows.Media;

namespace XuLyDonShopee.App.Converters;

/// <summary>
/// Brush theo mã màu cho các converter pill/badge — parse + <see cref="Freezable.Freeze"/> + cache.
/// PHẢI Freeze: converter có thể chạy khi binding cập nhật từ luồng nền, brush chưa đóng băng sẽ ném
/// "must be created on the same thread" lúc render (build không bắt được lỗi này).
/// </summary>
internal static class BrushPalette
{
    private static readonly ConcurrentDictionary<string, SolidColorBrush> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static SolidColorBrush From(string hex) => Cache.GetOrAdd(hex, h =>
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(h));
        brush.Freeze();
        return brush;
    });
}
