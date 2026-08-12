using ClosedXML.Excel;
using ShopeeStatApp.Services;

namespace Shopee.Module.Search.Tests;

/// <summary>
/// Đánh dấu "đã xử lý" lên file link. Hai lỗi mất dấu đã khoá lại ở đây:
/// <list type="number">
/// <item><c>SetDone</c> tạo <c>LinkFileStore</c> MỚI mỗi lần và không <c>Load()</c> ⇒ <c>StatusColumn=0</c>
/// ⇒ bản cũ lấy "cột cuối + 1" nên mỗi lượt đánh dấu đẻ THÊM một cột; nạp lại chỉ đọc cột cuối nên chỉ còn
/// dấu của lượt cuối, các link kia bị cào lại.</item>
/// <item>Nhiều lane cùng ghi một file: đọc-sửa-ghi chồng nhau làm mất dấu (lỗi lại bị nuốt lặng).</item>
/// </list>
/// </summary>
public sealed class LinkFileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ss-linkfile-tests", Guid.NewGuid().ToString("N"));

    public LinkFileStoreTests() => Directory.CreateDirectory(_dir);

    private static string Link(int i) => $"https://shopee.vn/thoi-trang-{i}-cat.1103556{i}";

    /// <summary>File .xlsx "sạch": cột 1 toàn link, chưa có cột trạng thái.</summary>
    private string TaoFileXlsx(int soLink, string ten = "links.xlsx")
    {
        var path = Path.Combine(_dir, ten);
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Sheet1");
        for (var r = 1; r <= soLink; r++) ws.Cell(r, 1).Value = Link(r);
        wb.SaveAs(path);
        return path;
    }

    private static int CotCuoi(string path)
    {
        using var wb = new XLWorkbook(path);
        return wb.Worksheets.First().LastColumnUsed()?.ColumnNumber() ?? 0;
    }

    [Fact]
    public void DanhDauBaLink_ChiSinhMotCotTrangThai_NapLaiDuBaDau()
    {
        var path = TaoFileXlsx(3);

        // Y HỆT SetDone: mỗi lượt một instance mới, không Load() trước.
        for (var row = 1; row <= 3; row++)
            new LinkFileStore(path).MarkStatus(row, LinkFileStore.Processed);

        Assert.Equal(2, CotCuoi(path));   // 1 cột link + ĐÚNG 1 cột trạng thái

        var rows = new LinkFileStore(path).Load();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.True(r.IsDone, $"dòng {r.RowNumber} mất dấu Processed"));
    }

    [Fact]
    public void FileBanNhieuCotTrangThai_DocGopDuDau_GhiTiepVaoCotTraiNhat()
    {
        // Dựng đúng thứ bản cũ để lại: mỗi lượt đánh dấu một cột riêng (2, 3, 4).
        var path = Path.Combine(_dir, "ban.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Sheet1");
            for (var r = 1; r <= 3; r++) ws.Cell(r, 1).Value = Link(r);
            ws.Cell(1, 2).Value = LinkFileStore.Processed;
            ws.Cell(2, 3).Value = LinkFileStore.Processed;
            ws.Cell(3, 4).Value = LinkFileStore.Processed;
            wb.SaveAs(path);
        }

        var store = new LinkFileStore(path);
        var rows = store.Load();

        Assert.Equal(2, store.StatusColumn);   // cột TRÁI NHẤT của dãy cột trạng thái
        Assert.All(rows, r => Assert.True(r.IsDone, $"dòng {r.RowNumber} mất dấu ở cột thừa"));

        // Ghi tiếp: dồn về cột trái nhất, KHÔNG đẻ thêm cột thứ 5.
        new LinkFileStore(path).MarkStatus(2, LinkFileStore.Processed);
        Assert.Equal(4, CotCuoi(path));
        using var kiemTra = new XLWorkbook(path);
        Assert.Equal(LinkFileStore.Processed, kiemTra.Worksheets.First().Cell(2, 2).GetString());
    }

    [Fact]
    public void XoaTrangThai_DonSachCaDayCotThua_KhongConDauCu()
    {
        var path = Path.Combine(_dir, "ban-reset.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Sheet1");
            for (var r = 1; r <= 3; r++) ws.Cell(r, 1).Value = Link(r);
            ws.Cell(1, 2).Value = LinkFileStore.Processed;
            ws.Cell(2, 3).Value = LinkFileStore.Processed;
            ws.Cell(3, 4).Value = LinkFileStore.Processed;
            wb.SaveAs(path);
        }

        new LinkFileStore(path).ClearAllStatuses();

        var rows = new LinkFileStore(path).Load();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.False(r.IsDone, $"dòng {r.RowNumber} vẫn còn dấu sau khi đặt lại"));
    }

    [Fact]
    public void HaiLuongCungDanhDauMotFileXlsx_KhongLoi_DuHaiMuoiDau()
    {
        var path = TaoFileXlsx(20, "song-song.xlsx");

        ChayHaiLuong(path);

        var rows = new LinkFileStore(path).Load();
        Assert.Equal(20, rows.Count);
        Assert.Equal(20, rows.Count(r => r.IsDone));
        Assert.Equal(2, CotCuoi(path));
    }

    [Fact]
    public void HaiLuongCungDanhDauMotFileTxt_KhongLoi_DuHaiMuoiDau()
    {
        var path = Path.Combine(_dir, "song-song.txt");
        File.WriteAllLines(path, Enumerable.Range(1, 20).Select(Link));

        ChayHaiLuong(path);

        var rows = new LinkFileStore(path).Load();
        Assert.Equal(20, rows.Count);
        Assert.Equal(20, rows.Count(r => r.IsDone));
        // Không để lại file .tmp rác (mỗi lượt ghi một tên tmp riêng, đổi tên xong là hết).
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    // ── Cột GHI CHÚ của user không được coi là cột trạng thái ─────────────────────────────────────────
    // IsStatusColumn từng nhận mọi giá trị tiền tố "Lỗi" → cột ghi chú "Lỗi ảnh dòng 2"… bị nhận nhầm:
    // MarkStatus ghi đè mất ghi chú, ClearAllStatuses xoá lan sang, ReadStatus lấy nhầm ghi chú che dấu thật.

    /// <summary>File A=link, B=ghi chú user: đánh dấu phải sang cột MỚI, không đè ghi chú.</summary>
    [Fact]
    public void CotGhiChuBatDauBangLoi_KhongBiNhanNham_DanhDauKhongDeGhiChu()
    {
        var path = TaoFileCoGhiChu("ghi-chu.xlsx", themTrangThai: false);

        new LinkFileStore(path).MarkStatus(2, LinkFileStore.Processed);

        using var kiemTra = new XLWorkbook(path);
        var ws = kiemTra.Worksheets.First();
        Assert.Equal("Lỗi ảnh dòng 2", ws.Cell(2, 2).GetString());          // ghi chú còn nguyên
        Assert.Equal(LinkFileStore.Processed, ws.Cell(2, 3).GetString());   // dấu nằm cột mới sau dữ liệu
    }

    /// <summary>Ô ghi chú lọt cạnh cột trạng thái thật không được kéo vào dãy → không che dấu Processed.</summary>
    [Fact]
    public void OGhiChuCanhCotTrangThai_KhongCheDauProcessedThat()
    {
        var path = Path.Combine(_dir, "ghi-chu-che.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Sheet1");
            for (var r = 1; r <= 3; r++) ws.Cell(r, 1).Value = Link(r);
            ws.Cell(1, 2).Value = "Lỗi mạng";   // ghi chú user, MỘT ô sót lại
            for (var r = 1; r <= 3; r++) ws.Cell(r, 3).Value = LinkFileStore.Processed;
            wb.SaveAs(path);
        }

        var store = new LinkFileStore(path);
        var rows = store.Load();

        Assert.Equal(3, store.StatusColumn);   // dãy trạng thái KHÔNG lan sang cột ghi chú
        Assert.All(rows, r => Assert.True(r.IsDone, $"dòng {r.RowNumber} bị ghi chú che mất dấu"));
    }

    /// <summary>Đặt lại trạng thái chỉ xoá cột trạng thái, không xoá lan sang cột ghi chú bên trái.</summary>
    [Fact]
    public void DatLai_KhongXoaLanSangCotGhiChu()
    {
        var path = TaoFileCoGhiChu("ghi-chu-reset.xlsx", themTrangThai: true);

        new LinkFileStore(path).ClearAllStatuses();

        using var kiemTra = new XLWorkbook(path);
        var ws = kiemTra.Worksheets.First();
        Assert.Equal("Lỗi ảnh dòng 2", ws.Cell(2, 2).GetString());   // ghi chú còn
        Assert.Equal("", ws.Cell(2, 3).GetString());                 // dấu đã xoá
    }

    /// <summary>File user có dòng TIÊU ĐỀ BẢNG ở hàng 1, tên cột ("Trạng thái") nằm HÀNG 2 — khoá cứng
    /// header vào hàng 1 là các file này mất sạch dấu Processed và bị cào lại toàn bộ.</summary>
    [Fact]
    public void HeaderTrangThaiOHangHai_VanNhanRaCotTrangThai_KhongMatDau()
    {
        var path = Path.Combine(_dir, "header-hang-2.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Sheet1");
            ws.Cell(1, 1).Value = "DANH SÁCH LINK THÁNG 8";   // tiêu đề bảng
            ws.Cell(2, 1).Value = "Link";
            ws.Cell(2, 2).Value = "Trạng thái";               // tên cột ở HÀNG 2
            for (var r = 3; r <= 5; r++)
            {
                ws.Cell(r, 1).Value = Link(r);
                ws.Cell(r, 2).Value = LinkFileStore.Processed;
            }
            wb.SaveAs(path);
        }

        var store = new LinkFileStore(path);
        var rows = store.Load();

        Assert.Equal(2, store.StatusColumn);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.True(r.IsDone, $"dòng {r.RowNumber} mất dấu vì header nằm hàng 2"));

        // Đánh dấu tiếp phải vào ĐÚNG cột 2, không đẻ cột mới.
        new LinkFileStore(path).MarkStatus(4, LinkFileStore.Processed);
        Assert.Equal(2, CotCuoi(path));
    }

    /// <summary>A=link, B=ghi chú "Lỗi ảnh dòng r" (mọi dòng), C=Processed nếu <paramref name="themTrangThai"/>.</summary>
    private string TaoFileCoGhiChu(string ten, bool themTrangThai)
    {
        var path = Path.Combine(_dir, ten);
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Sheet1");
        for (var r = 1; r <= 3; r++)
        {
            ws.Cell(r, 1).Value = Link(r);
            ws.Cell(r, 2).Value = $"Lỗi ảnh dòng {r}";
            if (themTrangThai) ws.Cell(r, 3).Value = LinkFileStore.Processed;
        }
        wb.SaveAs(path);
        return path;
    }

    /// <summary>2 luồng × 10 dòng, xen kẽ chẵn/lẻ — mô phỏng 2 lane cùng đánh dấu một file.</summary>
    private static void ChayHaiLuong(string path)
    {
        var loi = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var tasks = Enumerable.Range(0, 2).Select(offset => Task.Run(() =>
        {
            for (var i = 0; i < 10; i++)
            {
                try { new LinkFileStore(path).MarkStatus(i * 2 + offset + 1, LinkFileStore.Processed); }
                catch (Exception ex) { loi.Add(ex); }
            }
        })).ToArray();
        Task.WaitAll(tasks);
        Assert.True(loi.IsEmpty, loi.FirstOrDefault()?.Message);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }
}
