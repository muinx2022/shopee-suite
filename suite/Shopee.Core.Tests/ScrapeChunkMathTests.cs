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

    // ── SplitPatch: cắt mảng vá cho worker rảnh. Điểm chết người là STALL của PHẦN DƯ ──────────────────
    // stall đếm "khoảng bắt đầu tại dòng X mà không tiến được lần nào"; chạm 3 là BỎ dòng đầu khoảng. Bản cũ
    // cho phần dư thừa hưởng nguyên stall của mảng gốc → phần dư (bắt đầu ở dòng KHÁC, chưa kẹt lần nào) chỉ
    // cần trượt 1 lần là chạm ngưỡng → bỏ oan một dòng chưa từng được thử đủ.

    [Fact]
    public void SplitPatch_PhanDu_LuonBatDauLaiStall0()
    {
        var (piece, rest) = ScrapeChunkMath.SplitPatch(new ScrapeChunkMath.PatchSlice(10, 29, 2), workers: 4);
        Assert.Equal(new ScrapeChunkMath.PatchSlice(10, 14, 2), piece);   // lát đầu GIỮ stall: dòng nghi kẹt nằm ở đây
        Assert.Equal(new ScrapeChunkMath.PatchSlice(15, 29, 0), rest);    // phần dư: chưa kẹt lần nào
    }

    [Theory]
    [InlineData(1)]    // 1 worker → không có ai để chia việc
    [InlineData(4)]    // mảng ngắn (5 dòng < 2×4) → để 1 worker chạy nốt
    public void SplitPatch_KhongCat_ThiGiuNguyenCaKhoangLanStall(int workers)
    {
        var patch = new ScrapeChunkMath.PatchSlice(10, 14, 2);
        var (piece, rest) = ScrapeChunkMath.SplitPatch(patch, workers);
        Assert.Equal(patch, piece);
        Assert.Null(rest);
    }

    [Fact]
    public void SplitPatch_CatDungBien_KhongMatKhongChongDong()
    {
        // size == 2×workers = ngưỡng cắt nhỏ nhất.
        var patch = new ScrapeChunkMath.PatchSlice(100, 103, 1);
        var (piece, rest) = ScrapeChunkMath.SplitPatch(patch, workers: 2);
        Assert.NotNull(rest);
        Assert.Equal(patch.From, piece.From);
        Assert.Equal(piece.To + 1, rest!.Value.From);   // liền mạch: không mất dòng, không chạy chồng
        Assert.Equal(patch.To, rest.Value.To);
        Assert.Equal(0, rest.Value.Stall);
    }
}
