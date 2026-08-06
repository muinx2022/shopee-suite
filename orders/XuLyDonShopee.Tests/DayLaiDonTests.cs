using System.Linq;
using XuLyDonShopee.Core.Data;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Nút "Đẩy lại" của màn chẩn đoán đơn kẹt (H2.5) → <see cref="OrdersRepository.DatLaiCoDayLai"/>. Đây là thao
/// tác GHI DB theo lệnh người dùng nên test canh CẢ HAI chiều:
/// <list type="bullet">
/// <item>mở ĐÚNG các cờ cần mở → lượt outbox sau thật sự đẩy lại (Hub + Google Sheet);</item>
/// <item>KHÔNG đụng <c>gsheet_tab</c> (đẩy lại phải về đúng tab cũ, kẻo nhân đôi dòng khi sang tháng),
/// <c>sold_counted_at</c> (mở lại là +1 "Đã bán" lần hai trên kho hub), <c>gsheet_file_url</c> và
/// <c>hub_slip_synced_at</c>.</item>
/// </list>
/// </summary>
public class DayLaiDonTests
{
    private const long Acc = 77;

    private static SyncedOrder Don(string sn, string status = "Đã giao") => new()
    {
        OrderSn = sn,
        Status = status,
        Sku = "B02435",
        TrackingNumber = "SPX1",
        ItemsJson = "[]",
    };

    /// <summary>Dựng một đơn ĐÃ hoàn tất mọi nghĩa vụ (mọi cờ đã đóng) để thấy rõ cái gì bị mở lại.</summary>
    private static OrdersRepository RepoVoiDonDaXong(TempDatabase temp, string tab = "Tháng 08-2026")
    {
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(Acc, new[] { Don("SN1") }, DateTime.UtcNow);
        repo.MarkHubSynced(Acc, new[] { "SN1" }, DateTime.UtcNow);
        repo.MarkHubSlipSynced(Acc, new[] { "SN1" }, DateTime.UtcNow);
        repo.MarkSoldCounted(Acc, new[] { "SN1" }, DateTime.UtcNow);
        repo.MarkGsheetSynced(Acc, "SN1", "https://drive/file1", daHuy: false, coVanDon: true, coUocTinh: true,
            coDonTraHang: true, tab: tab, at: DateTime.UtcNow);
        return repo;
    }

    private static GsheetPendingOrder Doc(OrdersRepository repo)
        => repo.GetForGsheetPush(Acc).Single(p => p.OrderSn == "SN1");

    [Fact]
    public void DayLai_MoLaiHangCho_HubVaSheet()
    {
        using var temp = new TempDatabase();
        var repo = RepoVoiDonDaXong(temp);

        // Trước: không còn trong hàng chờ nào.
        Assert.Empty(repo.GetForHubPush(Acc));
        Assert.Equal(0, repo.CountForHubPush(Acc));
        Assert.Equal(0, repo.CountForGsheetPush(Acc));

        Assert.Equal(1, repo.DatLaiCoDayLai(Acc, "SN1"));

        // Sau: hàng chờ HUB có lại đơn (GetForHubPush chỉ lấy hub_synced_at IS NULL).
        Assert.Equal(new[] { "SN1" }, repo.GetForHubPush(Acc).Select(o => o.OrderSn));
        Assert.Equal(1, repo.CountForHubPush(Acc));
        // Hàng chờ SHEET cũng có lại — BẮT BUỘC: HubOutboxWorker chỉ chạy lượt đẩy sheet khi số đếm này > 0.
        Assert.Equal(1, repo.CountForGsheetPush(Acc));

        // Và lượt đẩy sheet thật sự CHỌN GỬI đơn này (đúng hàm mà vòng đẩy dùng).
        var p = Doc(repo);
        Assert.False(p.DaGhiSheet);
        Assert.Null(p.GsheetDaHuy);
        Assert.Null(p.GsheetDaCoVanDon);
        Assert.Null(p.GsheetDaCoUocTinh);
        Assert.Null(p.GsheetDaCoDonTraHang);
        Assert.True(XuLyDonShopee.App.Services.HubOutbox.ConNghiaVuGhiSheet(p, coFileBoSung: false));
    }

    [Fact]
    public void DayLai_KHONG_DungTab_DemDaBan_LinkPhieu_VaCoPhieuHub()
    {
        using var temp = new TempDatabase();
        var repo = RepoVoiDonDaXong(temp, tab: "Tháng 07-2026");

        repo.DatLaiCoDayLai(Acc, "SN1");

        var p = Doc(repo);
        Assert.Equal("Tháng 07-2026", p.GsheetTab);   // đẩy lại phải về ĐÚNG tab cũ (chống nhân đôi dòng)
        Assert.True(p.DaDemDaBan);                     // KHÔNG mở lại → không +1 "Đã bán" lần hai
        Assert.Equal("https://drive/file1", p.FileUrl); // KHÔNG upload lại phiếu đã có link
        Assert.True(p.DaDayPhieuHub);                  // KHÔNG mở lại cờ phiếu hub
        Assert.Equal(0, repo.CountForHubSlipPush(Acc)); // ⇒ không đẻ hàng tồn phiếu
    }

    [Fact]
    public void DayLai_GiuNguyen_CreatedAt()
    {
        using var temp = new TempDatabase();
        var repo = RepoVoiDonDaXong(temp);
        var truoc = repo.GetForHubPush(Acc); // rỗng, chỉ để chắc chắn không có gì
        Assert.Empty(truoc);

        var createdTruoc = DocCreatedAt(temp);
        repo.DatLaiCoDayLai(Acc, "SN1");
        Assert.Equal(createdTruoc, DocCreatedAt(temp));
    }

    private static string DocCreatedAt(TempDatabase temp)
    {
        using var conn = temp.Open().OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT created_at FROM orders WHERE order_sn = 'SN1';";
        return (string)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void DayLai_XenGiuaLoDangBay_MarkHubSynced_KhongDongCoOan()
    {
        // Cùng lớp bảo vệ hub_push_gen của MarkPrepared/SetReturnRequestCodes: user bấm "Đẩy lại" trong lúc một
        // lô đang bay lên hub → lô đó trả OK cho bản CŨ, KHÔNG được đóng cờ vừa mở.
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(Acc, new[] { Don("SN1") }, DateTime.UtcNow);

        var lo = repo.GetForHubPush(Acc);                       // snapshot thế hệ (lô bắt đầu bay)
        Assert.Single(lo);
        repo.MarkHubSynced(Acc, new[] { "SN1" }, DateTime.UtcNow);
        Assert.Empty(repo.GetForHubPush(Acc));                  // lô đầu đóng cờ bình thường

        var lo2 = repo.GetForHubPush(Acc);                      // rỗng (không có gì bay)
        Assert.Empty(lo2);

        repo.DatLaiCoDayLai(Acc, "SN1");                        // user bấm Đẩy lại
        var lo3 = repo.GetForHubPush(Acc);                      // lô mới chụp thế hệ MỚI
        Assert.Single(lo3);
        repo.DatLaiCoDayLai(Acc, "SN1");                        // user bấm lần nữa TRONG LÚC lô3 đang bay
        repo.MarkHubSynced(Acc, new[] { "SN1" }, DateTime.UtcNow);

        // Thế hệ đã lệch → KHÔNG đóng cờ oan; đơn còn trong hàng chờ để lượt sau đẩy lại.
        Assert.Equal(new[] { "SN1" }, repo.GetForHubPush(Acc).Select(o => o.OrderSn));
    }

    [Fact]
    public void DayLai_MaDonKhongCo_Tra0_KhongNem()
    {
        using var temp = new TempDatabase();
        var repo = RepoVoiDonDaXong(temp);

        Assert.Equal(0, repo.DatLaiCoDayLai(Acc, "SN-KHONG-CO"));
        Assert.Equal(0, repo.DatLaiCoDayLai(Acc, "   "));
        Assert.Equal(0, repo.DatLaiCoDayLai(Acc + 1, "SN1")); // đúng mã nhưng KHÁC tài khoản → không đụng
        Assert.Empty(repo.GetForHubPush(Acc));                 // đơn thật vẫn nguyên trạng (chưa mở cờ)
    }
}
