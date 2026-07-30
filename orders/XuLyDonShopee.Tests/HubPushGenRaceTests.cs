using XuLyDonShopee.Core.Data;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Tests;

/// <summary>
/// ĐUA giữa lượt đẩy hub ĐANG BAY và các chỗ reset cờ <c>hub_synced_at</c> (cặp cột "thế hệ"
/// <c>hub_push_gen</c> / <c>hub_push_gen_sent</c>).
/// <para>
/// Luồng thật: <see cref="OrdersRepository.GetForHubPush"/> chụp lô 200 đơn → POST qua tunnel (có thể hàng chục
/// giây) → <see cref="OrdersRepository.MarkHubSynced"/>. Đơn bị ĐỔI trong lúc lô đang bay (arrange xong / ghi mã
/// trả hàng / sync lại thấy "Đã hủy") mà vẫn bị đóng cờ thì dữ liệu MỚI KHÔNG BAO GIỜ lên hub được — mà client
/// sau đó còn XOÁ đơn kết thúc vì tưởng đã đẩy xong, hết đường sửa. Đây là cùng lớp lỗi "cờ đã đẩy kẹt" (v1.6.3).
/// </para>
/// </summary>
public class HubPushGenRaceTests
{
    private static SyncedOrder Don(string sn, string status = "Chờ lấy hàng") => new()
    {
        OrderSn = sn,
        Status = status,
        ItemsJson = "[]",
    };

    // ===== 1. MarkPrepared xen giữa snapshot và mark → đơn KHÔNG bị đóng cờ, còn trong hàng đợi đẩy =====
    [Fact]
    public void MarkPrepared_XenGiuaSnapshotVaMark_DonVanConChoDay()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Don("SN1") }, DateTime.UtcNow);

        var lo = repo.GetForHubPush(1);                        // snapshot (lô bắt đầu bay)
        Assert.Equal(new[] { "SN1" }, lo.Select(o => o.OrderSn));

        repo.MarkPrepared(1, "SN1", DateTime.UtcNow);          // arrange xong TRONG LÚC lô đang bay
        repo.MarkHubSynced(1, new[] { "SN1" }, DateTime.UtcNow); // hub trả OK cho bản CŨ

        // Đơn phải CÒN trong hàng đợi để lượt outbox sau đẩy lại kèm prepared_at.
        var conCho = repo.GetForHubPush(1);
        Assert.Equal(new[] { "SN1" }, conCho.Select(o => o.OrderSn));
        Assert.NotNull(Assert.Single(conCho).PreparedAt);
    }

    // ===== 2. Trạng thái đổi (sync lại thấy "Đã hủy") xen giữa snapshot và mark =====
    [Fact]
    public void TrangThaiDoi_XenGiuaSnapshotVaMark_DonVanConChoDay()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Don("SN1") }, DateTime.UtcNow);

        repo.GetForHubPush(1);
        repo.UpsertMany(1, new[] { Don("SN1", "Đã hủy") }, DateTime.UtcNow); // lượt sync mới ghi đè trạng thái
        repo.MarkHubSynced(1, new[] { "SN1" }, DateTime.UtcNow);

        var conCho = repo.GetForHubPush(1);
        Assert.Equal("Đã hủy", Assert.Single(conCho).Status);
    }

    // ===== 3. Mã trả hàng ghi xen giữa snapshot và mark → mã KHÔNG bị nuốt =====
    [Fact]
    public void MaTraHang_XenGiuaSnapshotVaMark_MaVanLenDuocHub()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Don("SN1") }, DateTime.UtcNow);

        repo.GetForHubPush(1);
        repo.SetReturnRequestCodes(1, new[] { ("SN1", "R001") });
        repo.MarkHubSynced(1, new[] { "SN1" }, DateTime.UtcNow);

        Assert.Equal("R001", Assert.Single(repo.GetForHubPush(1)).ReturnRequestCode);
    }

    // ===== 4. KHÔNG có gì xen vào → mark đóng cờ như thường (không chặn oan đường bình thường) =====
    [Fact]
    public void KhongCoGiXenVao_MarkDongCoBinhThuong()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Don("SN1"), Don("SN2") }, DateTime.UtcNow);

        repo.GetForHubPush(1);
        repo.MarkHubSynced(1, new[] { "SN1", "SN2" }, DateTime.UtcNow);

        Assert.Empty(repo.GetForHubPush(1));
    }

    // ===== 5. Đơn KHÁC trong cùng lô không bị vạ lây: chỉ đơn bị đổi mới quay lại hàng đợi =====
    [Fact]
    public void ChiDonBiDoi_QuayLaiHangDoi_DonConLaiVanDongCo()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Don("SN1"), Don("SN2") }, DateTime.UtcNow);

        repo.GetForHubPush(1);
        repo.MarkPrepared(1, "SN2", DateTime.UtcNow);            // chỉ SN2 bị đổi
        repo.MarkHubSynced(1, new[] { "SN1", "SN2" }, DateTime.UtcNow);

        Assert.Equal(new[] { "SN2" }, repo.GetForHubPush(1).Select(o => o.OrderSn));
    }

    // ===== 6. Lượt đẩy KẾ TIẾP chụp lại thế hệ mới → đóng cờ được (không kẹt vĩnh viễn) =====
    [Fact]
    public void LuotDayKeTiep_ChupLaiTheHeMoi_DongCoDuoc()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Don("SN1") }, DateTime.UtcNow);

        repo.GetForHubPush(1);
        repo.MarkPrepared(1, "SN1", DateTime.UtcNow);
        repo.MarkHubSynced(1, new[] { "SN1" }, DateTime.UtcNow);  // bị chặn (đúng)

        repo.GetForHubPush(1);                                     // lượt sau: chụp lại
        repo.MarkHubSynced(1, new[] { "SN1" }, DateTime.UtcNow);

        Assert.Empty(repo.GetForHubPush(1));
    }

    // ===== 7. GetForHubPush trả created_at (mốc đơn xuất hiện lần đầu trên máy) để đẩy lên hub =====
    [Fact]
    public void GetForHubPush_TraCreatedAt_DeHubDatFirstSeen()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        var mocTao = new DateTime(2026, 6, 10, 3, 0, 0, DateTimeKind.Utc);
        repo.UpsertMany(1, new[] { Don("SN1") }, mocTao);

        // Sync LẠI ở mốc khác → created_at GIỮ NGUYÊN mốc lần đầu (đó mới là mốc thống kê dùng).
        repo.UpsertMany(1, new[] { Don("SN1", "Đã giao") }, mocTao.AddDays(30));

        Assert.Equal(mocTao, Assert.Single(repo.GetForHubPush(1)).CreatedAt);
    }
}
