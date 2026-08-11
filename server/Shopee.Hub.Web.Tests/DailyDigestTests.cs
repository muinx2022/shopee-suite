using Microsoft.Extensions.Logging.Abstractions;
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

    /// <summary>T7 (review 11/08): tin tổng kết đếm đơn ĐÃ CHUẨN BỊ hôm nay (prepared_day — PrepareStatsByDay),
    /// KHÔNG đếm ảnh chụp "còn đang chờ lúc giờ gửi": lời tin nói "hôm nay làm được gì", mà snapshot thì ngày
    /// chạy càng trơn (xử hết đơn) số càng NHỎ — nghịch hướng với chính câu chữ.</summary>
    [Fact]
    public void GomSoLieu_DemDonDaChuanBiHomNay_TheoPreparedDay_SapGiamDan()
    {
        using var db = new HubDatabase(_dataDir);
        var shopA = db.GetOrCreateShopByUsername("shop-a", "Shop A");
        var shopB = db.GetOrCreateShopByUsername("shop-b", "Shop B");
        var homNay = DailyDigest.NgayVn(DateTimeOffset.UtcNow);
        db.UpsertOrders(shopA,
        [
            // ĐÃ chuẩn bị hôm nay + đã rời trạng thái chờ (ngày chạy trơn) — VẪN phải được đếm.
            new OrderPushItem { OrderSn = "A1", Status = "Đã giao", PreparedAt = "2026-08-11T03:00:00Z", PreparedDay = homNay },
            new OrderPushItem { OrderSn = "A2", Status = HomeOverview.TrangThaiCho, PreparedAt = "2026-08-11T04:00:00Z", PreparedDay = homNay },
            new OrderPushItem { OrderSn = "A3", Status = HomeOverview.TrangThaiCho },                 // CHƯA chuẩn bị → không đếm
            new OrderPushItem { OrderSn = "A4", Status = "Đã giao", PreparedAt = "2026-08-01T03:00:00Z", PreparedDay = "2026-08-01" }, // ngày khác
        ]);
        db.UpsertOrders(shopB,
            [new OrderPushItem { OrderSn = "B1", Status = "Đã giao", PreparedAt = "2026-08-11T05:00:00Z", PreparedDay = homNay }]);

        var so = DailyDigest.GomSoLieu(db, new FleetSnapshot(), DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10));

        Assert.Equal(3, so.TongDonCho);
        Assert.Equal(new[] { ("shop-a", 2), ("shop-b", 1) }, so.TheoShop.ToArray());
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
        Assert.Contains("Đơn đã chuẩn bị hàng hôm nay: 78", text); // "ĐÃ chuẩn bị" — khớp nguồn prepared_day (T7)
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

        Assert.Contains("(chưa shop nào chuẩn bị đơn hôm nay)", text);
        Assert.DoesNotContain("null", text);
    }

    // ══════════ T8: mốc "đã gửi" ghi SAU khi tin được XỬ (OnDone) + chốt in-flight ══════════

    private DailyDigestService DungService(HubDatabase db, FleetStateService fleet, WebhookQueueService queue)
        => new(db, fleet, queue, NullLogger<DailyDigestService>.Instance);

    private static void BatTongKet(HubDatabase db)
    {
        db.SetSetting(SettingKeys.NotifyTongKetBat, "1");
        db.SetSetting(SettingKeys.NotifyWebhookTongKet, "https://example.invalid/hook");
        db.SetSetting(SettingKeys.NotifyTongKetGio, "21");
    }

    /// <summary>Restart đúng lúc không được nuốt tin: mốc bền CHỈ ghi khi worker đã xử xong (OnDone) — trước đó
    /// restart ⇒ mốc trống ⇒ nhịp đầu sau restart gửi lại. Trong lúc tin còn nằm hàng đợi, nhịp 60s KHÔNG được
    /// xếp trùng (chốt in-flight bộ nhớ).</summary>
    [Fact]
    public void MotLuot_MocChiGhiSauOnDone_VaKhongXepTrungTrongLucCho()
    {
        using var db = new HubDatabase(_dataDir);
        BatTongKet(db);
        using var fleet = new FleetStateService(db, NullLogger<FleetStateService>.Instance);
        using var queue = new WebhookQueueService(db, NullLogger<WebhookQueueService>.Instance);
        var svc = DungService(db, fleet, queue);
        var luc = LucVn("2026-08-11", 21);

        var daXep = new List<WebhookNotification>();
        svc.MotLuot(luc, tin => { daXep.Add(tin); return true; });
        var tinDau = Assert.Single(daXep);

        // Chưa xử xong → mốc bền CHƯA ghi (đây chính là chỗ bản cũ ghi TRƯỚC rồi mất tin khi restart).
        Assert.NotEqual("2026-08-11", (db.GetSetting(SettingKeys.NotifyTongKetDaGuiNgay) ?? "").Trim());

        // Nhịp 60s kế trong lúc tin còn trong hàng đợi: không xếp thêm tin thứ hai.
        svc.MotLuot(luc.AddMinutes(1), tin => { daXep.Add(tin); return true; });
        Assert.Single(daXep);

        // Worker xử xong (KỂ CẢ gửi fail — chính sách "webhook chết thì mất tin, có log" giữ nguyên) → mốc ghi,
        // các nhịp sau đọc mốc bền và im.
        tinDau.OnDone!.Invoke(false);
        Assert.Equal("2026-08-11", db.GetSetting(SettingKeys.NotifyTongKetDaGuiNgay));
        svc.MotLuot(luc.AddMinutes(2), tin => { daXep.Add(tin); return true; });
        Assert.Single(daXep);
    }

    /// <summary>Queue đầy (TryQueue false) → KHÔNG đụng mốc, KHÔNG kẹt chốt — nhịp sau thử lại từ đầu.</summary>
    [Fact]
    public void MotLuot_QueueDay_KhongGhiMoc_NhipSauThuLaiDuoc()
    {
        using var db = new HubDatabase(_dataDir);
        BatTongKet(db);
        using var fleet = new FleetStateService(db, NullLogger<FleetStateService>.Instance);
        using var queue = new WebhookQueueService(db, NullLogger<WebhookQueueService>.Instance);
        var svc = DungService(db, fleet, queue);
        var luc = LucVn("2026-08-11", 21);

        svc.MotLuot(luc, _ => false); // xếp không được (queue đầy)
        Assert.NotEqual("2026-08-11", (db.GetSetting(SettingKeys.NotifyTongKetDaGuiNgay) ?? "").Trim());

        var daXep = new List<WebhookNotification>();
        svc.MotLuot(luc.AddMinutes(1), tin => { daXep.Add(tin); return true; }); // nhịp sau THỬ LẠI được
        Assert.Single(daXep);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }
}
