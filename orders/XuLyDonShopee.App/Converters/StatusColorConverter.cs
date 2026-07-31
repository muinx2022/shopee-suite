using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.App.Converters;

/// <summary>
/// Chuyển trạng thái tài khoản thành màu nền badge.
/// </summary>
public class StatusColorConverter : IValueConverter
{
    public static readonly StatusColorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            AccountStatus.HoatDong => BrushPalette.From("#16A34A"),    // xanh lá
            AccountStatus.BiKhoa => BrushPalette.From("#DC2626"),      // đỏ
            AccountStatus.ChuaKiemTra => BrushPalette.From("#F5A623"), // amber
            _ => BrushPalette.From("#F5A623")
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
