using System.Linq;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Data;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test hàm thuần <see cref="OrderPersistPipeline.ChanDoanDonKetThuc"/> (H2.5) — LÝ DO một đơn kết thúc chưa
/// dọn được — và BẤT BIẾN quan trọng nhất của đợt này: <b>chẩn đoán và luật dọn phải là MỘT</b>
/// (<c>NenXoaDonKetThuc</c> gọi thẳng hàm chẩn đoán). Property-test chạy trên MỌI tổ hợp cờ để không thể
/// thêm/bớt điều kiện ở một bên mà bên kia đứng yên.
/// <para>Kèm luôn bất biến của <see cref="HubOutbox.ConNghiaVuGhiSheet"/>: nó phải nói ĐÚNG như lối tắt
/// "đơn hủy chưa có vận đơn, chưa ghi sheet → bỏ qua" còn nằm trong vòng đẩy sheet.</para>
/// </summary>
public class ChanDoanDonKetThucTests
{
    private static GsheetPendingOrder Make(
        string? status,
        string? sku = null,
        string? cancelReason = null,
        bool daDemDaBan = false,
        bool daDayHub = false,
        bool daDayPhieuHub = false,
        bool daGhiSheet = false,
        string? trackingNumber = null,
        long? gsheetDaHuy = null,
        long? gsheetDaCoVanDon = null,
        long? gsheetDaCoUocTinh = null,
        long? gsheetDaCoDonTraHang = null,
        long? finalAmount = null,
        string? returnRequestCode = null,
        string? fileUrl = null)
        => new(
            OrderSn: "SN",
            TrackingNumber: trackingNumber,
            Sku: sku,
            ItemsJson: null,
            TotalPrice: null,
            FinalAmount: finalAmount,
            Status: status,
            StatusDescription: null,
            CancelReason: cancelReason,
            DaGhiSheet: daGhiSheet,
            FileUrl: fileUrl,
            GsheetDaHuy: gsheetDaHuy,
            GsheetDaCoVanDon: gsheetDaCoVanDon,
            GsheetDaCoUocTinh: gsheetDaCoUocTinh,
            DaDemDaBan: daDemDaBan,
            DaDayHub: daDayHub,
            DaDayPhieuHub: daDayPhieuHub,
            GsheetTab: null,
            ReturnRequestCode: returnRequestCode,
            GsheetDaCoDonTraHang: gsheetDaCoDonTraHang);

    // ══════════ Ma trận nghĩa vụ ══════════

    [Fact]
    public void ChuaSettledSheet_RaNghiaVuGhiSheet()
    {
        var thieu = OrderPersistPipeline.ChanDoanDonKetThuc(
            Make("Đã hủy"), gsheetSettled: false, hubHookActive: false, coPhieuLocalChuaDayHub: false);
        Assert.Equal(new[] { OrderPersistPipeline.NghiaVuDonKetThuc.GhiSheet }, thieu);
    }

    [Fact]
    public void HubBat_ChuaDayHub_RaNghiaVuDayHub()
    {
        var thieu = OrderPersistPipeline.ChanDoanDonKetThuc(
            Make("Đã hủy"), gsheetSettled: true, hubHookActive: true, coPhieuLocalChuaDayHub: false);
        Assert.Equal(new[] { OrderPersistPipeline.NghiaVuDonKetThuc.DayHub }, thieu);
    }

    [Fact]
    public void HubTat_KhongBaoGioRaNghiaVuDayHub()
    {
        // Hub chưa cấu hình thì "chưa đẩy hub" KHÔNG phải nghĩa vụ (máy chạy độc lập).
        var thieu = OrderPersistPipeline.ChanDoanDonKetThuc(
            Make("Đã hủy", daDayHub: false), gsheetSettled: true, hubHookActive: false, coPhieuLocalChuaDayHub: false);
        Assert.Empty(thieu);
    }

    [Fact]
    public void ConPhieuLocalChuaDayHub_RaNghiaVuDayPhieu()
    {
        var thieu = OrderPersistPipeline.ChanDoanDonKetThuc(
            Make("Đã hủy"), gsheetSettled: true, hubHookActive: true, coPhieuLocalChuaDayHub: true);
        Assert.Contains(OrderPersistPipeline.NghiaVuDonKetThuc.DayPhieuHub, thieu);
    }

    [Fact]
    public void DaGiao_CoSku_ChuaDem_RaNghiaVuDemDaBan()
    {
        var thieu = OrderPersistPipeline.ChanDoanDonKetThuc(
            Make("Đã giao", sku: "B02435"), gsheetSettled: true, hubHookActive: false, coPhieuLocalChuaDayHub: false);
        Assert.Equal(new[] { OrderPersistPipeline.NghiaVuDonKetThuc.DemDaBan }, thieu);
    }

    [Fact]
    public void DaGiao_KhongSku_KhongRaNghiaVuDemDaBan()
    {
        var thieu = OrderPersistPipeline.ChanDoanDonKetThuc(
            Make("Đã giao", sku: null), gsheetSettled: true, hubHookActive: false, coPhieuLocalChuaDayHub: false);
        Assert.Empty(thieu);
    }

    [Fact]
    public void NhieuNghiaVu_LietKeDU_KhongDungLaiOCaiDauTien()
    {
        // Đơn Đã giao, có SKU, chưa đếm, hub bật mà chưa đẩy, sheet chưa xong, phiếu chưa lên hub → ĐỦ 4 lý do.
        var thieu = OrderPersistPipeline.ChanDoanDonKetThuc(
            Make("Đã giao", sku: "B1"), gsheetSettled: false, hubHookActive: true, coPhieuLocalChuaDayHub: true);
        Assert.Equal(4, thieu.Count);
        Assert.Contains(OrderPersistPipeline.NghiaVuDonKetThuc.GhiSheet, thieu);
        Assert.Contains(OrderPersistPipeline.NghiaVuDonKetThuc.DayHub, thieu);
        Assert.Contains(OrderPersistPipeline.NghiaVuDonKetThuc.DayPhieuHub, thieu);
        Assert.Contains(OrderPersistPipeline.NghiaVuDonKetThuc.DemDaBan, thieu);
    }

    [Fact]
    public void MoiNghiaVuDeuCoNhanTiengViet()
    {
        foreach (var nv in System.Enum.GetValues<OrderPersistPipeline.NghiaVuDonKetThuc>())
        {
            var nhan = OrderPersistPipeline.MoTaNghiaVu(nv);
            Assert.False(string.IsNullOrWhiteSpace(nhan));
            Assert.NotEqual(nv.ToString(), nhan); // không rơi về nhánh fallback ToString()
        }
    }

    // ══════════ BẤT BIẾN: chẩn đoán ⇔ luật dọn ══════════

    /// <summary>
    /// Với MỌI tổ hợp (trạng thái × sku × 3 cờ vào × 2 cờ DB), đơn KẾT THÚC phải thỏa:
    /// <c>NenXoaDonKetThuc == (ChanDoanDonKetThuc rỗng)</c>. Đây là điều plan đòi — hai luật không được trôi lệch.
    /// </summary>
    [Fact]
    public void ChanDoanRong_TuongDuong_NenXoa_TrenMoiToHop()
    {
        string?[] trangThai = { "Đã giao", "Đã hủy", "Chờ lấy hàng", "Đang giao", null };
        string?[] skus = { null, "B02435" };
        bool[] co = { false, true };

        foreach (var st in trangThai)
        foreach (var sku in skus)
        foreach (var daDem in co)
        foreach (var daDayHub in co)
        foreach (var settled in co)
        foreach (var hubBat in co)
        foreach (var phieuChuaDay in co)
        {
            var p = Make(st, sku: sku, cancelReason: st == "Đã hủy" ? "Khách đổi ý" : null,
                daDemDaBan: daDem, daDayHub: daDayHub);
            var nenXoa = OrderPersistPipeline.NenXoaDonKetThuc(p, settled, hubBat, phieuChuaDay);
            var thieu = OrderPersistPipeline.ChanDoanDonKetThuc(p, settled, hubBat, phieuChuaDay);

            if (OrderPersistPipeline.LaDonKetThuc(p))
            {
                Assert.Equal(nenXoa, thieu.Count == 0);
            }
            else
            {
                Assert.False(nenXoa); // đơn chưa kết thúc → không bao giờ dọn (dù chẩn đoán rỗng)
            }
        }
    }

    // ══════════ BẤT BIẾN: ConNghiaVuGhiSheet khớp lối tắt trong vòng đẩy sheet ══════════

    [Fact]
    public void DonHuy_ChuaVanDon_ChuaGhiSheet_KhongConNghiaVuSheet()
    {
        // Đây chính là lối tắt còn nằm trong PushOrdersToGsheetAsync (bỏ qua trước khi đọc đĩa).
        var p = Make("Đã hủy", cancelReason: "Khách đổi ý", trackingNumber: null, daGhiSheet: false);
        Assert.False(HubOutbox.ConNghiaVuGhiSheet(p, coFileBoSung: false));
        Assert.False(HubOutbox.ConNghiaVuGhiSheet(p, coFileBoSung: true)); // kể cả khi có file: vẫn không ghi
    }

    [Fact]
    public void ChuaGhiSheet_ConNghiaVuSheet()
    {
        var p = Make("Chờ lấy hàng", daGhiSheet: false);
        Assert.True(HubOutbox.ConNghiaVuGhiSheet(p, coFileBoSung: false));
    }

    [Fact]
    public void DaGhiSheet_DuCo_KhongConNghiaVuSheet()
    {
        // Đã ghi dòng + 4 cờ "đã đẩy" khớp trạng thái hiện tại + không có gì mới → xong nghĩa vụ.
        var p = Make("Chờ lấy hàng", daGhiSheet: true, trackingNumber: "SPX1",
            gsheetDaHuy: 0, gsheetDaCoVanDon: 1, gsheetDaCoUocTinh: 1, gsheetDaCoDonTraHang: 1,
            finalAmount: 1000, returnRequestCode: "YC1");
        Assert.False(HubOutbox.ConNghiaVuGhiSheet(p, coFileBoSung: false));
    }

    [Theory]
    // Mỗi ca: một thứ VỪA xuất hiện so với lần đẩy trước → phải đẩy lại (còn nghĩa vụ sheet).
    [InlineData("van-don")]
    [InlineData("uoc-tinh")]
    [InlineData("ma-tra")]
    [InlineData("huy-doi")]
    [InlineData("file-phieu")]
    public void ThuMoiXuatHien_ConNghiaVuSheet(string ca)
    {
        var p = ca switch
        {
            "van-don" => Make("Chờ lấy hàng", daGhiSheet: true, trackingNumber: "SPX1",
                gsheetDaHuy: 0, gsheetDaCoVanDon: null, gsheetDaCoUocTinh: 1, gsheetDaCoDonTraHang: 1),
            "uoc-tinh" => Make("Chờ lấy hàng", daGhiSheet: true, trackingNumber: "SPX1", finalAmount: 5000,
                gsheetDaHuy: 0, gsheetDaCoVanDon: 1, gsheetDaCoUocTinh: null, gsheetDaCoDonTraHang: 1),
            "ma-tra" => Make("Chờ lấy hàng", daGhiSheet: true, trackingNumber: "SPX1", returnRequestCode: "YC9",
                gsheetDaHuy: 0, gsheetDaCoVanDon: 1, gsheetDaCoUocTinh: 1, gsheetDaCoDonTraHang: null),
            "huy-doi" => Make("Đã hủy", cancelReason: "Khách đổi ý", daGhiSheet: true, trackingNumber: "SPX1",
                gsheetDaHuy: 0, gsheetDaCoVanDon: 1, gsheetDaCoUocTinh: 1, gsheetDaCoDonTraHang: 1),
            _ => Make("Chờ lấy hàng", daGhiSheet: true, trackingNumber: "SPX1",
                gsheetDaHuy: 0, gsheetDaCoVanDon: 1, gsheetDaCoUocTinh: 1, gsheetDaCoDonTraHang: 1),
        };

        Assert.True(HubOutbox.ConNghiaVuGhiSheet(p, coFileBoSung: ca == "file-phieu"));
    }
}
