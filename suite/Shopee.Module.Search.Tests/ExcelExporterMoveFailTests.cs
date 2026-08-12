using ShopeeStatApp.Models;
using ShopeeStatApp.Services;

namespace Shopee.Module.Search.Tests;

/// <summary>
/// <c>ExcelExporter.Export</c> ghi ra file tạm rồi <c>File.Move</c> đè lên file đích. File đích đang mở
/// trong Excel (khoá) là Move NÉM — trước đây file <c>*.tmp.xlsx</c> nằm lại trong thư mục xuất: tên có GUID
/// nên lượt sau không đè lên được, mỗi lần lỗi lại thêm một rác, và user không phân biệt được với file thật.
/// </summary>
public sealed class ExcelExporterMoveFailTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ss-export-tests", Guid.NewGuid().ToString("N"));

    private static ProductResult Sp(long itemId) => new()
    {
        ItemId = itemId,
        ShopId = 123456,
        Name = "Áo thun nam",
        Category = "Áo thun",
        PriceVnd = 99000,
        MonthlySold = 12,
    };

    [Fact]
    public void FileDichBiKhoa_NemLoiVaKhongDeLaiFileTmp()
    {
        Directory.CreateDirectory(_dir);
        var dich = Path.Combine(_dir, "ketqua.xlsx");
        File.WriteAllText(dich, "file cũ");

        // Khoá y như Excel đang mở file: không cho ai ghi/xoá/đổi tên.
        using (var _ = new FileStream(dich, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // Windows trả UnauthorizedAccessException cho Move đè file đang khoá, Linux/ca khác trả
            // IOException — cái phải chắc là LỖI ĐƯỢC NÉM RA, không bị nuốt.
            var loi = Record.Exception(() => ExcelExporter.Export([Sp(1), Sp(2)], _dir, "ketqua"));
            Assert.True(loi is IOException or UnauthorizedAccessException,
                "Ghi Excel vào file đang khoá phải ném lỗi, thực tế: " + (loi?.GetType().Name ?? "không ném"));
        }

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp.xlsx"));
        Assert.Equal("file cũ", File.ReadAllText(dich));   // file thật của user còn nguyên
    }

    [Fact]
    public void FileDichRanh_GhiDuocVaKhongConFileTmp()
    {
        var path = ExcelExporter.Export([Sp(1)], _dir, "ketqua");

        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp.xlsx"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }
}
