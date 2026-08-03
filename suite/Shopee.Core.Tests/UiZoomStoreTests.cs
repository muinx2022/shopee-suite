using Shopee.Core.Infrastructure;

namespace Shopee.Core.Tests;

/// <summary>
/// Phần THUẦN SỐ HỌC của mức thu phóng giao diện (<see cref="UiZoomStore"/>): kẹp giá trị + nhảy nấc.
/// Tách khỏi WPF nên test được thẳng; tầng UI (<c>UiZoom</c>) chỉ việc gọi lại 2 hàm này nên sai ở đây là
/// phím tắt Ctrl +/−/0 sai theo.
/// KHÔNG chạm <c>Shared</c> (nó đọc/ghi %AppData%\ShopeeSuite của máy đang chạy).
/// </summary>
public sealed class UiZoomStoreTests
{
    [Fact]
    public void Steps_TangDan_VaChua100PhanTram()
    {
        Assert.NotEmpty(UiZoomStore.Steps);
        for (int i = 1; i < UiZoomStore.Steps.Length; i++)
            Assert.True(UiZoomStore.Steps[i] > UiZoomStore.Steps[i - 1], "Nấc phải sắp tăng dần");
        Assert.Contains(UiZoomStore.Default, UiZoomStore.Steps);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Sanitize_GiaTriHong_VeMacDinh(double? zoom)
    {
        Assert.Equal(UiZoomStore.Default, UiZoomStore.Sanitize(zoom));
    }

    [Fact]
    public void Sanitize_NgoaiDai_KepVeBien()
    {
        Assert.Equal(UiZoomStore.MinZoom, UiZoomStore.Sanitize(0.1));
        Assert.Equal(UiZoomStore.MaxZoom, UiZoomStore.Sanitize(99));
        Assert.Equal(1.15, UiZoomStore.Sanitize(1.15));   // trong dải → giữ nguyên
    }

    [Fact]
    public void Next_NhayDungNacKeTiep()
    {
        Assert.Equal(1.15, UiZoomStore.Next(1.0, +1));
        Assert.Equal(0.85, UiZoomStore.Next(1.0, -1));
        Assert.Equal(1.0, UiZoomStore.Next(1.0, 0));
    }

    [Fact]
    public void Next_HetDai_GiuNguyenBien()
    {
        Assert.Equal(UiZoomStore.MaxZoom, UiZoomStore.Next(UiZoomStore.MaxZoom, +1));
        Assert.Equal(UiZoomStore.MinZoom, UiZoomStore.Next(UiZoomStore.MinZoom, -1));
    }

    [Fact]
    public void Next_MucLe_NhayVeNacGanNhatTheoHuong()
    {
        // 1.20 nằm giữa nấc 1.15 và 1.3 (vd file cấu hình sửa tay).
        Assert.Equal(1.3, UiZoomStore.Next(1.20, +1));
        Assert.Equal(1.15, UiZoomStore.Next(1.20, -1));
    }

    [Fact]
    public void Next_GiaTriNgoaiDai_KepTruocRoiMoiNhay()
    {
        Assert.Equal(UiZoomStore.MaxZoom, UiZoomStore.Next(50, +1));
        Assert.Equal(UiZoomStore.MinZoom, UiZoomStore.Next(-3, -1));
        Assert.Equal(UiZoomStore.Steps[1], UiZoomStore.Next(-3, +1));   // kẹp về Min rồi mới lên 1 nấc
    }

    [Fact]
    public void Percent_LamTronVeSoNguyen()
    {
        Assert.Equal("100%", UiZoomStore.Percent(1.0));
        Assert.Equal("115%", UiZoomStore.Percent(1.15));
        Assert.Equal("200%", UiZoomStore.Percent(2.0));
    }
}
