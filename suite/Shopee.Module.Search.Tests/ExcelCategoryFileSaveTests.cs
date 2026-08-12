using ClosedXML.Excel;
using ShopeeStatApp.Services;

namespace Shopee.Module.Search.Tests;

/// <summary>
/// <c>ExcelCategoryFile.ApplyAndSave</c> ghi danh mục AI trở lại chính file .xlsx của user. Trước
/// 2026-08-12 nó gọi thẳng <c>_wb.Save()</c>: ClosedXML ghi lại TOÀN BỘ workbook đè lên file gốc, hỏng giữa
/// chừng (hết đĩa, AV chặn, file đang mở) là mất luôn file nguồn. Nay ghi ra file tạm rồi đổi tên — và
/// không được để lại rác <c>*.tmp.xlsx</c> khi đổi tên hỏng.
/// </summary>
public sealed class ExcelCategoryFileSaveTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ss-catfile-tests", Guid.NewGuid().ToString("N"));

    private string TaoFile(string ten = "sp.xlsx")
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, ten);
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Sheet1");
        ws.Cell(1, 1).Value = "Tên sp";
        ws.Cell(2, 1).Value = "Áo thun nam";
        ws.Cell(3, 1).Value = "Quần jean nữ";
        wb.SaveAs(path);
        return path;
    }

    [Fact]
    public void ApplyAndSave_GhiDuocDanhMuc_KhongDeLaiFileTmp()
    {
        var path = TaoFile();

        int soDong;
        using (var file = new ExcelCategoryFile(path))
        {
            soDong = file.ApplyAndSave(["Áo", "Quần"]);
        }

        Assert.Equal(2, soDong);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp.xlsx"));

        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheets.First();
        Assert.Equal("Danh mục", ws.Cell(1, 2).GetString());
        Assert.Equal("Áo", ws.Cell(2, 2).GetString());
        Assert.Equal("Quần", ws.Cell(3, 2).GetString());
    }

    /// <summary>File đang mở trong Excel → lưu KHÔNG được, và lỗi phải nổi lên tận nơi gọi (tab "Danh mục
    /// (AI)" báo cho user) chứ không nuốt lặng thành "đã cập nhật N dòng" trong khi file y nguyên.
    /// <para>Lưu ý phạm vi: khoá file nguồn làm <c>SaveAs</c> hỏng ngay từ bước ghi file tạm, nên ca này
    /// KHÔNG chạm tới nhánh dọn <c>.tmp</c> khi <c>File.Move</c> hỏng — nhánh đó chỉ được canh ở
    /// <see cref="ExcelExporterMoveFailTests"/> (cùng khuôn ghi tmp + đổi tên).</para></summary>
    [Fact]
    public void FileDangBiKhoa_LoiPhaiNoiLen_KhongNuot()
    {
        var path = TaoFile();
        using var file = new ExcelCategoryFile(path);

        using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var loi = Record.Exception(() => file.ApplyAndSave(["Áo", "Quần"]));

        Assert.True(loi is IOException or UnauthorizedAccessException,
            "Lưu vào file đang khoá phải ném lỗi, thực tế: " + (loi?.GetType().Name ?? "không ném"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }
}
