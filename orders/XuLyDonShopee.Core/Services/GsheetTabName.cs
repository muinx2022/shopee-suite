using System.Globalization;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Tên tab (sheet) Google Sheet theo tháng: <c>"Tháng MM-yyyy"</c> (vd <c>"Tháng 07-2026"</c>).
/// Dùng <see cref="CultureInfo.InvariantCulture"/> để định dạng KHÔNG lệ thuộc locale máy — lịch
/// Phật (th-TH) / Hồi (ar-SA) hay chữ số Ả-Rập không được làm sai tháng/năm hay đổi ký tự số.
/// </summary>
public static class GsheetTabName
{
    /// <summary>Tên tab cho tháng chứa <paramref name="date"/> — luôn 2 chữ số tháng + 4 chữ số năm.</summary>
    public static string ForMonth(DateTime date)
        => "Tháng " + date.ToString("MM-yyyy", CultureInfo.InvariantCulture);
}
