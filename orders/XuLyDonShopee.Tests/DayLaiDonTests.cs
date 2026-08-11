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
            coDonTraHang: true, tab: tab, at: DateTime.UtcNow, pushGen: 0);
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
    public void DayLai_XenGiuaLoDangBay_MarkGsheetSynced_KhongDongCoOan()
    {
        // ĐỐI XỨNG với ca hub ngay trên, cho đường Google Sheet: lượt đẩy sheet chạy mỗi 2 phút và mỗi lượt mất
        // vài giây; user bấm "Đẩy lại" rơi đúng cửa sổ đó thì lô đang bay KHÔNG được đóng lại cờ vừa mở — kẻo
        // màn hình báo "đã xếp vào hàng chờ" mà đơn thì không bao giờ được đẩy lại.
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(Acc, new[] { Don("SN1") }, DateTime.UtcNow);

        var loDangBay = Doc(repo);                 // lô bắt đầu bay: chụp thế hệ hiện tại
        Assert.Equal(0, loDangBay.GsheetPushGen);

        repo.DatLaiCoDayLai(Acc, "SN1");           // user bấm "Đẩy lại" GIỮA LÚC lô đang bay (+1 thế hệ)

        // Lô cũ về đích, đòi đóng cờ bằng thế hệ CŨ → phải bị từ chối.
        repo.MarkGsheetSynced(Acc, "SN1", null, daHuy: false, coVanDon: true, coUocTinh: false,
            coDonTraHang: false, tab: "Tháng 08-2026", at: DateTime.UtcNow, pushGen: loDangBay.GsheetPushGen);

        var sau = Doc(repo);
        Assert.False(sau.DaGhiSheet);                   // cờ VẪN mở → lượt sau đẩy lại thật
        Assert.Equal(1, repo.CountForGsheetPush(Acc));  // worker vẫn còn cớ chạy lượt sheet
        Assert.True(XuLyDonShopee.App.Services.HubOutbox.ConNghiaVuGhiSheet(sau, coFileBoSung: false));

        // ĐỐI CHỨNG: lô MỚI (đọc lại thế hệ hiện tại) thì đóng cờ bình thường — chốt này không khoá chết đường đẩy.
        var loMoi = Doc(repo);
        Assert.Equal(1, loMoi.GsheetPushGen);
        repo.MarkGsheetSynced(Acc, "SN1", null, daHuy: false, coVanDon: true, coUocTinh: false,
            coDonTraHang: false, tab: "Tháng 08-2026", at: DateTime.UtcNow, pushGen: loMoi.GsheetPushGen);
        Assert.True(Doc(repo).DaGhiSheet);
        Assert.Equal(0, repo.CountForGsheetPush(Acc));
    }

    /// <summary>
    /// CÙNG lớp lỗi, đường KHÁC: <see cref="OrdersRepository.SetReturnRequestCodes"/> (bước check đơn trả hàng)
    /// MỞ cờ <c>gsheet_da_co_don_tra_hang</c> — đó cũng là một cờ trong nhóm gsheet, nên phải +1 thế hệ y như nút
    /// "Đẩy lại". Thiếu vế đó thì lô sheet đang bay đóng lại đúng cờ vừa mở ⇒ <c>donTraHangMoi</c> false VĨNH
    /// VIỄN, mã trả vừa đổi không bao giờ đi được đường đơn thường nữa.
    /// </summary>
    [Fact]
    public void MaTraHangDoi_XenGiuaLoSheetDangBay_KhongDongCoOan()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(Acc, new[] { Don("SN1") }, DateTime.UtcNow);
        // Đơn đã ghi sheet xong ở lượt trước, lúc đó CHƯA có mã trả hàng.
        repo.MarkGsheetSynced(Acc, "SN1", null, daHuy: false, coVanDon: true, coUocTinh: false,
            coDonTraHang: false, tab: "Tháng 08-2026", at: DateTime.UtcNow, pushGen: 0);

        var loDangBay = Doc(repo);                                    // lô sheet kế bắt đầu bay: chụp thế hệ
        repo.SetReturnRequestCodes(Acc, new[] { ("SN1", "R-MOI") });  // check trả hàng ghi mã MỚI giữa chừng

        // Lô cũ về đích, đòi đóng cờ bằng thế hệ CŨ (và khai "đã gửi kèm mã trả") → phải bị từ chối.
        repo.MarkGsheetSynced(Acc, "SN1", null, daHuy: false, coVanDon: true, coUocTinh: false,
            coDonTraHang: true, tab: "Tháng 08-2026", at: DateTime.UtcNow, pushGen: loDangBay.GsheetPushGen);

        var sau = Doc(repo);
        Assert.Null(sau.GsheetDaCoDonTraHang);  // cờ VẪN mở ⇒ lượt sau thật sự mang mã mới lên sheet
        Assert.Equal("R-MOI", sau.ReturnRequestCode);
        Assert.True(XuLyDonShopee.App.Services.HubOutbox.ConNghiaVuGhiSheet(sau, coFileBoSung: false));

        // ĐỐI CHỨNG: lô MỚI (đọc lại thế hệ) đóng cờ bình thường — chốt này không khoá chết đường đẩy.
        var loMoi = Doc(repo);
        Assert.Equal(1, loMoi.GsheetPushGen);
        repo.MarkGsheetSynced(Acc, "SN1", null, daHuy: false, coVanDon: true, coUocTinh: false,
            coDonTraHang: true, tab: "Tháng 08-2026", at: DateTime.UtcNow, pushGen: loMoi.GsheetPushGen);
        Assert.Equal(1, Doc(repo).GsheetDaCoDonTraHang);
        Assert.False(XuLyDonShopee.App.Services.HubOutbox.ConNghiaVuGhiSheet(Doc(repo), coFileBoSung: false));
    }

    /// <summary>
    /// BƯỚC DỌN không được tin ẢNH CHỤP: <c>PushOrdersToGsheetAsync</c> đọc <c>pending</c> một lần ở đầu lượt rồi
    /// vài phút sau (đọc PDF + POST Apps Script) mới dọn bằng chính ảnh đó. Cú bấm "Đẩy lại" rơi vào giữa ⇒ đơn
    /// vừa được mở lại nghĩa vụ mà bị xoá theo ảnh cũ là bốc hơi vĩnh viễn (không còn đơn để đẩy lại).
    /// </summary>
    [Fact]
    public void Don_DonDaXongNhungCoMoLaiGiuaLuot_KhongBiXoa()
    {
        using var temp = new TempDatabase();
        var repo = RepoVoiDonDaXong(temp);

        var anhChup = Doc(repo);                  // bước đẩy sheet đọc pending ở ĐẦU lượt
        repo.DatLaiCoDayLai(Acc, "SN1");          // user bấm "Đẩy lại" trong lúc lô đang bay (+1 thế hệ hub)

        Assert.Equal(0, repo.DeleteOrders(Acc, new[] { (anhChup.OrderSn, anhChup.HubPushGen) }));
        Assert.Single(repo.Query(Acc));           // đơn CÒN trong app → lượt sau đẩy lại thật

        // ĐỐI CHỨNG: không ai đụng gì giữa chừng → dọn bình thường.
        var moi = Doc(repo);
        Assert.Equal(1, repo.DeleteOrders(Acc, new[] { (moi.OrderSn, moi.HubPushGen) }));
        Assert.Empty(repo.Query(Acc));
    }

    /// <summary>Cùng bước dọn, đường mở lại KHÁC: mã yêu cầu trả hàng vừa xuất hiện giữa lượt (cũng +1 thế hệ
    /// hub). Xoá đơn lúc này là hub vĩnh viễn thiếu mã.</summary>
    [Fact]
    public void Don_MaTraHangVuaGhiGiuaLuot_KhongBiXoa()
    {
        using var temp = new TempDatabase();
        var repo = RepoVoiDonDaXong(temp);

        var anhChup = Doc(repo);
        repo.SetReturnRequestCodes(Acc, new[] { ("SN1", "R-MOI") });

        Assert.Equal(0, repo.DeleteOrders(Acc, new[] { (anhChup.OrderSn, anhChup.HubPushGen) }));
        Assert.Single(repo.Query(Acc));
    }

    [Fact]
    public void ChotTheHe_ChanCoDaDay_NhungKHONG_Chan_TabVaLinkPhieu()
    {
        // Chốt thế hệ chỉ được chặn NHÓM CỜ. `gsheet_tab` và `gsheet_file_url` là SỰ THẬT vừa xảy ra ngoài đời
        // (dòng đã nằm ở tab đó, file đã có link đó) — chặn luôn hai cột này là tự dựng lại đúng lỗi
        // "dòng bị ghi LẦN HAI ở tab tháng mới" mà DatLaiCoDayLai cố ý tránh.
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(Acc, new[] { Don("SN1") }, DateTime.UtcNow);

        var loDangBay = Doc(repo);
        repo.DatLaiCoDayLai(Acc, "SN1");   // user bấm "Đẩy lại" giữa lượt ⇒ thế hệ lệch

        var daDongCo = repo.MarkGsheetSynced(Acc, "SN1", "https://drive/vua-upload", daHuy: false, coVanDon: true,
            coUocTinh: false, coDonTraHang: false, tab: "Tháng 08-2026", at: DateTime.UtcNow,
            pushGen: loDangBay.GsheetPushGen);

        Assert.Equal(0, daDongCo);        // cờ KHÔNG đóng — đúng ý chốt thế hệ
        var p = Doc(repo);
        Assert.False(p.DaGhiSheet);
        Assert.Null(p.GsheetDaHuy);       // 4 cờ trạng thái cũng không bị đóng theo

        // NHƯNG hai cột DỮ LIỆU phải được ghi:
        Assert.Equal("Tháng 08-2026", p.GsheetTab);              // lượt sau về ĐÚNG tab cũ, không đẻ dòng thứ hai
        Assert.Equal("https://drive/vua-upload", p.FileUrl);     // không upload lại phiếu đã có link
        Assert.True(p.DaTungGhiSheet);                            // bằng chứng "đã từng có dòng" không bị mất
    }

    [Fact]
    public void DayLai_KHONG_XoaBangChung_DaTungGhiSheet()
    {
        // "Đã TỪNG có dòng trên sheet" phải sống sót qua nút này: nó là thứ giữ đơn HỦY-mất-vận-đơn khỏi rơi vào
        // lối tắt "by design không ghi sheet" rồi bị dọn, để lại dòng trắng vĩnh viễn (xem HubOutboxGsheetHuyTests).
        using var temp = new TempDatabase();
        var repo = RepoVoiDonDaXong(temp, tab: "Tháng 07-2026");

        Assert.True(Doc(repo).DaTungGhiSheet);
        repo.DatLaiCoDayLai(Acc, "SN1");

        var p = Doc(repo);
        Assert.False(p.DaGhiSheet);       // cờ "đang coi là đã ghi" thì MỞ (đúng mục đích nút)
        Assert.True(p.DaTungGhiSheet);    // nhưng bằng chứng "đã từng có dòng" KHÔNG được quên
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
