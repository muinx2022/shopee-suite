using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace XuLyDonShopee.App.Converters;

/// <summary>
/// Chuyển chuỗi trạng thái đơn (TỰ DO, quét từ trang bán hàng Shopee — vd "Chờ lấy hàng", "Chờ xác nhận",
/// "Đang giao", "Hoàn thành", "Đã hủy", "Đã hủy một phần", "Trả hàng/Hoàn tiền") + tham số vai trò
/// (<c>bg</c> / <c>border</c> / <c>text</c>) thành <see cref="SolidColorBrush"/> cho badge pill ở cột
/// "Trạng thái" màn Đơn hàng. Trạng thái là chuỗi tự do nên phân loại theo TỪ KHÓA (chứa) cho bền với
/// biến thể chữ; không khớp nhóm nào → xám trung tính. Cùng bảng màu pill với
/// <see cref="StatusPillConverter"/> (nền nhạt + viền mềm + chữ đậm) để đồng bộ giao diện.
/// </summary>
public class OrderStatusPillConverter : IValueConverter
{
    public static readonly OrderStatusPillConverter Instance = new();

    /// <summary>Nhóm màu suy ra từ từ khóa trạng thái.</summary>
    private enum Bucket
    {
        /// <summary>Chờ xử lý: Chờ lấy hàng / Chờ xác nhận / Chuẩn bị hàng — amber.</summary>
        Pending,

        /// <summary>Đang trên đường: Đang giao / Đang vận chuyển — xanh dương.</summary>
        InTransit,

        /// <summary>Đã xong: Đã giao / Hoàn thành — xanh lá.</summary>
        Done,

        /// <summary>Hủy / trả hàng / hoàn tiền / giao thất bại — đỏ.</summary>
        Cancelled,

        /// <summary>Không khớp từ khóa nào — xám trung tính.</summary>
        Unknown
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var role = parameter?.ToString() ?? "bg";

        var (bg, border, text) = Classify(value as string) switch
        {
            Bucket.Pending => ("#FFF4E5", "#FFD9A8", "#B8720A"),   // amber (giống "Chưa kiểm tra")
            Bucket.InTransit => ("#E7F1FD", "#B3D3F5", "#1565C0"), // xanh dương (giống nhãn "Chờ lấy: N")
            Bucket.Done => ("#E9F7EF", "#A8E6C1", "#1E7E45"),      // xanh lá (giống "Hoạt động")
            Bucket.Cancelled => ("#FDECEA", "#F5B5AD", "#B4231A"), // đỏ (giống "Bị khóa")
            _ => ("#F1F3F5", "#DEE2E6", "#5A6169")                 // xám trung tính
        };

        var hex = role switch
        {
            "border" => border,
            "text" => text,
            _ => bg
        };

        return new SolidColorBrush(Color.Parse(hex));
    }

    /// <summary>
    /// Phân loại trạng thái theo từ khóa (chuẩn hóa: bỏ hoa/thường + gộp khoảng trắng thừa). THỨ TỰ kiểm
    /// quan trọng: nhánh Cancelled xét TRƯỚC nhánh Done — "hủy" bắt cả "Đã hủy một phần"; "không thành công"
    /// / "thất bại" cho "Giao hàng không thành công" phải kiểm TRƯỚC để không dính "thành công" của nhánh
    /// Done (đỏ), trong khi "Giao hàng thành công" rơi xuống Done (xanh lá); "hoàn thành" tách khỏi
    /// "hoàn tiền"; "đã giao" (xong) tách khỏi "đang giao" (đang đi).
    /// </summary>
    private static Bucket Classify(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return Bucket.Unknown;
        }

        var s = string.Join(' ', status.ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        if (s.Contains("hủy") || s.Contains("trả hàng") || s.Contains("hoàn tiền") || s.Contains("hoàn trả")
            || s.Contains("không thành công") || s.Contains("thất bại"))
        {
            return Bucket.Cancelled;
        }

        if (s.Contains("hoàn thành") || s.Contains("đã giao") || s.Contains("thành công") || s.Contains("đã nhận"))
        {
            return Bucket.Done;
        }

        if (s.Contains("đang giao") || s.Contains("vận chuyển") || s.Contains("đang gửi") || s.Contains("đã gửi"))
        {
            return Bucket.InTransit;
        }

        if (s.Contains("chờ") || s.Contains("chuẩn bị") || s.Contains("xác nhận"))
        {
            return Bucket.Pending;
        }

        return Bucket.Unknown;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
