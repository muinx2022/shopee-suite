using System.Text.Json;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Data;
using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test đợt <b>"mã trả hàng sống độc lập với đơn"</b>. Bối cảnh: Shopee cho trả hàng trong 15 ngày, mà app DỌN
/// đơn kết thúc ngay khi ghi sheet xong (<see cref="OrderPersistPipeline.NenXoaDonKetThuc"/>) — nên lúc yêu cầu trả
/// hàng xuất hiện thì đơn đã bị xoá khỏi <c>orders</c> và <c>SetReturnRequestCodes</c> VỨT mã đi. Đó là lý do số
/// mã lấy được vẫn là 0 dù đã sửa hai lỗi trước.
/// <list type="bullet">
/// <item><see cref="ReturnCodesRepository"/> — bảng <c>return_codes</c>: upsert, cờ đã-đẩy, dọn theo TUỔI.</item>
/// <item><b>Bẫy #1</b> — payload mã-trả KHÔNG được mang <c>daHuy</c> (khẳng định bằng SERIALIZE THẬT).</item>
/// <item><b>Bẫy #2</b> — payload phải mang <c>chiDienNeuCo</c> để script không đẻ dòng mới.</item>
/// <item><b>Ca chốt</b> — mã của đơn KHÔNG CÒN trong <c>orders</c> vẫn đẩy được lên Google Sheet.</item>
/// </list>
/// </summary>
public class MaTraHangDocLapTests
{
    private static readonly DateTime Luc = new(2026, 7, 29, 10, 0, 0, DateTimeKind.Utc);

    // ===================== Bẫy #1: daHuy phải VẮNG HẲN =====================

    /// <summary>
    /// <b>BẪY NGUY NHẤT.</b> Apps Script xử <c>daHuy === true</c> → tô nền đỏ; <c>=== false</c> → <b>XOÁ</b> nền
    /// đỏ; VẮNG → không đụng màu. Nếu payload mã-trả mang <c>daHuy:false</c> thì đẩy mã trả cho một đơn ĐÃ HỦY sẽ
    /// xoá sạch nền đỏ ở CẢ hai file, im lặng, rất khó phát hiện. Khẳng định bằng chuỗi JSON THẬT
    /// (<see cref="GoogleSheetSyncService.TaoJsonBody"/>) chứ không phải bằng niềm tin vào kiểu.
    /// </summary>
    [Fact]
    public void BayMotBayNhat_PayloadMaTra_KHONGChuaDaHuy()
    {
        var json = GoogleSheetSyncService.TaoJsonBody(
            "Tháng 07-2026", new[] { new GsheetReturnCodeRow("260725JTBTAJVD", "2607280TS2VYAW3") });

        Assert.DoesNotContain("daHuy", json, StringComparison.OrdinalIgnoreCase);
        // Soi cả bằng DOM JSON: field không tồn tại, không phải "tồn tại mà null".
        using var doc = JsonDocument.Parse(json);
        var don = doc.RootElement.GetProperty("orders")[0];
        Assert.False(don.TryGetProperty("daHuy", out _));
    }

    /// <summary>Không có ĐƯỜNG NÀO đặt được <c>daHuy</c> lên payload mã-trả: kiểu không có thành viên đó. Đây là
    /// chốt CẤU TRÚC — chặn cả những call-site chưa tồn tại, khác hẳn "nhớ đừng truyền".</summary>
    [Fact]
    public void BayMotBayNhat_KieuMaTra_KhongCoThanhVienDaHuy()
    {
        var ten = typeof(GsheetReturnCodeRow).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain("DaHuy", ten);
        Assert.Equal(new[] { "MaDon", "DonTraHang", "ChiDienNeuCo" }.OrderBy(x => x), ten.OrderBy(x => x));
    }

    /// <summary>Đối chứng: đường đẩy ĐƠN THƯỜNG vẫn phải LUÔN gửi <c>daHuy</c> (script cần giá trị tường minh để
    /// đổi màu 2 chiều). Hai hợp đồng khác nhau trên cùng một đường mạng — đừng gộp.</summary>
    [Fact]
    public void DoiChung_PayloadDonThuong_VanLuonCoDaHuy()
    {
        var json = GoogleSheetSyncService.TaoJsonBody(
            "Tháng 07-2026",
            new[] { new GsheetOrderRow("D1", null, null, null, null, null, null, null, null, null, false) });

        Assert.Contains("\"daHuy\":false", json);
    }

    // ===================== Bẫy #2: chiDienNeuCo =====================

    /// <summary>
    /// Không có cờ này, nhánh append của Apps Script đẻ một dòng gần như RỖNG (chỉ mã đơn + mã trả) cho mọi đơn
    /// chưa từng ghi sheet. Cờ phải là <c>true</c> và KHÔNG đặt được thành gì khác (property tính sẵn).
    /// <para>Phía Apps Script được kiểm bằng sheet giả — <c>scratchpad/sim-ma-tra-hang.js</c> CA 1.</para>
    /// </summary>
    [Fact]
    public void BayHai_PayloadMaTra_LuonCoChiDienNeuCoTrue()
    {
        var json = GoogleSheetSyncService.TaoJsonBody(
            "Tháng 07-2026", new[] { new GsheetReturnCodeRow("D1", "R1") });

        Assert.Contains("\"chiDienNeuCo\":true", json);
        using var doc = JsonDocument.Parse(json);
        var don = doc.RootElement.GetProperty("orders")[0];
        Assert.Equal("D1", don.GetProperty("maDon").GetString());
        Assert.Equal("R1", don.GetProperty("donTraHang").GetString());
        // ĐÚNG 3 field — thừa field nào cũng là rủi ro đụng vào ô/màu người dùng.
        Assert.Equal(3, don.EnumerateObject().Count());
    }

    [Fact]
    public void BayHai_CoChiDienNeuCo_KhongDatDuoc_ChiDoc()
        => Assert.Null(typeof(GsheetReturnCodeRow).GetProperty("ChiDienNeuCo")!.SetMethod);

    // ===================== Bảng return_codes =====================

    [Fact]
    public void LuuMaTraHang_DonKhongTonTaiTrongOrders_VanLuuDuoc()
    {
        using var temp = new TempDatabase();
        var repo = new ReturnCodesRepository(temp.Open());

        // KHÔNG hề có đơn nào trong bảng `orders` — đó chính là cảnh thật sau khi app dọn đơn.
        var kq = repo.LuuMaTraHang(1, new[] { ("260617ANE669U9", "2606210RB7XN9C4") }, "alina99.store", Luc);

        Assert.Equal(1, kq.DaGhi);
        Assert.Equal(("260617ANE669U9", "2606210RB7XN9C4"), Assert.Single(kq.CapMoi));
        Assert.Equal(("260617ANE669U9", "2606210RB7XN9C4"), Assert.Single(repo.LayMaTraHangChuaDay(1, Luc)));
    }

    [Fact]
    public void LuuMaTraHang_MaKhongDoi_KhongResetCoDaDay()
    {
        using var temp = new TempDatabase();
        var repo = new ReturnCodesRepository(temp.Open());
        repo.LuuMaTraHang(1, new[] { ("D1", "R1") }, "shop", Luc);
        repo.DanhDauDaDay(1, new[] { ("D1", "R1") }, Luc);
        Assert.Empty(repo.LayMaTraHangChuaDay(1, Luc));

        var kq = repo.LuuMaTraHang(1, new[] { ("D1", "R1") }, "shop", Luc.AddDays(1));

        Assert.Equal(0, kq.DaGhi);
        Assert.Empty(kq.CapMoi);
        Assert.Empty(repo.LayMaTraHangChuaDay(1, Luc)); // KHÔNG đẩy trùng
    }

    /// <summary>Bẫy #5: yêu cầu trả hàng được tạo LẠI với mã khác → phải đẩy lại.</summary>
    [Fact]
    public void LuuMaTraHang_MaDOI_ResetCoDeDayLai()
    {
        using var temp = new TempDatabase();
        var repo = new ReturnCodesRepository(temp.Open());
        repo.LuuMaTraHang(1, new[] { ("D1", "R1") }, "shop", Luc);
        repo.DanhDauDaDay(1, new[] { ("D1", "R1") }, Luc);

        var kq = repo.LuuMaTraHang(1, new[] { ("D1", "R2") }, "shop", Luc.AddDays(1));

        Assert.Equal(1, kq.DaGhi);
        Assert.Equal(("D1", "R2"), Assert.Single(kq.CapMoi));
        Assert.Equal(("D1", "R2"), Assert.Single(repo.LayMaTraHangChuaDay(1, Luc)));
    }

    /// <summary>
    /// Lô sheet mang mã R1 bay đi; GIỮA CHỪNG bước check shop ghi mã MỚI R2 cho cùng đơn (cờ đẩy về NULL). Lô cũ
    /// về đích KHÔNG được đóng cờ: đóng theo mã đơn TRẦN là đè lên nghĩa vụ vừa mở ⇒ R2 không bao giờ lên sheet,
    /// không log, không badge. Lô sheet bay lâu (nhiều nhóm tab × tới 120s) nên cửa sổ đua này là chuyện thường.
    /// </summary>
    [Fact]
    public void DanhDauDaDay_MaDaDoiGiuaLucLoDangBay_KhongDongCoOan()
    {
        using var temp = new TempDatabase();
        var repo = new ReturnCodesRepository(temp.Open());
        repo.LuuMaTraHang(1, new[] { ("D1", "R1") }, "shop", Luc);              // lô gửi đi mang R1
        repo.LuuMaTraHang(1, new[] { ("D1", "R2") }, "shop", Luc.AddDays(1));   // yêu cầu tạo lại → R2, cờ mở lại

        // Lô cũ về đích với đúng cặp nó đã gửi (D1, R1) — mã trong kho giờ là R2 ⇒ 0 dòng đổi.
        Assert.Equal(0, repo.DanhDauDaDay(1, new[] { ("D1", "R1") }, Luc.AddDays(1)));
        Assert.Equal(("D1", "R2"), Assert.Single(repo.LayMaTraHangChuaDay(1, Luc.AddDays(1))));

        // ĐỐI CHỨNG: lô mang đúng mã đang có thì đóng cờ bình thường — mệnh đề này không khoá chết đường đẩy.
        Assert.Equal(1, repo.DanhDauDaDay(1, new[] { ("D1", "R2") }, Luc.AddDays(1)));
        Assert.Empty(repo.LayMaTraHangChuaDay(1, Luc.AddDays(1)));
    }

    [Fact]
    public void LuuMaTraHang_MaRongHoacThieuKhoa_BoQua_KhongNem()
    {
        using var temp = new TempDatabase();
        var repo = new ReturnCodesRepository(temp.Open());

        var kq = repo.LuuMaTraHang(1, new[] { ("D1", "  "), ("", "R1"), ("D2", "R2") }, null, Luc);

        Assert.Equal(1, kq.DaGhi);
        Assert.Equal(("D2", "R2"), Assert.Single(kq.CapMoi));
        Assert.Equal("D2", Assert.Single(repo.LayMaTraHangChuaDay(1, Luc)).OrderSn);
    }

    [Fact]
    public void LayMaTraHangChuaDay_TachBachTheoTaiKhoan()
    {
        using var temp = new TempDatabase();
        var repo = new ReturnCodesRepository(temp.Open());
        repo.LuuMaTraHang(1, new[] { ("D1", "R1") }, "shop", Luc);
        repo.LuuMaTraHang(2, new[] { ("D2", "R2") }, "shop", Luc);

        Assert.Equal("D1", Assert.Single(repo.LayMaTraHangChuaDay(1, Luc)).OrderSn);
        Assert.Equal("D2", Assert.Single(repo.LayMaTraHangChuaDay(2, Luc)).OrderSn);
    }

    /// <summary>Bẫy #4: bảng dọn theo TUỔI, KHÔNG theo vòng đời đơn.</summary>
    [Fact]
    public void DonDep_XoaTheoTUOI_KhongDinhToiDon()
    {
        using var temp = new TempDatabase();
        var repo = new ReturnCodesRepository(temp.Open());
        repo.LuuMaTraHang(1, new[] { ("CU", "R-CU") }, "shop", Luc.AddDays(-120));
        repo.LuuMaTraHang(1, new[] { ("MOI", "R-MOI") }, "shop", Luc.AddDays(-10));

        var xoa = repo.DonDep(Luc.AddDays(-ReturnCodesRepository.SoNgayGiuMac));

        Assert.Equal(1, xoa);
        // Truyền mốc "bây giờ" = Luc: từ 09/08/2026 LayMaTraHangChuaDay còn chặn theo HẠN THỬ LẠI, mà fixture
        // dùng ngày CỨNG (Luc) nên để nó lấy DateTime.UtcNow thì bản ghi "MOI" sẽ quá hạn theo thời gian thực.
        Assert.Equal("MOI", Assert.Single(repo.LayMaTraHangChuaDay(1, Luc)).OrderSn);
    }

    /// <summary>90 ngày = hơn 4 lần cửa sổ quét 20 ngày → không bao giờ dọn nhầm mã còn đang chờ đẩy.</summary>
    [Fact]
    public void SoNgayGiuMac_RongHonHanCuaSoQuet()
    {
        Assert.Equal(90, ReturnCodesRepository.SoNgayGiuMac);
        Assert.True(ReturnCodesRepository.SoNgayGiuMac > TraHangParser.SoNgayCuaSoTraHang * 4);
    }

    /// <summary>Bảng mới phải tự có trên DB CŨ (CREATE TABLE IF NOT EXISTS chạy trong <c>Initialize</c>).</summary>
    [Fact]
    public void BangReturnCodes_TuTaoTrenDbCu_KhongCanScript()
    {
        using var temp = new TempDatabase();

        var repo = new ReturnCodesRepository(new Database(temp.Path));

        var ex = Record.Exception(() => repo.LuuMaTraHang(1, new[] { ("D1", "R1") }, null, Luc));
        Assert.Null(ex);
    }

    // ===================== CA CHỐT: đơn đã bị dọn vẫn đẩy được mã lên GSheet =====================

    /// <summary>
    /// <b>Toàn bộ mục đích của đợt sửa.</b> Đơn ĐÃ BỊ XOÁ khỏi bảng <c>orders</c> (app dọn sau khi ghi sheet) mà
    /// mã trả hàng của nó vẫn phải tới được Google Sheet — vì DÒNG trên sheet còn nguyên và Apps Script tra theo
    /// mã đơn. Chạy qua Web App GIẢ trên loopback nên kiểm được đúng payload đi trên dây.
    /// </summary>
    [Fact]
    public async Task MaTraHang_CuaDonDaBiDonKhoiOrders_VanDayDuocLenGSheet()
    {
        using var temp = new TempDatabase();
        using var web = new HubOutboxGsheetHuyTests.FakeGsheetWebApp();
        var services = new AppServices(temp.Path);
        var accId = services.Accounts.Insert(new Account { Email = "shop-test@example.com" });
        services.Settings.SetGsheetWebAppUrl(web.Url);

        // Cảnh THẬT: đơn 260715QNAP2587 từng được ghi sheet rồi bị dọn khỏi app; yêu cầu trả hàng xuất hiện SAU đó.
        Assert.Empty(services.Orders.GetOrderSns(accId));
        services.ReturnCodes.LuuMaTraHang(
            accId, new[] { ("260715QNAP2587", "2607210QK4M8T21") }, "alina99.store", DateTime.UtcNow);

        var kq = await HubOutbox.PushReturnCodesToGsheetAsync(accId, services, _ => { }, CancellationToken.None);

        Assert.Equal(KetQuaDay.ThanhCong, kq);
        var body = Assert.Single(web.Bodies);
        Assert.Contains("\"maDon\":\"260715QNAP2587\"", body);
        Assert.Contains("\"donTraHang\":\"2607210QK4M8T21\"", body);
        Assert.Contains("\"chiDienNeuCo\":true", body);
        Assert.DoesNotContain("daHuy", body);                       // bẫy #1, trên dây thật
        // Đã đẩy → đánh dấu, lượt sau KHÔNG gửi lại.
        Assert.Empty(services.ReturnCodes.LayMaTraHangChuaDay(accId));
        Assert.Equal(KetQuaDay.KhongCanDay,
            await HubOutbox.PushReturnCodesToGsheetAsync(accId, services, _ => { }, CancellationToken.None));
        Assert.Single(web.Bodies);
    }

    /// <summary>
    /// Đường đẩy ĐƠN THƯỜNG (<see cref="HubOutbox.PushOrdersToGsheetAsync"/>) duyệt bảng <c>orders</c> nên với
    /// tài khoản đã dọn sạch đơn nó KHÔNG gửi gì — chứng minh vì sao phải có đường riêng, và vì sao lượt mã trả
    /// hàng ở worker phải chạy TRƯỚC cửa "hết hàng tồn".
    /// </summary>
    [Fact]
    public async Task DuongDayDonThuong_KhongConDonNao_ThiKhongGuiGi()
    {
        using var temp = new TempDatabase();
        using var web = new HubOutboxGsheetHuyTests.FakeGsheetWebApp();
        var services = new AppServices(temp.Path);
        var accId = services.Accounts.Insert(new Account { Email = "shop-test@example.com" });
        services.Settings.SetGsheetWebAppUrl(web.Url);
        services.ReturnCodes.LuuMaTraHang(accId, new[] { ("DA-DON", "R1") }, "shop", DateTime.UtcNow);

        var kq = await HubOutbox.PushOrdersToGsheetAsync(
            accId, services, shopId: null, shopLogin: "alina99.store",
            nenBaoThieuGsheetUrl: () => false, imLangKhiKhongCoDonMoi: true, log: _ => { },
            ct: CancellationToken.None);

        Assert.Equal(KetQuaDay.KhongCanDay, kq);
        Assert.Empty(web.Bodies);
        Assert.Single(services.ReturnCodes.LayMaTraHangChuaDay(accId)); // vẫn nằm chờ đường riêng
    }

    /// <summary>Đơn CÒN trong app → mã về đúng TAB đã nhớ của đơn (không nhân đôi dòng ở tab tháng mới).</summary>
    [Fact]
    public async Task MaTraHang_DonConTrongApp_VeDungTabDaNho()
    {
        using var temp = new TempDatabase();
        using var web = new HubOutboxGsheetHuyTests.FakeGsheetWebApp();
        var services = new AppServices(temp.Path);
        var accId = services.Accounts.Insert(new Account { Email = "shop-test@example.com" });
        services.Settings.SetGsheetWebAppUrl(web.Url);

        services.Orders.UpsertMany(accId, new[]
        {
            new SyncedOrder { OrderSn = "CON-SONG", ItemsJson = "[]", Status = "Chờ lấy hàng", TotalPrice = 1000 },
        }, DateTime.UtcNow);
        services.Orders.MarkGsheetSynced(accId, "CON-SONG", null, daHuy: false, coVanDon: false, coUocTinh: false,
            coDonTraHang: false, tab: "Tháng 05-2026", at: DateTime.UtcNow, pushGen: 0);
        services.ReturnCodes.LuuMaTraHang(accId, new[] { ("CON-SONG", "R9") }, "shop", DateTime.UtcNow);

        await HubOutbox.PushReturnCodesToGsheetAsync(accId, services, _ => { }, CancellationToken.None);

        Assert.Contains("\"tab\":\"Tháng 05-2026\"", Assert.Single(web.Bodies));
    }

    /// <summary>Web App LỖI → KHÔNG đánh dấu đã đẩy ⇒ lượt sau thử lại (không mất mã).</summary>
    [Fact]
    public async Task MaTraHang_WebAppLoi_KhongDanhDau_LuotSauThuLai()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var accId = services.Accounts.Insert(new Account { Email = "shop-test@example.com" });
        // Cổng chết → PushAsync ném → hàm nuốt lỗi, log, KHÔNG đánh dấu.
        services.Settings.SetGsheetWebAppUrl("http://127.0.0.1:9/");
        services.ReturnCodes.LuuMaTraHang(accId, new[] { ("D1", "R1") }, "shop", DateTime.UtcNow);

        var kq = await HubOutbox.PushReturnCodesToGsheetAsync(accId, services, _ => { }, CancellationToken.None);

        Assert.Equal(KetQuaDay.ThatBai, kq);
        Assert.Single(services.ReturnCodes.LayMaTraHangChuaDay(accId));
    }

    /// <summary>Chưa cấu hình Web App URL → không đẩy, nhưng GIỮ hàng đợi (điền URL là đẩy được ngay).</summary>
    [Fact]
    public async Task MaTraHang_ChuaCoUrl_GiuHangDoi()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var accId = services.Accounts.Insert(new Account { Email = "shop-test@example.com" });
        services.ReturnCodes.LuuMaTraHang(accId, new[] { ("D1", "R1") }, "shop", DateTime.UtcNow);

        var kq = await HubOutbox.PushReturnCodesToGsheetAsync(accId, services, _ => { }, CancellationToken.None);

        Assert.Equal(KetQuaDay.KhongCanDay, kq);
        Assert.Single(services.ReturnCodes.LayMaTraHangChuaDay(accId));
    }
}
