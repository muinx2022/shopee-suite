using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Data;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test hàm thuần <see cref="AccountSession.NenXoaDonKetThuc"/> — quyết định một đơn KẾT THÚC (Đã giao / Đã hủy)
/// có được DỌN khỏi app chưa. Chỉ xóa khi đã settled GSheet + (Đã giao có SKU thì đã đếm "Đã bán") + (hub bật thì
/// đã đẩy hub). Nghi ngờ thì GIỮ. Đơn trung gian → không bao giờ xóa.
/// </summary>
public class AccountSessionCleanupTests
{
    /// <summary>Dựng nhanh một <see cref="GsheetPendingOrder"/> với các trường cần cho ma trận dọn.</summary>
    private static GsheetPendingOrder Make(
        string sn,
        string? status,
        string? sku = null,
        string? cancelReason = null,
        bool daDemDaBan = false,
        bool daDayHub = false,
        bool daDayPhieuHub = false)
        => new(
            OrderSn: sn,
            TrackingNumber: null,
            Sku: sku,
            TotalPrice: null,
            Status: status,
            StatusDescription: null,
            CancelReason: cancelReason,
            DaGhiSheet: false,
            FileUrl: null,
            GsheetDaHuy: null,
            GsheetDaCoVanDon: null,
            DaDemDaBan: daDemDaBan,
            DaDayHub: daDayHub,
            DaDayPhieuHub: daDayPhieuHub,
            GsheetTab: null);

    [Fact]
    public void Terminal_Settled_KhongSku_HubTat_Xoa()
    {
        var p = Make("SN", status: "Đã hủy", cancelReason: "Khách đổi ý");
        Assert.True(AccountSession.NenXoaDonKetThuc(p, gsheetSettled: true, hubHookActive: false, coPhieuLocalChuaDayHub: false));
    }

    [Fact]
    public void DaGiao_CoSku_ChuaDem_Giu()
    {
        var p = Make("SN", status: "Đã giao", sku: "B02435", daDemDaBan: false);
        Assert.False(AccountSession.NenXoaDonKetThuc(p, gsheetSettled: true, hubHookActive: false, coPhieuLocalChuaDayHub: false));
    }

    [Fact]
    public void DaGiao_CoSku_DaDem_Xoa()
    {
        var p = Make("SN", status: "Đã giao", sku: "B02435", daDemDaBan: true);
        Assert.True(AccountSession.NenXoaDonKetThuc(p, gsheetSettled: true, hubHookActive: false, coPhieuLocalChuaDayHub: false));
    }

    [Fact]
    public void DaGiao_KhongSku_Xoa()
    {
        // Đã giao KHÔNG có SKU → không cần đếm "Đã bán" → xóa ngay khi settled.
        var p = Make("SN", status: "Đã giao", sku: null);
        Assert.True(AccountSession.NenXoaDonKetThuc(p, gsheetSettled: true, hubHookActive: false, coPhieuLocalChuaDayHub: false));
    }

    [Fact]
    public void DaHuy_Settled_Xoa()
    {
        var p = Make("SN", status: "Đã hủy");
        Assert.True(AccountSession.NenXoaDonKetThuc(p, gsheetSettled: true, hubHookActive: false, coPhieuLocalChuaDayHub: false));
    }

    [Fact]
    public void ChuaSettled_Giu()
    {
        var p = Make("SN", status: "Đã hủy");
        Assert.False(AccountSession.NenXoaDonKetThuc(p, gsheetSettled: false, hubHookActive: false, coPhieuLocalChuaDayHub: false));
    }

    [Fact]
    public void HubBat_ChuaDayHub_Giu()
    {
        var p = Make("SN", status: "Đã giao", sku: null, daDayHub: false);
        Assert.False(AccountSession.NenXoaDonKetThuc(p, gsheetSettled: true, hubHookActive: true, coPhieuLocalChuaDayHub: false));
    }

    [Fact]
    public void HubBat_DaDayHub_Xoa()
    {
        var p = Make("SN", status: "Đã giao", sku: null, daDayHub: true);
        Assert.True(AccountSession.NenXoaDonKetThuc(p, gsheetSettled: true, hubHookActive: true, coPhieuLocalChuaDayHub: false));
    }

    [Fact]
    public void CoPhieuLocalChuaDayHub_Giu()
    {
        // Đơn kết thúc đã settled + hub off nhưng CÒN phiếu local hợp lệ chưa đẩy hub → GIỮ (đợi phiếu lên hub).
        var p = Make("SN", status: "Đã hủy");
        Assert.False(AccountSession.NenXoaDonKetThuc(p, gsheetSettled: true, hubHookActive: false, coPhieuLocalChuaDayHub: true));
    }

    [Fact]
    public void HubBat_DaDayHub_CoPhieuLocalChuaDayPhieu_Giu()
    {
        // Đơn đã đẩy hub (DaDayHub) nhưng phiếu chưa đẩy (coPhieuLocalChuaDayHub=true) → GIỮ tới khi phiếu lên hub.
        var p = Make("SN", status: "Đã giao", sku: null, daDayHub: true);
        Assert.False(AccountSession.NenXoaDonKetThuc(p, gsheetSettled: true, hubHookActive: true, coPhieuLocalChuaDayHub: true));
    }

    [Fact]
    public void HubBat_DaDayHub_DaDayPhieu_Xoa()
    {
        // Đã đẩy hub + phiếu cũng đã đẩy (coPhieuLocalChuaDayHub=false) → đủ điều kiện dọn.
        var p = Make("SN", status: "Đã giao", sku: null, daDayHub: true, daDayPhieuHub: true);
        Assert.True(AccountSession.NenXoaDonKetThuc(p, gsheetSettled: true, hubHookActive: true, coPhieuLocalChuaDayHub: false));
    }

    [Theory]
    [InlineData("Đang giao")]
    [InlineData("Đang vận chuyển")]
    [InlineData("Chờ xác nhận")]
    [InlineData("Chuẩn bị hàng")]
    [InlineData("Chờ lấy hàng")]
    public void TrungGian_KhongXoa(string status)
    {
        // Đơn CHƯA kết thúc → KHÔNG bao giờ xóa dù settled + hub off.
        var p = Make("SN", status: status);
        Assert.False(AccountSession.NenXoaDonKetThuc(p, gsheetSettled: true, hubHookActive: false, coPhieuLocalChuaDayHub: false));
    }
}
