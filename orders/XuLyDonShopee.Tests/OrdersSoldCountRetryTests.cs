using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Data;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test HÀNG ĐỢI đếm "Đã bán" theo DB (<see cref="OrdersRepository.GetForSoldCountRetry"/>) — đường THỬ LẠI của
/// vòng chờ đẩy, bù cho chỗ hổng của <see cref="OrdersRepository.DetectNewlyDelivered"/> (chỉ thấy đơn CHUYỂN
/// trạng thái). Gồm cả test HỒI QUY đúng kịch bản mất đếm vĩnh viễn khi hub lỗi.
/// </summary>
public class OrdersSoldCountRetryTests
{
    private static SyncedOrder Order(string sn, string? status, string? sku = null)
        => new() { OrderSn = sn, Status = status, Sku = sku };

    [Fact]
    public void HangDoi_TraDonChuaDem_CoSku()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Order("SN1", "Đã giao", "B00001") }, DateTime.UtcNow);

        var q = repo.GetForSoldCountRetry(1);

        var row = Assert.Single(q);
        Assert.Equal("SN1", row.OrderSn);
        Assert.Equal("B00001", row.Sku);
        Assert.Equal("Đã giao", row.Status);
    }

    [Fact]
    public void HangDoi_BoDonDaDem()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Order("SN1", "Đã giao", "B00001") }, DateTime.UtcNow);
        repo.MarkSoldCounted(1, new[] { "SN1" }, DateTime.UtcNow);

        Assert.Empty(repo.GetForSoldCountRetry(1));
    }

    [Fact]
    public void HangDoi_BoDonKhongSku()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[]
        {
            Order("SN1", "Đã giao", null),      // không có SKU → không +1 được
            Order("SN2", "Đã giao", "   "),     // SKU toàn khoảng trắng → coi như không có
            Order("SN3", "Đã giao", "B00003"),
        }, DateTime.UtcNow);

        var q = repo.GetForSoldCountRetry(1);

        Assert.Equal(new[] { "SN3" }, q.Select(x => x.OrderSn));
    }

    [Fact]
    public void HangDoi_ChiLayDonCuaDungTaiKhoan()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Order("SN1", "Đã giao", "B00001") }, DateTime.UtcNow);
        repo.UpsertMany(2, new[] { Order("SN2", "Đã giao", "B00002") }, DateTime.UtcNow);

        Assert.Equal(new[] { "SN1" }, repo.GetForSoldCountRetry(1).Select(x => x.OrderSn));
        Assert.Equal(new[] { "SN2" }, repo.GetForSoldCountRetry(2).Select(x => x.OrderSn));
    }

    [Fact]
    public void HangDoi_LocTheoThoiGian_BoDonVuaGhi()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        var luc = DateTime.UtcNow;
        repo.UpsertMany(1, new[] { Order("SN1", "Đã giao", "B00001") }, luc);

        // Chốt chống ĐẾM ĐÔI: đơn VỪA ghi (chưa nguội) KHÔNG được worker nhặt — luồng phiên có thể đang
        // đánh cờ grandfather cho chính nó ngay sau UpsertMany.
        Assert.Empty(repo.GetForSoldCountRetry(1, luc.AddMinutes(-1)));

        // Đã nguội → nhặt được.
        Assert.Single(repo.GetForSoldCountRetry(1, luc.AddMinutes(1)));
    }

    /// <summary>
    /// <b>HỒI QUY lỗi MẤT ĐẾM VĨNH VIỄN.</b> Kịch bản: đơn chuyển sang đã-giao → phiên gọi hub +1 nhưng hub LỖI
    /// nên KHÔNG đánh cờ <c>sold_counted_at</c>; DB thì ĐÃ lưu trạng thái đã-giao (UpsertMany chạy trước). Lượt sync
    /// sau <see cref="OrdersRepository.DetectNewlyDelivered"/> không còn thấy "chuyển trạng thái" → xếp vào
    /// grandfather (đánh cờ, KHÔNG +1) = mất đếm. Hàng đợi theo DB PHẢI vẫn trả đơn đó để vòng chờ đếm bù.
    /// </summary>
    [Fact]
    public void HoiQuy_HubLoiKhongMark_DetectMatDauVet_NhungHangDoiVanTra()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Order("SN1", "Chờ lấy hàng", "B00001") }, DateTime.UtcNow);

        // ── Lượt sync 1: quét thấy đã giao → transition → cần +1 ──
        var r1 = repo.DetectNewlyDelivered(1, new[] { Order("SN1", "Đã giao", "B00001") });
        Assert.Equal(new[] { "B00001" }, r1.SkusToIncrement);
        Assert.Equal(new[] { "SN1" }, r1.PendingMarkOrderSns);
        // UpsertMany ghi trạng thái đã-giao vào DB; hub LỖI nên KHÔNG gọi MarkSoldCounted.
        repo.UpsertMany(1, new[] { Order("SN1", "Đã giao", "B00001") }, DateTime.UtcNow);

        // ── Lượt sync 2: DetectNewlyDelivered không còn thấy transition → grandfather, KHÔNG +1 ──
        var r2 = repo.DetectNewlyDelivered(1, new[] { Order("SN1", "Đã giao", "B00001") });
        Assert.Empty(r2.SkusToIncrement);
        Assert.Empty(r2.PendingMarkOrderSns);
        Assert.Equal(new[] { "SN1" }, r2.ImmediateMarkOrderSns); // ← chính là chỗ MẤT ĐẾM của đường cũ

        // ── Vòng chờ đẩy: hàng đợi theo DB vẫn giữ đơn này → đếm bù được ──
        var hangDoi = repo.GetForSoldCountRetry(1);
        Assert.Equal(new[] { "SN1" }, hangDoi.Select(x => x.OrderSn));

        var (skus, sns) = HubOutbox.LocHangDoiDemDaBan(hangDoi);
        Assert.Equal(new[] { "B00001" }, skus);
        Assert.Equal(new[] { "SN1" }, sns);

        // Đếm bù xong (hub OK) → đánh cờ → hàng đợi sạch, KHÔNG đếm lại lần nữa.
        repo.MarkSoldCounted(1, sns, DateTime.UtcNow);
        Assert.Empty(repo.GetForSoldCountRetry(1));
    }

    // ===== Các hàm COUNT dùng cho số tồn hiển thị + quyết định có chạy lượt đẩy không =====

    [Fact]
    public void CountForHubPush_DemDungDonChuaDayHub()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[]
        {
            Order("SN1", "Chờ lấy hàng", "B00001"),
            Order("SN2", "Chờ lấy hàng", "B00002"),
        }, DateTime.UtcNow);

        Assert.Equal(2, repo.CountForHubPush(1));
        Assert.Equal(repo.GetForHubPush(1).Count, repo.CountForHubPush(1));

        repo.MarkHubSynced(1, new[] { "SN1" }, DateTime.UtcNow);
        Assert.Equal(1, repo.CountForHubPush(1));
    }

    [Fact]
    public void CountForHubSlipPush_KhopVoiGetForHubSlipPush()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[]
        {
            new SyncedOrder { OrderSn = "SN1", Status = "Chờ lấy hàng", TrackingNumber = "VD1" },
            new SyncedOrder { OrderSn = "SN2", Status = "Chờ lấy hàng" },   // chưa có vận đơn → không tính
        }, DateTime.UtcNow);

        Assert.Equal(0, repo.CountForHubSlipPush(1));   // chưa đơn nào lên hub

        repo.MarkHubSynced(1, new[] { "SN1", "SN2" }, DateTime.UtcNow);
        Assert.Equal(1, repo.CountForHubSlipPush(1));
        Assert.Equal(repo.GetForHubSlipPush(1).Count, repo.CountForHubSlipPush(1));

        repo.MarkHubSlipSynced(1, new[] { "SN1" }, DateTime.UtcNow);
        Assert.Equal(0, repo.CountForHubSlipPush(1));
    }

    [Fact]
    public void CountForGsheetPush_DemDonChuaGhiSheet()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[]
        {
            Order("SN1", "Chờ lấy hàng", "B00001"),
            Order("SN2", "Chờ lấy hàng", "B00002"),
        }, DateTime.UtcNow);

        Assert.Equal(2, repo.CountForGsheetPush(1));

        repo.MarkGsheetSynced(1, "SN1", null, daHuy: false, coVanDon: false, coUocTinh: false, coDonTraHang: false, tab: "Tháng 07-2026", at: DateTime.UtcNow);
        Assert.Equal(1, repo.CountForGsheetPush(1));
    }
}
