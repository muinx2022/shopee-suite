using System;
using System.IO;
using XuLyDonShopee.App.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test cho tính năng "tải lại phiếu thiếu": helper kiểm magic PDF <see cref="SlipFiles.SlipFileIsValidPdf"/>.
/// KHÔNG test luồng browser (best-effort, verify tay).
/// </summary>
public class SlipRedownloadTests
{
    /// <summary>Ghi một file PDF hợp lệ (magic %PDF-) tạm, trả đường dẫn (caller tự xóa).</summary>
    private static string WriteValidPdf()
    {
        var path = Path.Combine(Path.GetTempPath(), $"slip_{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n%âãÏÓ\n1 0 obj\n"));
        return path;
    }

    /// <summary>Ghi một file KHÔNG phải PDF (không có magic) tạm.</summary>
    private static string WriteGarbage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"slip_{Guid.NewGuid():N}.pdf");
        File.WriteAllText(path, "<html>Bạn cần đăng nhập</html>");
        return path;
    }

    private static string MissingPath()
        => Path.Combine(Path.GetTempPath(), $"slip_{Guid.NewGuid():N}.pdf"); // KHÔNG tạo file

    // ===== SlipFileIsValidPdf =====

    [Fact]
    public void SlipFileIsValidPdf_FilePdfThat_True()
    {
        var path = WriteValidPdf();
        try { Assert.True(SlipFiles.SlipFileIsValidPdf(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SlipFileIsValidPdf_FileRac_False()
    {
        var path = WriteGarbage();
        try { Assert.False(SlipFiles.SlipFileIsValidPdf(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SlipFileIsValidPdf_FileKhongTonTai_False()
        => Assert.False(SlipFiles.SlipFileIsValidPdf(MissingPath()));
}
