using XuLyDonShopee.App.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Notify "có đơn trả hàng" phải bám <b>kho mã</b> (<c>return_codes</c>), không bám bảng <c>orders</c>.
/// <para>
/// Sự cố: tin chỉ bắn theo <c>OrdersRepository.SetReturnRequestCodes</c> — mà đa số mã trả hàng thuộc đơn đã bị
/// <c>NenXoaDonKetThuc</c> dọn khỏi máy, nên đường đó rỗng và tin gần như không bao giờ được gửi (đúng lý do
/// bảng <c>return_codes</c> ra đời).
/// </para>
/// Kèm badge "⏳ Chờ đẩy": mã trả hàng chưa đẩy cũng là hàng tồn, trước đây không được đếm.
/// </summary>
public class NotifyDonTraKhoMaTests
{
    /// <summary>
    /// Mốc ghi mã trả hàng — phải là mốc <b>TƯƠNG ĐỐI</b> ("vừa ghi hôm qua"), KHÔNG được đóng cứng ngày.
    /// <para>
    /// <c>HubOutbox.PushReturnCodesToGsheetAsync</c> gọi <c>ReturnCodes.DonDep(UtcNow - SoNgayGiuMac)</c> ngay dòng
    /// ĐẦU (trước cả cửa kiểm URL) ⇒ ngày đóng cứng sẽ trôi ra ngoài cửa sổ 90 ngày và bản ghi bị xoá NGAY trước
    /// khi đếm. Bản cũ ghi <c>2026-07-30</c>: đã đo, từ <b>2026-10-28</b> là
    /// <c>BadgeChoDay_DemCaMaTraHangConTon</c> đỏ vĩnh viễn với <c>Assert.False() Failure — Actual: null</c>
    /// (trùng y hệt thông điệp của lỗi đua PushGate vừa vá) và <c>ChuaCoUrlSheet_…</c> đỏ ở <c>Assert.Single</c>.
    /// </para>
    /// Dùng thẳng <c>UtcNow</c> (không trừ lùi ngày nào) để KHÔNG ghép ngầm với giả định "cửa sổ giữ ≥ 1 ngày";
    /// <c>created_at</c> là thứ duy nhất mốc này đi vào — không truy vấn nào của kho mã lọc theo thời gian.
    /// </summary>
    private static readonly DateTime Luc = DateTime.UtcNow;

    /// <summary>Id tài khoản RIÊNG của lớp này — xem <see cref="TempDatabase.ThemTaiKhoanIdRieng{TLopTest}"/> (lấy id 1
    /// mặc định thì lớp này và <c>HubOutboxWorkerRoundTests</c> giành <c>PushGate(1, Gsheet)</c> của nhau).
    /// <b>Chép lớp test này đi nơi khác thì PHẢI đổi số này</b> — trùng id là dựng lại đúng cuộc đua đó.</summary>
    private const long AccId = 4101;

    // ===== Nguồn của tin: kho mã, KHÔNG phải bảng orders =====

    /// <summary>Ca thật: đơn KHÔNG còn trong <c>orders</c>. Đường cũ (SetReturnRequestCodes) không có cặp nào để
    /// báo; kho mã trả đúng cặp mới → tin vẫn bắn được.</summary>
    [Fact]
    public void DonDaBiDon_KhoMaVanChoCapMoi_DuongCuThiRong()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var accId = TempDatabase.ThemTaiKhoanIdRieng<NotifyDonTraKhoMaTests>(services.Database, AccId);
        var cap = new[] { ("260715QNAP2587", "2607210QK4M8T21") };

        var kqMa = services.ReturnCodes.LuuMaTraHang(accId, cap, "alina99.store", Luc);
        var kqDon = services.Orders.SetReturnRequestCodes(accId, cap);

        Assert.Equal(1, kqMa.DaGhi);
        Assert.Equal(("260715QNAP2587", "2607210QK4M8T21"), Assert.Single(kqMa.CapMoi));
        Assert.Empty(kqDon.CapDaGhi); // đường cũ: không đơn nào trong DB ⇒ notify im lặng
    }

    /// <summary>Chống báo lại: mã KHÔNG đổi thì lượt sau không có cặp mới nào (không gửi tin nhắc lại mỗi vòng).</summary>
    [Fact]
    public void MaKhongDoi_KhongConCapMoi_KhongBaoLai()
    {
        using var temp = new TempDatabase();
        var repo = new XuLyDonShopee.Core.Data.ReturnCodesRepository(temp.Open());
        repo.LuuMaTraHang(1, new[] { ("D1", "R1") }, "shop", Luc);

        var lai = repo.LuuMaTraHang(1, new[] { ("D1", "R1"), ("D2", "R2") }, "shop", Luc.AddMinutes(30));

        Assert.Equal(1, lai.DaGhi);
        Assert.Equal(("D2", "R2"), Assert.Single(lai.CapMoi)); // chỉ cặp MỚI, không kèm D1 đã báo vòng trước
    }

    // ===== Hợp đồng gửi Hub =====

    [Fact]
    public void KindGuiHub_DungChuoiHopDong() => Assert.Equal("don_tra", OrderPersistPipeline.KindDonTra);

    /// <summary>Chống HAI TIN một mã: Hub đã tự bắn tin cho đơn CÒN trong <c>orders</c> (qua
    /// <c>ReturnCodeChangedItems</c> của <c>orders/push</c>) ⇒ app-alert chỉ mang phần đơn ĐÃ BỊ DỌN.</summary>
    [Fact]
    public void LocCapDonDaDon_BoDonConSong_GiuDonDaDon()
    {
        var capMoi = new[] { ("DA-DON", "R1"), ("CON-SONG", "R2") };
        var conSong = new[] { ("CON-SONG", "R2") }; // SetReturnRequestCodes ghi được ⇒ đơn còn trong app

        Assert.Equal(("DA-DON", "R1"), Assert.Single(OrderPersistPipeline.LocCapDonDaDon(capMoi, conSong)));
    }

    [Fact]
    public void LocCapDonDaDon_KhongDonNaoConSong_GiuNguyenCaLo()
    {
        var capMoi = new[] { ("D1", "R1"), ("D2", "R2") };

        Assert.Equal(capMoi, OrderPersistPipeline.LocCapDonDaDon(capMoi, Array.Empty<(string, string)>()));
        Assert.Equal(capMoi, OrderPersistPipeline.LocCapDonDaDon(capMoi, null));
    }

    /// <summary>Mọi mã mới đều thuộc đơn còn sống → KHÔNG còn gì để app-alert (nhánh Hub im lặng, Hub lo).</summary>
    [Fact]
    public void LocCapDonDaDon_TatCaConSong_ThiRong()
    {
        var capMoi = new[] { ("D1", "R1") };

        Assert.Empty(OrderPersistPipeline.LocCapDonDaDon(capMoi, new[] { ("D1", "R1") }));
        Assert.Empty(OrderPersistPipeline.LocCapDonDaDon(null, capMoi));
    }

    [Fact]
    public void MoTaCapDonTra_GhepCapTheoDinhDangSN_Bang_CODE()
    {
        var s = OrderPersistPipeline.MoTaCapDonTra(new[] { ("SN1", "CODE1"), ("SN2", "CODE2") });

        Assert.Equal("SN1=CODE1; SN2=CODE2", s);
    }

    [Fact]
    public void MoTaCapDonTra_BoCapThieuVe_VaChiuDuocDanhSachRong()
    {
        Assert.Equal("SN2=CODE2", OrderPersistPipeline.MoTaCapDonTra(new[] { ("SN1", "  "), ("", "CODE1"), ("SN2", "CODE2") }));
        Assert.Equal(string.Empty, OrderPersistPipeline.MoTaCapDonTra(Array.Empty<(string, string)>()));
        Assert.Equal(string.Empty, OrderPersistPipeline.MoTaCapDonTra(null));
    }

    // ===== Badge "⏳ Chờ đẩy" =====

    /// <summary>Mã trả hàng chưa đẩy được (Web App lỗi) phải hiện trên badge — trước đây badge báo 0 dù mã đang kẹt.</summary>
    [Fact]
    public async Task BadgeChoDay_DemCaMaTraHangConTon()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var accId = TempDatabase.ThemTaiKhoanIdRieng<NotifyDonTraKhoMaTests>(services.Database, AccId);
        services.Settings.SetGsheetWebAppUrl("http://127.0.0.1:9/"); // cổng chết → đẩy hỏng, mã còn nguyên
        services.ReturnCodes.LuuMaTraHang(accId, new[] { ("D1", "R1") }, "shop", Luc);

        var worker = new HubOutboxWorker(services);
        Assert.False(await worker.MotLuotAsync(CancellationToken.None));

        Assert.Equal(1, services.PendingOutbox.ReturnCodes);
        Assert.Equal(1, services.PendingOutbox.Tong); // badge BẬT
    }

    /// <summary>Chưa cấu hình Web App URL = KHÔNG có đích → đếm 0, badge không kẹt số vĩnh viễn (cùng luật với
    /// 4 loại tồn kia).</summary>
    [Fact]
    public async Task ChuaCoUrlSheet_ThiKhongDemMaTra_BadgeTat()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var accId = TempDatabase.ThemTaiKhoanIdRieng<NotifyDonTraKhoMaTests>(services.Database, AccId);
        services.ReturnCodes.LuuMaTraHang(accId, new[] { ("D1", "R1") }, "shop", Luc);

        var worker = new HubOutboxWorker(services);
        await worker.MotLuotAsync(CancellationToken.None);

        Assert.Equal(0, services.PendingOutbox.ReturnCodes);
        Assert.Equal(0, services.PendingOutbox.Tong);
        Assert.Single(services.ReturnCodes.LayMaTraHangChuaDay(accId)); // vẫn nằm chờ, chỉ là chưa có đích
    }
}
