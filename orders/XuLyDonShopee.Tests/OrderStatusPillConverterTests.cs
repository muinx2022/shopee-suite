using System.Globalization;
using Avalonia.Media;
using XuLyDonShopee.App.Converters;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test <see cref="OrderStatusPillConverter"/>: khóa MÀU (qua vai trò "text") mà mỗi nhóm trạng thái trả về
/// theo TỪ KHÓA — gồm ca "Giao hàng không thành công" → đỏ (không dính "thành công" của nhánh Done) và
/// "Giao hàng thành công" → xanh lá; và xác nhận cùng một trạng thái cho ra màu khác nhau giữa vai trò
/// "bg" và "text". Chạy headless: converter chỉ dựng <see cref="SolidColorBrush"/>, không cần khởi tạo app.
/// </summary>
public class OrderStatusPillConverterTests
{
    /// <summary>Gọi converter với vai trò cho trước và lấy <see cref="Color"/> của brush trả về.</summary>
    private static Color ColorFor(string? status, string role)
        => ((SolidColorBrush)OrderStatusPillConverter.Instance
                .Convert(status, typeof(IBrush), role, CultureInfo.InvariantCulture)!).Color;

    [Theory]
    // Hủy / trả hàng / hoàn tiền / giao thất bại → đỏ
    [InlineData("Đã hủy", "#B4231A")]
    [InlineData("Đã hủy một phần", "#B4231A")]
    [InlineData("Trả hàng/Hoàn tiền", "#B4231A")]
    [InlineData("Giao hàng không thành công", "#B4231A")]
    // Đã giao / hoàn thành → xanh lá ("Giao hàng thành công" KHÔNG rơi vào nhóm đỏ)
    [InlineData("Hoàn thành", "#1E7E45")]
    [InlineData("Đã giao", "#1E7E45")]
    [InlineData("Giao hàng thành công", "#1E7E45")]
    // Đang giao / vận chuyển → xanh dương
    [InlineData("Đang giao", "#1565C0")]
    [InlineData("Đang vận chuyển", "#1565C0")]
    // Chờ xử lý → amber
    [InlineData("Chờ lấy hàng", "#B8720A")]
    [InlineData("Chờ xác nhận", "#B8720A")]
    // Rỗng / null / không khớp từ khóa → xám trung tính
    [InlineData("", "#5A6169")]
    [InlineData(null, "#5A6169")]
    [InlineData("trạng thái lạ", "#5A6169")]
    public void Mau_chu_theo_tu_khoa_trang_thai(string? status, string expectedHex)
        => Assert.Equal(Color.Parse(expectedHex), ColorFor(status, "text"));

    [Fact]
    public void Cung_trang_thai_vai_tro_bg_khac_text()
    {
        var bg = ColorFor("Chờ lấy hàng", "bg");
        var text = ColorFor("Chờ lấy hàng", "text");
        Assert.NotEqual(bg, text);
    }
}
