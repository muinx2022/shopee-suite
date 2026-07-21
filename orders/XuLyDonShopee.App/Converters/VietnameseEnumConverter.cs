using System;
using System.Globalization;
using Avalonia.Data.Converters;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.App.Converters;

/// <summary>
/// Chuyển giá trị enum (AccountStatus, ProxyType, ProxyStatus) sang chuỗi tiếng Việt để hiển thị.
/// </summary>
public class VietnameseEnumConverter : IValueConverter
{
    public static readonly VietnameseEnumConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            AccountStatus a => ToText(a),
            ProxyType t => ToText(t),
            ProxyStatus s => ToText(s),
            _ => value?.ToString() ?? string.Empty
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    public static string ToText(AccountStatus status) => status switch
    {
        AccountStatus.ChuaKiemTra => "Chưa kiểm tra",
        AccountStatus.HoatDong => "Đang hoạt động",
        AccountStatus.BiKhoa => "Bị khóa",
        _ => status.ToString()
    };

    public static string ToText(ProxyType type) => type switch
    {
        ProxyType.Http => "HTTP",
        ProxyType.Socks5 => "SOCKS5",
        _ => type.ToString()
    };

    public static string ToText(ProxyStatus status) => status switch
    {
        ProxyStatus.ChuaKiemTra => "Chưa kiểm tra",
        ProxyStatus.Song => "Sống",
        ProxyStatus.Chet => "Chết",
        _ => status.ToString()
    };
}
