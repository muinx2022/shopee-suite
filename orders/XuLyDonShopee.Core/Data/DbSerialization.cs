using System.Globalization;

namespace XuLyDonShopee.Core.Data;

/// <summary>
/// Các hàm chuyển đổi dùng chung giữa các repository (ngày ISO-8601, enum lưu dạng TEXT).
/// </summary>
internal static class DbSerialization
{
    /// <summary>Chuyển DateTime sang chuỗi ISO-8601 (round-trip) để lưu DB.</summary>
    public static string FormatDate(DateTime value)
        => value.ToString("o", CultureInfo.InvariantCulture);

    /// <summary>Parse chuỗi ISO-8601 từ DB về DateTime.</summary>
    public static DateTime ParseDate(string value)
        => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>Parse enum lưu dạng TEXT, trả về giá trị mặc định nếu không hợp lệ.</summary>
    public static TEnum ParseEnum<TEnum>(object? value) where TEnum : struct, Enum
    {
        if (value is string s && Enum.TryParse<TEnum>(s, out var result))
        {
            return result;
        }
        return default;
    }
}
