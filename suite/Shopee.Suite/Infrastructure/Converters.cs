using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Shopee.Suite.Infrastructure;

/// <summary>bool → Visibility (true = Visible, false = Collapsed).</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object parameter, CultureInfo c) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type t, object parameter, CultureInfo c) =>
        value is Visibility.Visible;
}

/// <summary>bool đảo → Visibility (true = Collapsed, false = Visible).</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object parameter, CultureInfo c) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type t, object parameter, CultureInfo c) =>
        value is Visibility.Collapsed;
}

/// <summary>int (count) → Visibility (>0 = Visible, ngược lại Collapsed).</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object parameter, CultureInfo c) =>
        value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type t, object parameter, CultureInfo c) =>
        throw new NotSupportedException();
}
