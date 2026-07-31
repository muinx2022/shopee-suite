using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Shopee.Suite.Infrastructure;

// ══════════════════════════════════════════════════════════════════════════════════════════════════
// Converter dùng chung, khai MỘT LẦN ở App.xaml với key cố định:
//     BoolToVis   — bool → Visible/Collapsed (dùng BooleanToVisibilityConverter có sẵn của WPF)
//     InvBool     — bool → bool đảo (WPF không có toán tử phủ định trong biểu thức binding)
//     InvBoolToVis— bool → Collapsed/Visible (thay `IsVisible="{Binding !X}"`)
//     CountToVis  — int  → Visible khi > 0
//     StringToVis — string → Visible khi có nội dung
//     WiderThan   — double (ActualWidth) → Visible khi ≥ ngưỡng (ConverterParameter)
// WPF không có `IsVisible` (chỉ có Visibility 3 trạng thái) nên MỌI chỗ ẩn/hiện đều đi qua đây.
// ══════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>bool → bool đảo (hai chiều).</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not bool b || !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not bool b || !b;
}

/// <summary>bool → Collapsed khi true (ngược với BooleanToVisibilityConverter).</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>int (count) → Visible khi &gt; 0. Dùng cho Visibility="{Binding Count, Converter={StaticResource CountToVis}}".</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>string → Visible khi có nội dung (ẩn khi rỗng/null/toàn khoảng trắng).</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// double (ActualWidth) → Visible khi RỘNG HƠN ngưỡng truyền qua ConverterParameter, ngược lại Collapsed.
/// Thay truy vấn theo bề rộng container (WPF không có): thanh trạng thái hẹp thì tự ẩn các đoạn phụ.
/// </summary>
public sealed class WiderThanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var width = value is double d ? d : 0;
        var threshold = parameter is null ? 0
            : double.TryParse(System.Convert.ToString(parameter, CultureInfo.InvariantCulture),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : 0;
        return width >= threshold ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
