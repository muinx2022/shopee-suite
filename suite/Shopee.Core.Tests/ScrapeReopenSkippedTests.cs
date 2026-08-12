using Shopee.Core.Coordination;
using Shopee.Core.Scrape;

namespace Shopee.Core.Tests;

/// <summary>
/// "CÀO LẠI DÒNG ĐÃ BỎ" (đợt A) — trước đây muốn cào lại một dòng hỏng thì chỉ có đường Chạy (reset), tức là
/// vứt tiến độ của CẢ sheet. Nay <see cref="ScrapeProgressStore.ReopenSkipped"/> khoét đúng các dòng trong sổ
/// bỏ-qua ra khỏi vùng phủ để lượt Tiếp tục nhặt lại đúng ngần ấy dòng.
/// <para>Phần dưới của file soát <see cref="RowRangeMath.SubtractRows"/> — phép khoét thuần, cũng là thứ HUB
/// dùng ở <c>POST /ledger/reopen-skipped</c> (bản copy trong <c>server\…\Data\RowRanges.cs</c>).</para>
/// <para>Test đụng <c>Shared</c> thật nhưng an toàn: <c>data-dir.txt</c> đẩy kho về <c>bin\…\test-data</c>,
/// mỗi test một accountId riêng (xem <see cref="ScrapeProgressSkipTests"/>).</para>
/// </summary>
public sealed class ScrapeReopenSkippedTests
{
    private const string Sheet = "Data";

    private static string NewAccount() => "test-" + Guid.NewGuid().ToString("N");

    private static ScrapeProgress Get(string acc) =>
        ScrapeProgressStore.Shared.Find(acc, Sheet) ?? throw new InvalidOperationException("chưa có tiến độ");

    private static IEnumerable<(int from, int to)> Pairs(IEnumerable<RowRange> rs) => rs.Select(r => (r.From, r.To));

    // ===== ReopenSkipped =====

    [Fact]
    public void ReopenSkipped_KhoetDungDongDaBo_ComplementThayLai()
    {
        var acc = NewAccount();
        ScrapeProgressStore.Shared.BeginFresh(acc, Sheet, "TK test", 10);
        ScrapeProgressStore.Shared.MarkCompleted(acc, Sheet, 2, 8);
        ScrapeProgressStore.Shared.MarkSkipped(acc, Sheet, 4);
        ScrapeProgressStore.Shared.MarkSkipped(acc, Sheet, 7);
        // Trước khi mở lại: dòng 4 và 7 nằm gọn trong vùng phủ → resume KHÔNG giao lại (đúng thiết kế cũ).
        Assert.Empty(ScrapeProgressStore.Shared.RemainingSegments(acc, Sheet, 2, 8));

        var n = ScrapeProgressStore.Shared.ReopenSkipped(acc, Sheet);

        Assert.Equal(2, n);
        Assert.Equal([(2, 3), (5, 6), (8, 8)], Pairs(Get(acc).Completed));
        // Lượt Tiếp tục giờ nhặt lại ĐÚNG 2 dòng đó, không đụng phần đã cào được.
        Assert.Equal([(4, 4), (7, 7)], ScrapeProgressStore.Shared.RemainingSegments(acc, Sheet, 2, 8));
    }

    [Fact]
    public void ReopenSkipped_XoaSachSo_VaHaCompletedVeStopped()
    {
        var acc = NewAccount();
        ScrapeProgressStore.Shared.BeginFresh(acc, Sheet, "TK test", 5);
        ScrapeProgressStore.Shared.MarkCompleted(acc, Sheet, 2, 4);
        ScrapeProgressStore.Shared.MarkSkipped(acc, Sheet, 5);
        ScrapeProgressStore.Shared.FinishRun(acc, Sheet, 2, 5);
        Assert.Equal(LedgerStatus.Completed, Get(acc).Status);

        ScrapeProgressStore.Shared.ReopenSkipped(acc, Sheet);

        // Vừa có dòng chưa cào → KHÔNG được để UI báo "✔ Hoàn thành" nữa.
        Assert.Equal(LedgerStatus.Stopped, Get(acc).Status);
        Assert.Empty(Get(acc).SkippedRows);
    }

    [Fact]
    public void ReopenSkipped_SoRong_KhongDoiGi_TraVeKhong()
    {
        var acc = NewAccount();
        ScrapeProgressStore.Shared.BeginFresh(acc, Sheet, "TK test", 5);
        ScrapeProgressStore.Shared.MarkCompleted(acc, Sheet, 2, 5);
        ScrapeProgressStore.Shared.FinishRun(acc, Sheet, 2, 5);

        Assert.Equal(0, ScrapeProgressStore.Shared.ReopenSkipped(acc, Sheet));
        // Sổ sạch thì nút này là no-op HOÀN TOÀN — không được đụng vùng phủ, cũng không hạ trạng thái.
        Assert.Equal([(2, 5)], Pairs(Get(acc).Completed));
        Assert.Equal(LedgerStatus.Completed, Get(acc).Status);
    }

    [Fact]
    public void ReopenSkipped_GhiXuongDia_DocLaiVanMo()
    {
        var acc = NewAccount();
        ScrapeProgressStore.Shared.BeginFresh(acc, Sheet, "TK test", 10);
        ScrapeProgressStore.Shared.MarkCompleted(acc, Sheet, 2, 6);
        ScrapeProgressStore.Shared.MarkSkipped(acc, Sheet, 3);
        ScrapeProgressStore.Shared.ReopenSkipped(acc, Sheet);

        ScrapeProgressStore.Shared.Load();   // nạp lại từ file (mô phỏng mở lại app)
        Assert.Equal([(2, 2), (4, 6)], Pairs(Get(acc).Completed));
        Assert.Empty(Get(acc).SkippedRows);
    }

    // ===== RowRangeMath.SubtractRows =====

    [Fact]
    public void SubtractRows_DongGiuaKhoang_TachDoi()
    {
        var r = RowRangeMath.SubtractRows([new RowRange { From = 1, To = 10 }], [5]);
        Assert.Equal([(1, 4), (6, 10)], Pairs(r));
    }

    [Fact]
    public void SubtractRows_DongDauVaCuoi_CutDau_CutDuoi()
    {
        Assert.Equal([(2, 10)], Pairs(RowRangeMath.SubtractRows([new RowRange { From = 1, To = 10 }], [1])));
        Assert.Equal([(1, 9)], Pairs(RowRangeMath.SubtractRows([new RowRange { From = 1, To = 10 }], [10])));
    }

    [Fact]
    public void SubtractRows_KhoangMotDong_BienMatHan()
    {
        var r = RowRangeMath.SubtractRows(
            [new RowRange { From = 3, To = 3 }, new RowRange { From = 7, To = 8 }], [3]);
        Assert.Equal([(7, 8)], Pairs(r));
    }

    [Fact]
    public void SubtractRows_DongKhongThuocKhoangNao_BoQua()
    {
        var r = RowRangeMath.SubtractRows([new RowRange { From = 2, To = 4 }], [9, 100]);
        Assert.Equal([(2, 4)], Pairs(r));
    }

    [Fact]
    public void SubtractRows_KhongCoDongNaoDeKhoet_TraVungPhuDaChuanHoa()
    {
        // Danh sách rỗng = đường "sổ sạch": vẫn phải trả về vùng phủ hợp lệ (chuẩn hoá), không mất khoảng nào.
        var r = RowRangeMath.SubtractRows(
            [new RowRange { From = 5, To = 6 }, new RowRange { From = 1, To = 3 }], []);
        Assert.Equal([(1, 3), (5, 6)], Pairs(r));
    }

    [Fact]
    public void SubtractRows_NhieuDongLienNhau_KhongDinhLaiKhiNormalize()
    {
        // Bẫy của Normalize: nó NỐI khoảng liền kề (from <= To+1). Khoét 4,5 khỏi 1–8 phải còn ĐÚNG 2 mảnh,
        // dính lại thành 1–8 là mất luôn phần vừa mở ra (resume không thấy dòng nào thiếu).
        var r = RowRangeMath.SubtractRows([new RowRange { From = 1, To = 8 }], [4, 5]);
        Assert.Equal([(1, 3), (6, 8)], Pairs(r));
    }

    // ===== UnmarkResolvedSkipped: gỡ dòng đã được (máy khác) cào lại khỏi sổ bỏ qua local =====

    [Fact]
    public void UnmarkResolvedSkipped_DongDaXongTrenHub_GoKhoiSoLocal_GiuVungPhu()
    {
        var acc = NewAccount();
        ScrapeProgressStore.Shared.BeginFresh(acc, Sheet, "TK test", 10);
        ScrapeProgressStore.Shared.MarkCompleted(acc, Sheet, 2, 8);
        ScrapeProgressStore.Shared.MarkSkipped(acc, Sheet, 4);   // local còn nghĩ 4 và 7 đang bỏ
        ScrapeProgressStore.Shared.MarkSkipped(acc, Sheet, 7);

        // Hub: vùng phủ 2–8; sổ bỏ qua CHỈ còn [7] (dòng 4 đã được máy khác cào lại xong).
        ScrapeProgressStore.Shared.UnmarkResolvedSkipped(acc, Sheet,
            [new RowRange { From = 2, To = 8 }], new HashSet<int> { 7 });

        var p = Get(acc);
        Assert.Equal([7], p.SkippedRows);              // 4 đã gỡ, 7 giữ (hub vẫn bỏ)
        Assert.Equal([(2, 8)], Pairs(p.Completed));    // KHÔNG đụng vùng phủ (dòng 4 đã cào xong, vẫn phủ)
    }

    [Fact]
    public void UnmarkResolvedSkipped_DongNgoaiVungPhuHub_GiuNguyen()
    {
        var acc = NewAccount();
        ScrapeProgressStore.Shared.BeginFresh(acc, Sheet, "TK test", 10);
        ScrapeProgressStore.Shared.MarkCompleted(acc, Sheet, 2, 8);
        ScrapeProgressStore.Shared.MarkSkipped(acc, Sheet, 4);

        // Hub chưa phủ dòng 4 (vùng phủ chỉ 2–3) và cũng không liệt nó trong skipped → CHƯA đủ bằng chứng
        // "đã xong", phải GIỮ (gỡ oan là mất dấu dòng thật sự còn thiếu — tái tạo lỗi S1).
        ScrapeProgressStore.Shared.UnmarkResolvedSkipped(acc, Sheet,
            [new RowRange { From = 2, To = 3 }], new HashSet<int>());

        Assert.Equal([4], Get(acc).SkippedRows);
    }

    [Fact]
    public void SubtractRows_KhoangRacToMaxValue_KhongTreo_VaKhongTranSo()
    {
        // Server không validate RowRange do client gửi: một bản ghi rác To=int.MaxValue mà thuật toán đi TỪNG
        // DÒNG của khoảng là vòng lặp chạy (gần như) vĩnh viễn TRONG lock của HubDatabase → treo cả Hub.
        // Thuật toán phải cắt QUANH các dòng khoét (xong tức thì) và không tràn int ở biên (row+1).
        var r = RowRangeMath.SubtractRows([new RowRange { From = 2, To = int.MaxValue }], [5, int.MaxValue]);
        Assert.Equal([(2, 4), (6, int.MaxValue - 1)], Pairs(r));
    }
}
