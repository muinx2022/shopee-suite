using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
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
            AccountStatus.HoatDong => new SolidColorBrush(Color.Parse("#16A34A")),    // xanh lá
            AccountStatus.BiKhoa => new SolidColorBrush(Color.Parse("#DC2626")),      // đỏ
            AccountStatus.ChuaKiemTra => new SolidColorBrush(Color.Parse("#F5A623")), // amber
            _ => new SolidColorBrush(Color.Parse("#F5A623"))
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
