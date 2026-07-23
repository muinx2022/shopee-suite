using System;
using System.IO;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Data;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test cho tính năng "tải lại phiếu thiếu": hàm PURE <see cref="AccountSession.ThieuPhieu"/> (ma trận trạng
/// thái/vận đơn/file), helper kiểm magic PDF <see cref="AccountSession.SlipFileIsValidPdf"/>, và query
/// <see cref="OrdersRepository.GetOrdersForSlipCheck"/>. KHÔNG test luồng browser (best-effort, verify tay).
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
        try { Assert.True(AccountSession.SlipFileIsValidPdf(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SlipFileIsValidPdf_FileRac_False()
    {
        var path = WriteGarbage();
        try { Assert.False(AccountSession.SlipFileIsValidPdf(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SlipFileIsValidPdf_FileKhongTonTai_False()
        => Assert.False(AccountSession.SlipFileIsValidPdf(MissingPath()));

    // ===== ThieuPhieu (ma trận) =====

    [Fact]
    public void ThieuPhieu_ChuanBiHang_CoVanDon_FileThieu_True()
    {
        // đúng trạng thái + có vận đơn + file không tồn tại → THIẾU
        Assert.True(AccountSession.ThieuPhieu("Chờ lấy hàng", "SPXVN123", MissingPath()));
        Assert.True(AccountSession.ThieuPhieu("Chuẩn bị hàng", "SPXVN123", MissingPath()));
    }

    [Fact]
    public void ThieuPhieu_ChuanBiHang_CoVanDon_FileRac_True()
    {
        // file .pdf tồn tại nhưng KHÔNG có magic → coi như thiếu (không tin đuôi file)
        var path = WriteGarbage();
        try { Assert.True(AccountSession.ThieuPhieu("Chờ lấy hàng", "SPXVN123", path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ThieuPhieu_ChuanBiHang_CoVanDon_FilePdfHopLe_False()
    {
        // đã có file PDF thật → KHÔNG thiếu
        var path = WriteValidPdf();
        try { Assert.False(AccountSession.ThieuPhieu("Chờ lấy hàng", "SPXVN123", path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ThieuPhieu_KhongVanDon_False()
    {
        // chưa có vận đơn (chưa arrange) → phiếu tạo ở bước Xử lý đơn, KHÔNG tính thiếu
        Assert.False(AccountSession.ThieuPhieu("Chờ lấy hàng", null, MissingPath()));
        Assert.False(AccountSession.ThieuPhieu("Chuẩn bị hàng", "", MissingPath()));
        Assert.False(AccountSession.ThieuPhieu("Chuẩn bị hàng", "   ", MissingPath()));
    }

    [Theory]
    [InlineData("Đã giao")]
    [InlineData("Đang giao")]
    [InlineData("Đã hủy")]
    [InlineData("Hoàn thành")]
    [InlineData("")]
    [InlineData(null)]
    public void ThieuPhieu_TrangThaiKhac_False(string? status)
    {
        // trạng thái không phải Chuẩn bị hàng → KHÔNG tính thiếu dù có vận đơn + thiếu file
        Assert.False(AccountSession.ThieuPhieu(status, "SPXVN123", MissingPath()));
    }

    // ===== OrdersRepository.GetOrdersForSlipCheck =====

    private static SyncedOrder Order(string sn, string? status, string? tracking) => new()
    {
        OrderSn = sn,
        Status = status,
        TrackingNumber = tracking,
        ItemsJson = "[]",
        ItemCount = 0,
    };

    [Fact]
    public void GetOrdersForSlipCheck_TraDungManStatusTracking()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[]
        {
            Order("SN1", "Chờ lấy hàng", "SPXVN1"),
            Order("SN2", "Đã giao", null),
        }, DateTime.UtcNow);
        // đơn của tài khoản KHÁC không được trả về
        repo.UpsertMany(2, new[] { Order("SN3", "Chờ lấy hàng", "SPXVN3") }, DateTime.UtcNow);

        var rows = repo.GetOrdersForSlipCheck(1);

        Assert.Equal(2, rows.Count);
        var sn1 = Assert.Single(rows, r => r.OrderSn == "SN1");
        Assert.Equal("Chờ lấy hàng", sn1.Status);
        Assert.Equal("SPXVN1", sn1.TrackingNumber);
        var sn2 = Assert.Single(rows, r => r.OrderSn == "SN2");
        Assert.Equal("Đã giao", sn2.Status);
        Assert.Null(sn2.TrackingNumber);
        Assert.DoesNotContain(rows, r => r.OrderSn == "SN3");
    }

    [Fact]
    public void GetOrdersForSlipCheck_KhongDon_TraRong()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        Assert.Empty(repo.GetOrdersForSlipCheck(1));
    }
}
