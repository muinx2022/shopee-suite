using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace XuLyDonShopee.App.Converters;

// ══════════════════════════════════════════════════════════════════════════════════════════════════
// Converter ẩn/hiện + ngày, khai MỘT LẦN ở Styles/ModuleResources.xaml (mỗi view/dialog của module merge
// file đó vào Resources riêng) với key cố định — TRÙNG TÊN với bộ của suite ở App.xaml là CỐ Ý: cùng ngữ
// nghĩa, và bản của module gần hơn trong cây tài nguyên nên thắng trong phạm vi module.
//     BoolToVis    — bool → Visible/Collapsed (BooleanToVisibilityConverter có sẵn của WPF)
//     InvBoolToVis — bool → Collapsed/Visible (thay `IsVisible="{Binding !X}"` của Avalonia)
//     StringToVis  — string → Visible khi có nội dung (thay `StringConverters.IsNotNullOrEmpty`)
//     EmptyToVis   — string → Visible khi RỖNG (gợi ý mờ phủ trên ô lọc shop)
//     NullToVis    — object → Visible khi NULL      (thay `ObjectConverters.IsNull`)
//     NotNullToVis — object → Visible khi KHÁC NULL (thay `ObjectConverters.IsNotNull`)
//     DateOffset   — DateTimeOffset ↔ DateTime? (WPF DatePicker.SelectedDate là DateTime?)
// WPF không có `IsVisible` (chỉ Visibility 3 trạng thái) nên MỌI chỗ ẩn/hiện đều đi qua đây.
// ══════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>bool → Collapsed khi true (ngược với BooleanToVisibilityConverter).</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

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

/// <summary>string → Visible khi RỖNG (ngược với <see cref="StringToVisibilityConverter"/>). Dùng cho gợi ý
/// mờ phủ trên ô "gõ để lọc shop" (ComboBox IsEditable không chèn được PART_Watermark).</summary>
public sealed class StringEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>object → Visible khi giá trị là NULL (thay <c>ObjectConverters.IsNull</c> của Avalonia).</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>object → Visible khi giá trị KHÁC NULL (thay <c>ObjectConverters.IsNotNull</c>).</summary>
public sealed class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// <see cref="DateTimeOffset"/> (kiểu của <c>AccountsViewModel.ResultDate</c>) ↔ <see cref="DateTime"/>?
/// (kiểu của <c>DatePicker.SelectedDate</c> ở WPF). Không có converter thì binding đổ
/// "System.Windows.Data Error" vì WPF không tự đổi được 2 kiểu này.
/// <para>
/// XÓA TRẮNG lịch (SelectedDate = null) trả <see cref="Binding.DoNothing"/> → KHÔNG ghi đè
/// <c>ResultDate</c> (property không-null), giữ đúng hành vi bản Avalonia đã ghi chú trong AccountsView.
/// </para>
/// </summary>
public sealed class DateTimeOffsetConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            DateTimeOffset dto => dto.DateTime,
            DateTime dt => dt,
            _ => null,
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTime dt ? new DateTimeOffset(dt) : Binding.DoNothing;
}
