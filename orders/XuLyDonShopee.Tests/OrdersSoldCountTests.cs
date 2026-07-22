using XuLyDonShopee.Core.Data;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test lõi +1 "Đã bán" theo SKU ở tầng DB: <see cref="OrdersRepository.DetectNewlyDelivered"/> (no-backfill —
/// chỉ +1 khi đơn CHUYỂN từ chưa-giao → đã-giao; đơn đã-giao-sẵn = grandfather không +1) và
/// <see cref="OrdersRepository.MarkSoldCounted"/> (cờ chống đếm trùng). KHÔNG cần Brave/hub thật.
/// </summary>
public class OrdersSoldCountTests
{
    private static SyncedOrder Order(string sn, string? status, string? sku = null)
        => new() { OrderSn = sn, Status = status, Sku = sku };

    [Fact]
    public void Detect_ChuyenSangDaGiao_CoSku_Gom_ChoDanhCoSauHub()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        // Seed: đơn đang "Chờ lấy hàng" (chưa giao).
        repo.UpsertMany(1, new[] { Order("SN1", "Chờ lấy hàng", "B00001") }, DateTime.UtcNow);

        // Sync thấy đơn đã "Đã giao" → chuyển sang đã-giao → +1.
        var r = repo.DetectNewlyDelivered(1, new[] { Order("SN1", "Đã giao", "B00001") });

        Assert.Equal(new[] { "B00001" }, r.SkusToIncrement);
        Assert.Equal(new[] { "SN1" }, r.PendingMarkOrderSns);   // đánh cờ SAU khi hub +1 OK
        Assert.Empty(r.ImmediateMarkOrderSns);
    }

    [Fact]
    public void Detect_DonMoiToanh_DaGiaoSan_Grandfather_KhongCong()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());

        // Đơn CHƯA có trong DB, lần đầu thấy đã giao ngay → grandfather (đánh cờ ngay, KHÔNG +1).
        var r = repo.DetectNewlyDelivered(1, new[] { Order("SNX", "Đã giao", "B00002") });

        Assert.Empty(r.SkusToIncrement);
        Assert.Empty(r.PendingMarkOrderSns);
        Assert.Equal(new[] { "SNX" }, r.ImmediateMarkOrderSns);
    }

    [Fact]
    public void Detect_DonCu_StatusDaDelivered_CoNULL_Grandfather_KhongCong()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        // Đơn cũ có sẵn từ trước tính năng: status CŨ đã là "Hoàn thành", cờ NULL.
        repo.UpsertMany(1, new[] { Order("SN2", "Hoàn thành", "B00003") }, DateTime.UtcNow);

        var r = repo.DetectNewlyDelivered(1, new[] { Order("SN2", "Hoàn thành", "B00003") });

        Assert.Empty(r.SkusToIncrement);
        Assert.Empty(r.PendingMarkOrderSns);
        Assert.Equal(new[] { "SN2" }, r.ImmediateMarkOrderSns);   // grandfather
    }

    [Fact]
    public void Detect_ChuyenSangDaGiao_KhongSku_DanhCoNgay_KhongCong()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Order("SN5", "Đang giao", null) }, DateTime.UtcNow);

        // Chuyển sang đã-giao nhưng KHÔNG có SKU → không +1 được → đánh cờ ngay (khỏi xét lại mãi).
        var r = repo.DetectNewlyDelivered(1, new[] { Order("SN5", "Đã giao", null) });

        Assert.Empty(r.SkusToIncrement);
        Assert.Empty(r.PendingMarkOrderSns);
        Assert.Equal(new[] { "SN5" }, r.ImmediateMarkOrderSns);
    }

    [Theory]
    [InlineData("Đã giao cho ĐVVC")]                  // mới bàn giao ĐVVC — CHƯA nhận
    [InlineData("Đã giao cho đơn vị vận chuyển")]
    [InlineData("Giao hàng không thành công")]
    [InlineData("Đang giao")]
    [InlineData("Đã hủy")]
    public void Detect_TrangThaiKhongTinhDaGiao_BoQua(string newStatus)
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Order("SN4", "Chờ lấy hàng", "B00005") }, DateTime.UtcNow);

        var r = repo.DetectNewlyDelivered(1, new[] { Order("SN4", newStatus, "B00005") });

        Assert.Empty(r.SkusToIncrement);
        Assert.Empty(r.PendingMarkOrderSns);
        Assert.Empty(r.ImmediateMarkOrderSns);
    }

    [Fact]
    public void Detect_DaDanhCo_KhongCongLaiDuSyncDocLai()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Order("SN3", "Chờ lấy hàng", "B00004") }, DateTime.UtcNow);

        // Lần 1: transition → cần +1.
        var r1 = repo.DetectNewlyDelivered(1, new[] { Order("SN3", "Đã giao", "B00004") });
        Assert.Equal(new[] { "SN3" }, r1.PendingMarkOrderSns);

        // Giả lập: UpsertMany ghi status delivered vào DB + hub +1 OK → đánh cờ.
        repo.UpsertMany(1, new[] { Order("SN3", "Đã giao", "B00004") }, DateTime.UtcNow);
        repo.MarkSoldCounted(1, new[] { "SN3" }, DateTime.UtcNow);

        // Lần 2 (sync đọc lại toàn trang): đã đếm rồi → KHÔNG +1, KHÔNG đánh cờ lại.
        var r2 = repo.DetectNewlyDelivered(1, new[] { Order("SN3", "Đã giao", "B00004") });
        Assert.Empty(r2.SkusToIncrement);
        Assert.Empty(r2.PendingMarkOrderSns);
        Assert.Empty(r2.ImmediateMarkOrderSns);
    }

    [Fact]
    public void Detect_HaiDon_TrungSku_ChuyenSangDaGiao_CongMoiDonMotLan()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[]
        {
            Order("SNA", "Chờ lấy hàng", "B00009"),
            Order("SNB", "Đang giao", "B00009"),
        }, DateTime.UtcNow);

        var r = repo.DetectNewlyDelivered(1, new[]
        {
            Order("SNA", "Đã giao", "B00009"),
            Order("SNB", "Hoàn thành", "B00009"),
        });

        // +1 MỖI đơn (không theo số lượng) → SKU lặp 2 lần → hub +2 cho mọi dòng khớp.
        Assert.Equal(new[] { "B00009", "B00009" }, r.SkusToIncrement);
        Assert.Equal(new[] { "SNA", "SNB" }, r.PendingMarkOrderSns);
    }

    [Fact]
    public void MarkSoldCounted_CoalesceGiuMocDau_KhongDeGhiDe()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Order("SN6", "Chờ lấy hàng", "B00006") }, DateTime.UtcNow);

        // Đánh cờ → transition sau đó bị bỏ qua (đã đếm).
        repo.MarkSoldCounted(1, new[] { "SN6" }, DateTime.UtcNow);
        var r = repo.DetectNewlyDelivered(1, new[] { Order("SN6", "Đã giao", "B00006") });
        Assert.Empty(r.SkusToIncrement);
        Assert.Empty(r.PendingMarkOrderSns);
        Assert.Empty(r.ImmediateMarkOrderSns);   // cờ đã set (dù chưa từng +1) → không xử lại
    }
}
