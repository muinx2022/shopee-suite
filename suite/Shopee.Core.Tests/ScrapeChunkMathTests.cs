using Shopee.Core.Scrape;

namespace Shopee.Core.Tests;

/// <summary>
/// <see cref="ScrapeChunkMath.ClampLastDone"/> — "dòng cuối đã cào xong" của một khối.
/// Đây là chỗ ĐÃ TỪNG biến rác thành dữ liệu: profile Brave dùng lại theo tk Shopee nên
/// <c>runnerState.lastCompletedRow</c> của lượt trước (vd 5000) còn nguyên; bản cũ kẹp XUỐNG <c>to</c>
/// (<c>Math.Min(last, to)</c>) nên khối 2–12 login fail ngay dòng đầu vẫn được ghi "đã xong tới 12" →
/// 11 dòng biến mất khỏi mọi lượt Resume mà không có dấu vết nào.
/// </summary>
public sealed class ScrapeChunkMathTests
{
    [Theory]
    // last VƯỢT khối = rác của lượt trước → coi như CHƯA làm gì (from-1), KHÔNG phải "xong cả khối".
    [InlineData(5000, 2, 12, 1)]
    [InlineData(13, 2, 12, 1)]
    // chưa có tiến độ → from-1
    [InlineData(null, 2, 12, 1)]
    // trong khối → giữ nguyên
    [InlineData(7, 2, 12, 7)]
    // đúng biên trên = xong thật cả khối → giữ
    [InlineData(12, 2, 12, 12)]
    // trước cả đầu khối (tiến độ khối khác, nhỏ hơn) → from-1
    [InlineData(1, 5, 9, 4)]
    public void ClampLastDone_KepDungKhoang(int? last, int from, int to, int mong)
    {
        Assert.Equal(mong, ScrapeChunkMath.ClampLastDone(last, from, to));
    }

    [Fact]
    public void ClampLastDone_KhoiMotDong_VanPhanBietXongVaChua()
    {
        Assert.Equal(4, ScrapeChunkMath.ClampLastDone(null, 5, 5));   // chưa xong
        Assert.Equal(5, ScrapeChunkMath.ClampLastDone(5, 5, 5));      // xong đúng dòng đó
        Assert.Equal(4, ScrapeChunkMath.ClampLastDone(99, 5, 5));     // rác → chưa xong
    }
}
