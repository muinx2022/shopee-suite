using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.App.Converters;

/// <summary>
/// Chuyển <see cref="AccountStatus"/> + tham số (bg/border/text) thành <see cref="SolidColorBrush"/>
/// cho badge pill trạng thái ở màn chi tiết tài khoản.
/// </summary>
public class StatusPillConverter : IValueConverter
{
    public static readonly StatusPillConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var role = parameter?.ToString() ?? "bg";
        var status = value is AccountStatus s ? s : AccountStatus.ChuaKiemTra;

        var hex = (status, role) switch
        {
            (AccountStatus.ChuaKiemTra, "bg") => "#FFF4E5",
            (AccountStatus.ChuaKiemTra, "border") => "#FFD9A8",
            (AccountStatus.ChuaKiemTra, "text") => "#B8720A",

            (AccountStatus.HoatDong, "bg") => "#E9F7EF",
            (AccountStatus.HoatDong, "border") => "#A8E6C1",
            (AccountStatus.HoatDong, "text") => "#1E7E45",

            (AccountStatus.BiKhoa, "bg") => "#FDECEA",
            (AccountStatus.BiKhoa, "border") => "#F5B5AD",
            (AccountStatus.BiKhoa, "text") => "#B4231A",

            _ => "#FFF4E5"
        };

        return new SolidColorBrush(Color.Parse(hex));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
