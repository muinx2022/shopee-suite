using XuLyDonShopee.App.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test các hàm THUẦN của vòng chờ đẩy: bậc backoff <see cref="HubOutboxWorker.ChuKyTiepTheo"/> và bộ lọc hàng
/// đợi đếm "Đã bán" <see cref="HubOutbox.LocHangDoiDemDaBan"/> (SQL không biết luật "đã giao" / "đơn hủy" nên
/// lọc ở C#). Phần vòng lặp nền + hook thật kiểm ở chạy thật, như các phần browser khác của dự án.
/// </summary>
public class HubOutboxWorkerTests
{
    private static (string OrderSn, string Sku, string? Status, string? StatusDescription, string? CancelReason)
        Row(string sn, string sku, string? status, string? desc = null, string? cancelReason = null)
        => (sn, sku, status, desc, cancelReason);

    // ===== Backoff: 2′ khi khỏe → 5′ → 10′ (trần); thành công thì về 2′ =====

    [Theory]
    [InlineData(0, 2)]    // khỏe (hoặc vừa thành công → đếm lỗi về 0)
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 10)]
    [InlineData(50, 10)]  // trần 10′
    public void ChuKyTiepTheo_GianDungBac(int soLuotLoiLienTiep, int soPhut)
        => Assert.Equal(soPhut, HubOutboxWorker.ChuKyTiepTheo(soLuotLoiLienTiep).TotalMinutes);

    [Fact]
    public void ChuKyTiepTheo_ChuoiLoiRoiThanhCong_VeLai2Phut()
    {
        // Mô phỏng đúng cách vòng lặp đếm: lỗi → +1; thành công → 0.
        var loi = 0;
        Assert.Equal(HubOutboxWorker.ChuKyThuong, HubOutboxWorker.ChuKyTiepTheo(loi));

        loi++;
        Assert.Equal(HubOutboxWorker.ChuKyLoi1, HubOutboxWorker.ChuKyTiepTheo(loi));
        loi++;
        Assert.Equal(HubOutboxWorker.ChuKyLoiTran, HubOutboxWorker.ChuKyTiepTheo(loi));
        loi++;
        Assert.Equal(HubOutboxWorker.ChuKyLoiTran, HubOutboxWorker.ChuKyTiepTheo(loi));

        loi = 0; // đích nhận lại được
        Assert.Equal(HubOutboxWorker.ChuKyThuong, HubOutboxWorker.ChuKyTiepTheo(loi));
    }

    // ===== Bộ lọc hàng đợi đếm "Đã bán" =====

    [Fact]
    public void LocHangDoi_GiuDonDaGiaoCoSku()
    {
        var (skus, sns) = HubOutbox.LocHangDoiDemDaBan(new[]
        {
            Row("SN1", "B00001", "Đã giao"),
            Row("SN2", "B00002", "Hoàn thành"),
        });

        Assert.Equal(new[] { "B00001", "B00002" }, skus);
        Assert.Equal(new[] { "SN1", "SN2" }, sns);
    }

    [Theory]
    [InlineData("Chờ lấy hàng")]
    [InlineData("Đang giao")]
    [InlineData("Đã giao cho ĐVVC")]              // mới bàn giao ĐVVC — khách CHƯA nhận
    [InlineData("Giao hàng không thành công")]
    [InlineData(null)]
    public void LocHangDoi_BoDonChuaGiaoXong(string? status)
    {
        var (skus, sns) = HubOutbox.LocHangDoiDemDaBan(new[] { Row("SN1", "B00001", status) });

        Assert.Empty(skus);
        Assert.Empty(sns);
    }

    [Fact]
    public void LocHangDoi_BoDonHuy()
    {
        var (skus, sns) = HubOutbox.LocHangDoiDemDaBan(new[]
        {
            Row("SN1", "B00001", "Đã hủy"),
            // Trạng thái chính là "Đã giao" nhưng CÓ lý do hủy → LaDonHuy → KHÔNG đếm.
            Row("SN2", "B00002", "Đã giao", cancelReason: "Người mua hủy"),
        });

        Assert.Empty(skus);
        Assert.Empty(sns);
    }

    [Fact]
    public void LocHangDoi_TrungMaDon_ChiXuMotLan()
    {
        var (skus, sns) = HubOutbox.LocHangDoiDemDaBan(new[]
        {
            Row("SN1", "B00001", "Đã giao"),
            Row("SN1", "B00001", "Đã giao"),
        });

        Assert.Equal(new[] { "B00001" }, skus);
        Assert.Equal(new[] { "SN1" }, sns);
    }

    [Fact]
    public void LocHangDoi_HaiDonTrungSku_MoiDonMotPhanTu()
    {
        var (skus, sns) = HubOutbox.LocHangDoiDemDaBan(new[]
        {
            Row("SNA", "B00009", "Đã giao"),
            Row("SNB", "B00009", "Hoàn thành"),
        });

        // +1 MỖI đơn (như đường transition của phiên) → SKU lặp 2 lần → hub +2.
        Assert.Equal(new[] { "B00009", "B00009" }, skus);
        Assert.Equal(new[] { "SNA", "SNB" }, sns);
    }
}
