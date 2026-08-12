using Shopee.Core.Coordination;
using Shopee.Core.Scrape;

namespace Shopee.Core.Tests;

/// <summary>
/// SỔ DÒNG BỎ QUA của <see cref="ScrapeProgressStore"/> — bất biến: mọi dòng trong vùng phủ
/// <see cref="ScrapeProgress.Completed"/> là "cào OK" HOẶC có tên trong
/// <see cref="ScrapeProgress.SkippedRows"/>. Trước đây dòng cào hỏng giữa khối chỉ có một dòng log rồi
/// trôi: nó nằm gọn trong khoảng runner báo "đã cào" nên không lượt Resume nào chạy lại, cũng không chỗ
/// nào cho user biết là đang thiếu SP.
/// <para>Test ĐỤNG <c>Shared</c> thật (đọc/ghi file) nhưng an toàn: <c>data-dir.txt</c> trong output của
/// project test đẩy <c>SuitePaths.Root</c> về <c>bin\…\test-data\ShopeeSuite</c>, KHÔNG chạm
/// %AppData% của máy. Mỗi test dùng accountId riêng (Guid) nên không giẫm chân nhau.</para>
/// </summary>
public sealed class ScrapeProgressSkipTests
{
    private const string Sheet = "Data";

    private static string NewAccount() => "test-" + Guid.NewGuid().ToString("N");

    private static ScrapeProgress Get(string acc) =>
        ScrapeProgressStore.Shared.Find(acc, Sheet) ?? throw new InvalidOperationException("chưa có tiến độ");

    [Fact]
    public void MarkSkipped_GhiVaoSoBoQua_VaKeoVaoVungPhu()
    {
        var acc = NewAccount();
        ScrapeProgressStore.Shared.BeginFresh(acc, Sheet, "TK test", 10);
        ScrapeProgressStore.Shared.MarkCompleted(acc, Sheet, 2, 4);
        ScrapeProgressStore.Shared.MarkSkipped(acc, Sheet, 5);

        var p = Get(acc);
        Assert.Equal([5], p.SkippedRows);
        // Vùng phủ phải NUỐT dòng bỏ qua (2–4 nối 5 thành 2–5) → Resume không giao lại dòng hỏng vòng vô tận.
        Assert.Empty(ScrapeProgressStore.Shared.RemainingSegments(acc, Sheet, 2, 5));
        Assert.Equal(5, p.LastRowReached);
    }

    [Fact]
    public void MarkSkipped_GoiTrungKhongNhanDoi()
    {
        var acc = NewAccount();
        ScrapeProgressStore.Shared.BeginFresh(acc, Sheet, "TK test", 10);
        ScrapeProgressStore.Shared.MarkSkipped(acc, Sheet, 7);
        ScrapeProgressStore.Shared.MarkSkipped(acc, Sheet, 7);   // diff của runner có thể bắn lại khi retry chunk
        ScrapeProgressStore.Shared.MarkSkipped(acc, Sheet, 6);

        Assert.Equal([6, 7], Get(acc).SkippedRows);   // dedup + sắp xếp
    }

    [Fact]
    public void MarkSkipped_GhiXuongDia_DocLaiVanCon()
    {
        var acc = NewAccount();
        ScrapeProgressStore.Shared.BeginFresh(acc, Sheet, "TK test", 10);
        ScrapeProgressStore.Shared.MarkSkipped(acc, Sheet, 3);

        ScrapeProgressStore.Shared.Load();   // nạp lại từ file (mô phỏng mở lại app)
        Assert.Equal([3], Get(acc).SkippedRows);
    }

    [Fact]
    public void BeginFresh_XoaSoBoQua_BeginResume_GiuNguyen()
    {
        var acc = NewAccount();
        ScrapeProgressStore.Shared.BeginFresh(acc, Sheet, "TK test", 10);
        ScrapeProgressStore.Shared.MarkSkipped(acc, Sheet, 4);
        Assert.Equal([4], Get(acc).SkippedRows);

        // Tiếp tục: giữ nguyên sổ (dòng đó vẫn đang thiếu SP, phải còn hiện ra cho user).
        ScrapeProgressStore.Shared.BeginResume(acc, Sheet, "TK test", 10);
        Assert.Equal([4], Get(acc).SkippedRows);

        // Chạy (reset): cào lại từ đầu → sổ cũ hết nghĩa.
        ScrapeProgressStore.Shared.BeginFresh(acc, Sheet, "TK test", 10);
        Assert.Empty(Get(acc).SkippedRows);
        Assert.Empty(Get(acc).Completed);
    }

    [Fact]
    public void FinishRun_PhanThieuChiToanDongDaBoQua_VanRaCompleted()
    {
        var acc = NewAccount();
        ScrapeProgressStore.Shared.BeginFresh(acc, Sheet, "TK test", 5);
        ScrapeProgressStore.Shared.MarkCompleted(acc, Sheet, 2, 4);
        ScrapeProgressStore.Shared.MarkSkipped(acc, Sheet, 5);   // dòng cuối cào hỏng, đã ghi nhận

        ScrapeProgressStore.Shared.FinishRun(acc, Sheet, 2, 5);
        // Kết luận là "hoàn thành" (không kẹt vĩnh viễn ở "chưa xong", không đốt lượt mở Brave cho dòng
        // hỏng), nhưng sổ bỏ qua vẫn còn để UI nói rõ đang thiếu dòng nào.
        Assert.Equal(LedgerStatus.Completed, Get(acc).Status);
        Assert.Equal([5], Get(acc).SkippedRows);
    }
}
