using Shopee.Core.Coordination;
using Shopee.Hub;
using Shopee.Hub.Web.Services;
using XuLyDonShopee.Core.Services;

namespace Shopee.Hub.Web.Tests;

/// <summary>
/// Tin TỔNG KẾT CUỐI NGÀY (H2.1). Điểm dễ vỡ nhất KHÔNG phải nội dung tin mà là "đúng 1 tin/ngày kể cả khi hub
/// restart quanh giờ gửi" — mốc đã-gửi nằm ở <c>settings</c> (bền qua restart), khác cảnh báo máy offline giữ
/// trạng thái trong bộ nhớ.
/// </summary>
public sealed class DailyDigestTests : IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "hub-digest-test-" + Guid.NewGuid().ToString("N"));

    /// <summary>Mốc UTC ứng với <paramref name="gioVn"/> giờ ngày <paramref name="ngayVn"/> theo giờ Việt Nam.</summary>
    private static DateTimeOffset LucVn(string ngayVn, int gioVn)
        => new DateTimeOffset(DateTime.Parse(ngayVn).AddHours(gioVn), TimeSpan.FromHours(7)).ToUniversalTime();

    // ══════════ Mốc "đã gửi ngày d" ══════════

    [Fact]
    public void ChuaToiGio_KhongGui()
    {
        Assert.False(DailyDigest.DenLuotGui(LucVn("2026-08-06", 20), 21, ngayDaGui: null, out var ngay));
        Assert.Equal("2026-08-06", ngay);
    }

    [Fact]
    public void DungGio_ChuaGuiNgayNao_ThiGui()
    {
        Assert.True(DailyDigest.DenLuotGui(LucVn("2026-08-06", 21), 21, ngayDaGui: "", out var ngay));
        Assert.Equal("2026-08-06", ngay);
    }

    [Fact]
    public void DaGuiHomNay_ThiThoi_DuGoiLaiBaoNhieuLan()
    {
        // Chính là ca hub restart lúc 21:05: mốc đọc từ DB nên lượt quét đầu sau restart KHÔNG bắn tin thứ hai.
        Assert.False(DailyDigest.DenLuotGui(LucVn("2026-08-06", 21), 21, "2026-08-06", out _));
        Assert.False(DailyDigest.DenLuotGui(LucVn("2026-08-06", 22), 21, "2026-08-06", out _));
        Assert.False(DailyDigest.DenLuotGui(LucVn("2026-08-06", 23), 21, "2026-08-06", out _));
    }

    [Fact]
    public void HubTatQuaGioGui_LenLaiTrongNgay_VanGuiBu()
    {
        // Điều kiện là "đã QUA giờ hẹn", không phải "bằng đúng giờ hẹn" — muộn còn hơn mất tin cả ngày.
        Assert.True(DailyDigest.DenLuotGui(LucVn("2026-08-06", 23), 21, "2026-08-05", out var ngay));
        Assert.Equal("2026-08-06", ngay);
    }

    [Fact]
    public void SangNgayMoi_GuiTinCuaNgayMoi()
    {
        Assert.False(DailyDigest.DenLuotGui(LucVn("2026-08-07", 0), 21, "2026-08-06", out _)); // 0h chưa tới giờ
        Assert.True(DailyDigest.DenLuotGui(LucVn("2026-08-07", 21), 21, "2026-08-06", out var ngay));
        Assert.Equal("2026-08-07", ngay);
    }

    [Fact]
    public void NgayTinhTheoGioVIETNAM_KhongTheoGioMayChu()
    {
        // 2026-08-06 23:30 giờ VN = 16:30 UTC cùng ngày; còn 00:30 giờ VN ngày 07 = 17:30 UTC ngày 06 —
        // lấy nhầm ngày UTC là gửi 2 tin cho ngày 06 rồi bỏ mất ngày 07.
        Assert.Equal("2026-08-06", DailyDigest.NgayVn(LucVn("2026-08-06", 23)));
        Assert.Equal("2026-08-07", DailyDigest.NgayVn(LucVn("2026-08-07", 0)));
    }

    [Theory]
    [InlineData(null, DailyDigest.GioMacDinh)]
    [InlineData("", DailyDigest.GioMacDinh)]
    [InlineData("rác", DailyDigest.GioMacDinh)]
    [InlineData("7", 7)]
    [InlineData(" 23 ", 23)]
    [InlineData("-5", DailyDigest.GioMin)]
    [InlineData("99", DailyDigest.GioMax)]
    public void KepGio(string? raw, int mong)
        => Assert.Equal(mong, DailyDigest.KepGio(raw));

    // ══════════ Gom số liệu ══════════

    [Fact]
    public void GomSoLieu_DemDonChoTheoShop_TheoNgayVN_VaSapGiamDan()
    {
        using var db = new HubDatabase(_dataDir);
        var shopA = db.GetOrCreateShopByUsername("shop-a", "Shop A");
        var shopB = db.GetOrCreateShopByUsername("shop-b", "Shop B");
        db.UpsertOrders(shopA,
        [
            new OrderPushItem { OrderSn = "A1", Status = HomeOverview.TrangThaiCho },
            new OrderPushItem { OrderSn = "A2", Status = HomeOverview.TrangThaiCho },
            new OrderPushItem { OrderSn = "A3", Status = "Đã giao" },   // không phải "chờ" → không đếm
        ]);
        db.UpsertOrders(shopB, [new OrderPushItem { OrderSn = "B1", Status = HomeOverview.TrangThaiCho }]);

        var so = DailyDigest.GomSoLieu(db, new FleetSnapshot(), DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10));

        Assert.Equal(3, so.TongDonCho);
        Assert.Equal(new[] { ("shop-a", 2), ("shop-b", 1) }, so.TheoShop.ToArray());
        // Số này phải KHỚP thẻ "Đơn chờ hôm nay" của trang chủ (cùng truy vấn).
        Assert.Equal(HomeOverview.DonChoHomNay(db, DateTimeOffset.UtcNow), so.TongDonCho);
    }

    [Fact]
    public void GomSoLieu_DemMaTraMoi_ChiKhiMaTHAYDOI_TrenDonDaCo()
    {
        using var db = new HubDatabase(_dataDir);
        var shop = db.GetOrCreateShopByUsername("shop-a", "Shop A");

        // Lần 1: đơn lên hub ĐÃ mang sẵn mã → KHÔNG tính là mã mới (dựng lại hub / đẩy bù cả kho sẽ dồn mã cũ).
        db.UpsertOrders(shop, [new OrderPushItem { OrderSn = "A1", Status = "Đã giao", ReturnRequestCode = "YC-1" }]);
        var (from, to) = GioVietNam.KhoangNgayUtc(DateTimeOffset.UtcNow);
        Assert.Equal(0, db.CountReturnCodesInRange(from, to));

        // Đẩy lại đúng mã cũ → vẫn 0 (không đổi thì không phải mã mới).
        db.UpsertOrders(shop, [new OrderPushItem { OrderSn = "A1", Status = "Đã giao", ReturnRequestCode = "YC-1" }]);
        Assert.Equal(0, db.CountReturnCodesInRange(from, to));

        // Đơn khác đã có trên hub, giờ MỚI có mã → đếm 1.
        db.UpsertOrders(shop, [new OrderPushItem { OrderSn = "A2", Status = "Đã giao" }]);
        db.UpsertOrders(shop, [new OrderPushItem { OrderSn = "A2", Status = "Đã giao", ReturnRequestCode = "YC-2" }]);
        Assert.Equal(1, db.CountReturnCodesInRange(from, to));

        var so = DailyDigest.GomSoLieu(db, new FleetSnapshot(), DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10));
        Assert.Equal(1, so.MaTraMoi);
    }

    [Fact]
    public void GomSoLieu_DemShopCanhBaoDiaChi_DangMO()
    {
        using var db = new HubDatabase(_dataDir);
        db.UpsertPickupAlert("sub@shopee", "shop-a", "Hà Nội", "may-1");
        db.UpsertPickupAlert("sub@shopee", "shop-b", "Hà Nội", "may-1");
        db.DismissPickupAlert("sub@shopee", "shop-b", "may-1");   // đã bấm X → không còn tính

        var so = DailyDigest.GomSoLieu(db, new FleetSnapshot(), DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10));
        Assert.Equal(1, so.ShopCanhBaoDiaChi);
    }

    [Fact]
    public void GomSoLieu_DemMayOffline_TheoNguongTruyenVao()
    {
        using var db = new HubDatabase(_dataDir);
        var now = DateTimeOffset.UtcNow;
        var fleet = new FleetSnapshot
        {
            Machines =
            [
                new MachinePresence { MachineId = "m1", Hostname = "PC1", LastSeen = now.AddMinutes(-1) },
                new MachinePresence { MachineId = "m2", Hostname = "PC2", LastSeen = now.AddMinutes(-30) },
                new MachinePresence { MachineId = "m3", Hostname = "PC3", LastSeen = now.AddHours(-5) },
            ],
        };

        Assert.Equal(2, DailyDigest.GomSoLieu(db, fleet, now, TimeSpan.FromMinutes(10)).MayOffline);
        Assert.Equal(1, DailyDigest.GomSoLieu(db, fleet, now, TimeSpan.FromMinutes(60)).MayOffline);
    }

    // ══════════ Nội dung tin ══════════

    [Fact]
    public void TaoTin_CoDuCacMuc_VaCatTopShop()
    {
        var shops = Enumerable.Range(1, 12).Select(i => ($"shop-{i:00}", 13 - i)).ToList();
        var text = OrderNotifyService.TaoTinNhanTongKetNgay(
            new DateTime(2026, 8, 6, 21, 0, 0), tongDonCho: 78, theoShop: shops,
            maTraMoi: 4, shopCanhBaoDiaChi: 2, mayOffline: 1);

        Assert.Contains("TỔNG KẾT NGÀY 06/08/2026", text);
        Assert.Contains("Đơn chuẩn bị hàng phát sinh hôm nay: 78", text);
        Assert.Contains("• shop-01 — 12 đơn", text);
        Assert.Contains("… và 2 shop nữa.", text);      // 12 shop, in 10
        Assert.DoesNotContain("shop-11", text);
        Assert.Contains("Mã trả hàng mới hôm nay: 4", text);
        Assert.Contains("Shop còn cảnh báo địa chỉ lấy hàng: 2", text);
        Assert.Contains("Máy client đang offline: 1", text);
    }

    [Fact]
    public void TaoTin_KhongShopNao_VanRaTinDocDuoc()
    {
        var text = OrderNotifyService.TaoTinNhanTongKetNgay(
            new DateTime(2026, 8, 6, 21, 0, 0), 0, theoShop: null, 0, 0, 0);

        Assert.Contains("(chưa shop nào có đơn hôm nay)", text);
        Assert.DoesNotContain("null", text);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }
}
