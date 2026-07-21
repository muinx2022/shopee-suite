using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace XuLyDonShopee.App.Converters;

/// <summary>
/// Lấy ký tự đầu tiên (viết hoa) của chuỗi email/tên đăng nhập để hiển thị trong avatar.
/// Chuỗi rỗng → "?".
/// </summary>
public class InitialConverter : IValueConverter
{
    public static readonly InitialConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value?.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return "?";
        }

        return char.ToUpper(text.Trim()[0], CultureInfo.InvariantCulture).ToString();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
