using System;
using System.Linq;
using XuLyDonShopee.Core.Data;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Tests;

/// <summary>
/// <b>T5 (review 11/08): giá trị ĐỔI — không chỉ NULL→có — phải mở lại cờ đẩy hub/sheet.</b>
/// <para>Shopee đổi mã vận đơn A→B / điều chỉnh "Số tiền cuối cùng" thì lượt sync ghi đè local; bản trước KHÔNG
/// reset cờ nên hub/sheet giữ giá trị CŨ vĩnh viễn — đơn kết thúc sau đó bị dọn khỏi client là hết đường sửa.
/// Chiều ngược lại cũng phải giữ: giá trị Y HỆT thì KHÔNG mở lại cờ (không đẩy lại vô ích mỗi lượt sync).</para>
/// </summary>
public class DoiGiaTriResetCoTests
{
    private static SyncedOrder Don(string sn, string? tracking = null, long? finalAmount = null) => new()
    {
        OrderSn = sn,
        Status = "Chờ lấy hàng",
        ItemsJson = "[]",
        TrackingNumber = tracking,
        FinalAmount = finalAmount,
    };

    /// <summary>Mô phỏng một lượt outbox trọn vẹn: chụp lô đang chờ rồi đóng cờ hub cho cả lô.</summary>
    private static void DongCoHub(OrdersRepository repo)
    {
        var lo = repo.GetForHubPush(1);
        repo.MarkHubSynced(1, lo.Select(o => o.OrderSn).ToList(), DateTime.UtcNow);
    }

    [Fact]
    public void TrackingDoi_MoLaiCoHub_VaResetCoVanDonSheet()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Don("SN1", tracking: "SPX-A") }, DateTime.UtcNow);
        DongCoHub(repo);
        Assert.Empty(repo.GetForHubPush(1));

        // Sheet đã từng gửi kèm vận đơn A (cờ gsheet_da_co_van_don = 1).
        var gen = Assert.Single(repo.GetForGsheetPush(1)).GsheetPushGen;
        repo.MarkGsheetSynced(1, "SN1", null, daHuy: false, coVanDon: true, coUocTinh: false,
            coDonTraHang: false, "T8", DateTime.UtcNow, gen);
        Assert.Equal(1L, Assert.Single(repo.GetForGsheetPush(1)).GsheetDaCoVanDon);

        // Shopee ĐỔI mã vận đơn A→B.
        repo.UpsertMany(1, new[] { Don("SN1", tracking: "SPX-B") }, DateTime.UtcNow);

        var choHub = Assert.Single(repo.GetForHubPush(1));       // cờ hub MỞ LẠI
        Assert.Equal("SPX-B", choHub.TrackingNumber);            // và mang giá trị MỚI
        Assert.Null(Assert.Single(repo.GetForGsheetPush(1)).GsheetDaCoVanDon); // sheet sẽ re-push vận đơn mới
    }

    [Fact]
    public void TrackingGiuNguyen_KhongMoLaiCo_KhongDayLaiVoIch()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Don("SN1", tracking: "SPX-A") }, DateTime.UtcNow);
        DongCoHub(repo);

        var gen = Assert.Single(repo.GetForGsheetPush(1)).GsheetPushGen;
        repo.MarkGsheetSynced(1, "SN1", null, daHuy: false, coVanDon: true, coUocTinh: false,
            coDonTraHang: false, "T8", DateTime.UtcNow, gen);

        // Lượt sync sau đọc lại Y HỆT giá trị cũ — chi phí đẩy lại không được tăng.
        repo.UpsertMany(1, new[] { Don("SN1", tracking: "SPX-A") }, DateTime.UtcNow);

        Assert.Empty(repo.GetForHubPush(1));
        Assert.Equal(1L, Assert.Single(repo.GetForGsheetPush(1)).GsheetDaCoVanDon);
    }

    [Fact]
    public void FinalAmountDoi_MoLaiCoHub()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Don("SN1", finalAmount: 100_000) }, DateTime.UtcNow);
        DongCoHub(repo);
        Assert.Empty(repo.GetForHubPush(1));

        repo.UpsertMany(1, new[] { Don("SN1", finalAmount: 120_000) }, DateTime.UtcNow); // Shopee điều chỉnh

        Assert.Equal(120_000, Assert.Single(repo.GetForHubPush(1)).FinalAmount);
    }

    /// <summary>Bất biến "MỞ CỜ NÀO THÌ +1 THẾ HỆ ĐÍCH ĐÓ" (phản biện 11/08): lô đẩy sheet FIRE-AND-FORGET chạy
    /// song song với vòng shop — tracking đổi GIỮA lúc lô đang bay mà không +1 <c>gsheet_push_gen</c> thì
    /// <c>MarkGsheetSynced</c> mang thế hệ CŨ vẫn khớp, đóng lại đúng cái cờ vừa mở ⇒ vận đơn MỚI không bao giờ
    /// lên sheet (đơn dọn xong là hết đường sửa).</summary>
    [Fact]
    public void TrackingDoi_GiuaLucLoSheetDangBay_LoCuKhongDongLaiCoVuaMo()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Don("SN1", tracking: "SPX-A") }, DateTime.UtcNow);
        DongCoHub(repo);

        // Lô sheet CHỤP thế hệ lúc bắt đầu bay (mang tracking A).
        var genLucChup = Assert.Single(repo.GetForGsheetPush(1)).GsheetPushGen;

        // Tracking đổi A→B TRONG lúc lô đang bay (flow bắt vận đơn mới → UpsertMany giữa vòng).
        repo.UpsertMany(1, new[] { Don("SN1", tracking: "SPX-B") }, DateTime.UtcNow);

        // Lô cũ về, đóng cờ với thế hệ CŨ — thế hệ đã +1 nên KHÔNG được đóng cờ vận đơn vừa mở.
        repo.MarkGsheetSynced(1, "SN1", null, daHuy: false, coVanDon: true, coUocTinh: false,
            coDonTraHang: false, "T8", DateTime.UtcNow, genLucChup);

        Assert.Null(Assert.Single(repo.GetForGsheetPush(1)).GsheetDaCoVanDon); // vận đơn B vẫn chờ re-push
    }

    /// <summary>Hồi quy: đường CŨ "vừa xuất hiện" (NULL→có) vẫn phải mở lại cờ như trước.</summary>
    [Fact]
    public void TrackingMoiXuatHien_VanMoLaiCoNhuTruoc()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Don("SN1") }, DateTime.UtcNow);
        DongCoHub(repo);
        Assert.Empty(repo.GetForHubPush(1));

        repo.UpsertMany(1, new[] { Don("SN1", tracking: "SPX-A") }, DateTime.UtcNow);

        Assert.Equal("SPX-A", Assert.Single(repo.GetForHubPush(1)).TrackingNumber);
    }
}
