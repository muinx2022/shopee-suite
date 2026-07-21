using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace XuLyDonShopee.App.Converters;

/// <summary>
/// Hiển thị DateTime (lưu dạng UTC) theo giờ địa phương, định dạng dd/MM/yyyy HH:mm.
/// </summary>
public class DateTimeDisplayConverter : IValueConverter
{
    public static readonly DateTimeDisplayConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DateTime dt && dt != default)
        {
            return dt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        }
        return string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
