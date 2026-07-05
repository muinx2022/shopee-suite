using System.Globalization;
using Avalonia.Data.Converters;

namespace Shopee.Suite.Infrastructure;

/// <summary>int (count) → bool (>0). Dùng cho IsVisible="{Binding Count, Converter={StaticResource CountToBool}}".
/// (bool → ẩn/hiện dùng thẳng IsVisible="{Binding X}" / "{Binding !X}", không cần converter.)</summary>
public sealed class CountToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int n && n > 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>string → bool (có nội dung = true). Dùng cho IsVisible theo chuỗi rỗng/null.</summary>
public sealed class StringToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        !string.IsNullOrWhiteSpace(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
