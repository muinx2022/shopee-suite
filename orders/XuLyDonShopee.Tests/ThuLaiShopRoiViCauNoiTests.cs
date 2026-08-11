using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// <b>Shop rơi OAN vì cầu nối đứt ⇒ được chạy lại ĐÚNG MỘT lượt cuối vòng — nhưng CHỈ khi chưa hề gửi
/// <c>prepareNextOrder</c>.</b>
/// <para>
/// Bối cảnh (vòng 19:01→19:54 ngày 10/08/2026, lần đầu đi trọn vòng): 9/12 shop xong, 3 shop rơi vì
/// <c>minoa.store</c> hết hạn chờ extension, <c>cicily.store</c> + <c>bizly.store</c> "cầu nối rớt giữa chặng".
/// Cả ba là RƠI OAN — trang Seller Centre không hỏng, ta chỉ mất câu trả lời vì network service của trình duyệt
/// bị dựng lại đều đặn ~240s/lần.
/// </para>
/// <para>
/// <b>RÀNG BUỘC SỐNG CÒN mà bộ test này canh:</b> <c>prepareNextOrder</c> CHUẨN BỊ HÀNG + IN PHIẾU trên đơn THẬT.
/// Shop đã gửi lệnh đó thì tuyệt đối không được chạy lại — đếm trùng, in phiếu trùng, đụng đơn của người dùng.
/// Điều này quan trọng hơn cả mục tiêu đi trọn 12/12 shop.
/// </para>
/// </summary>
public class ThuLaiShopRoiViCauNoiTests
{
    private const string Tinh = "Thanh Hóa";

    /// <summary>Hạn chờ nối lại rút gọn cho test (production 90s) — cũng là hạn RÚT NGẮN của chặng khi socket đứt.</summary>
    private static readonly TimeSpan ChoNoiLaiTest = TimeSpan.FromMilliseconds(400);

    // ===== 1. Luật THUẦN: shop vừa hỏng có được xếp vào hàng đợi thử lại không =====

    [Theory]
    // Ca chính của bản vá: đứt cầu nối + CHƯA gửi chuẩn bị hàng ⇒ xếp hàng đợi.
    [InlineData(false, true, false, false, 0, true)]
    // Sát trần vẫn nhận — trần là mốc DỪNG, không phải mốc cảnh báo.
    [InlineData(false, true, false, false, OrdersBridgeSession.TranThuLaiShopMoiVong - 1, true)]
    // ĐÃ gửi prepareNextOrder ⇒ KHÔNG, dù mọi điều kiện khác đều thuận. Đây là ràng buộc sống còn.
    [InlineData(true, true, false, false, 0, false)]
    // Lỗi KHÁC cầu nối (trang hỏng thật) ⇒ giữ nguyên hành vi cũ: bỏ shop, không thử lại.
    [InlineData(false, false, false, false, 0, false)]
    // Thân shop đã chạy trọn, chỉ bước đóng tab mới ném ⇒ không có gì để làm lại.
    [InlineData(false, true, true, false, 0, false)]
    // Đã thử lại rồi ⇒ mỗi shop đúng 1 lượt.
    [InlineData(false, true, false, true, 0, false)]
    // Chạm trần tổng số lượt thử lại của vòng.
    [InlineData(false, true, false, false, OrdersBridgeSession.TranThuLaiShopMoiVong, false)]
    public void NenThuLaiShopRoiOan_MaTran(
        bool daGuiChuanBiHang, bool loiCauNoi, bool daXongViecShop, bool daThuLaiShopNay,
        int soShopDaXepThuLai, bool mong)
        => Assert.Equal(mong, OrdersBridgeSession.NenThuLaiShopRoiOan(
            daGuiChuanBiHang, loiCauNoi, daXongViecShop, daThuLaiShopNay, soShopDaXepThuLai));

    /// <summary>Vượt trần thì vẫn phải từ chối (trần bị so bằng <c>&lt;</c>, không phải <c>!=</c>).</summary>
    [Fact]
    public void VuotTran_ThiVanKhongThuLai()
        => Assert.False(OrdersBridgeSession.NenThuLaiShopRoiOan(
            daGuiChuanBiHang: false, loiCauNoi: true, daXongViecShop: false, daThuLaiShopNay: false,
            soShopDaXepThuLai: OrdersBridgeSession.TranThuLaiShopMoiVong + 5));

    // ===== 2. Phân loại lỗi: cái nào là "rơi oan vì cầu nối" =====

    [Fact]
    public void CauNoiRotGiuaChang_LaLoiCauNoi()
        => Assert.True(OrdersBridgeSession.LaLoiCauNoi(new CauNoiRotGiuaChangException("rớt giữa chặng")));

    [Fact]
    public void HetHanChoExtension_CungLaLoiCauNoi()
        => Assert.True(OrdersBridgeSession.LaLoiCauNoi(new TimeoutException("Hết thời gian chờ phản hồi từ extension.")));

    [Fact]
    public void LoiKhac_KhongPhaiLoiCauNoi()
        => Assert.False(OrdersBridgeSession.LaLoiCauNoi(new InvalidOperationException("trang trả hàng không render")));

    /// <summary>Extension khai "MẤT NGỮ CẢNH TAB SHOP" (service worker MV3 vừa bị dựng lại, khôi phục hỏng) — đi
    /// qua đường <c>error</c> nên thành <c>InvalidOperationException</c>, nhưng bản chất là hạ tầng cầu nối chết
    /// đi sống lại: trang không hỏng, shop phải được xếp thử lại như mọi cú rớt cầu nối khác. Không nhận diện thì
    /// cùng một cú SW chết mà biểu hiện bằng rớt socket được cứu, biểu hiện bằng <c>error</c> lại mất trọn vòng
    /// (phản biện đợt trả nợ 11/08/2026).</summary>
    [Fact]
    public void MatNguCanhTabShop_CungLaLoiCauNoi()
        => Assert.True(OrdersBridgeSession.LaLoiCauNoi(new InvalidOperationException(
            "Extension báo lỗi: " + OrdersBridgeChannel.MocLoiMatTabShop
            + " (service worker vừa khởi động lại) — bỏ lượt")));

    /// <summary>Mốc chuỗi C# phải nằm TRONG đúng hằng <c>LOI_MAT_TAB_SHOP</c> của extension — đọc thẳng
    /// <c>core.js</c> chứ không tin bản chép tay (cùng khuôn <c>HangTran_KhopBanExtension</c>): bên JS đổi câu là
    /// phân loại thử-lại gãy ÂM THẦM, shop dính lỗi này mất trọn vòng thay vì được cứu.</summary>
    [Fact]
    public void MocLoiMatTabShop_KhopBanExtension()
    {
        var js = File.ReadAllText(Path.Combine(GocRepo(), "extensions", "shopee-orders", "core.js"));
        var m = Regex.Match(js, "LOI_MAT_TAB_SHOP\\s*=\\s*\\r?\\n?\\s*\"([^\"]+)\"");
        Assert.True(m.Success, "Không thấy hằng LOI_MAT_TAB_SHOP trong core.js — đổi tên hằng thì sửa cả test này.");
        Assert.Contains(OrdersBridgeChannel.MocLoiMatTabShop, m.Groups[1].Value, StringComparison.Ordinal);
    }

    /// <summary>Gốc repo — dò ngược từ thư mục chạy test lên chỗ có <c>ShopeeSuite.sln</c>.</summary>
    private static string GocRepo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "ShopeeSuite.sln")))
        {
            d = d.Parent;
        }
        Assert.NotNull(d);
        return d!.FullName;
    }

    // ===== 3. Cờ DaGuiChuanBiHang chạy thật qua cầu nối =====

    /// <summary>Runner tối giản: chỉ thư mục phiếu + tỉnh. Mọi callback null ⇒ tắt bước trả hàng và bước tải lại
    /// phiếu thiếu, để bài test chỉ còn đúng nhánh đơn/địa chỉ.</summary>
    private static ShopFlowRunner Runner(BridgeTestRig rig, string invoiceDir)
        => new(rig.Channel, rig.Log, invoiceDir, Tinh, syncCallback: null, finalDoneSns: null,
            onOrderPrepared: null, returnCountLast: null, saveReturnCount: null, saveReturnCodes: null,
            layDonThieuPhieu: null, demMaTraChuaBiet: null, dangCoCanhBaoDiaChi: null);

    private static string ThuMucTam() =>
        Path.Combine(Path.GetTempPath(), "xlds_thulai_" + Guid.NewGuid().ToString("N"));

    private static object LoDonRong() => new { action = "pageData", kind = "orders", data = Array.Empty<object>() };

    private static async Task NhanLenhAsync(BridgeTestRig rig, string action)
    {
        using var lenh = await rig.NhanLenhAsync();
        Assert.Equal(action, lenh.RootElement.GetProperty("action").GetString());
    }

    /// <summary>Shop CÓ đơn Chờ Lấy Hàng ⇒ lệnh chuẩn bị hàng đã bay ⇒ cờ BẬT (shop này mất quyền chạy lại).</summary>
    [Fact]
    public async Task ShopCoDon_DaGuiChuanBiHang_BatCo()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var dir = ThuMucTam();
        try
        {
            var flow = Runner(rig, dir);
            var chay = flow.RunShopOrdersAsync("shop-id-1", "shop1", toShip: 3, CancellationToken.None);

            await NhanLenhAsync(rig, "syncOrders");
            await rig.GuiAsync(LoDonRong());
            await NhanLenhAsync(rig, "setPickupAddress");
            await rig.GuiAsync(new { action = "pickupDone", ok = true });
            await NhanLenhAsync(rig, "prepareNextOrder");
            await rig.GuiAsync(new { action = "noOrder" });
            await NhanLenhAsync(rig, "setPickupAddressToOther");
            await rig.GuiAsync(new { action = "pickupOtherDone", ok = true });

            await chay;

            Assert.True(flow.DaGuiChuanBiHang);
            // Vòng ngoài đọc đúng cờ này ⇒ shop như vậy KHÔNG bao giờ được xếp hàng đợi thử lại.
            Assert.False(OrdersBridgeSession.NenThuLaiShopRoiOan(
                flow.DaGuiChuanBiHang, loiCauNoi: true, daXongViecShop: false, daThuLaiShopNay: false,
                soShopDaXepThuLai: 0));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* thư mục tạm */ }
        }
    }

    /// <summary>Shop 0 đơn (không banner) ⇒ không gửi lệnh nào của bước chuẩn bị hàng ⇒ cờ TẮT ⇒ shop rơi vì cầu
    /// nối ở lượt sau vẫn được cứu.</summary>
    [Fact]
    public async Task ShopKhongDon_KhongGuiChuanBiHang_CoTat()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var dir = ThuMucTam();
        try
        {
            var flow = Runner(rig, dir);
            var chay = flow.RunShopOrdersAsync("shop-id-1", "shop1", toShip: 0, CancellationToken.None);

            await NhanLenhAsync(rig, "syncOrders");
            await rig.GuiAsync(LoDonRong());
            await chay;

            Assert.False(flow.DaGuiChuanBiHang);
            Assert.True(OrdersBridgeSession.NenThuLaiShopRoiOan(
                flow.DaGuiChuanBiHang, loiCauNoi: true, daXongViecShop: false, daThuLaiShopNay: false,
                soShopDaXepThuLai: 0));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* thư mục tạm */ }
        }
    }

    /// <summary>
    /// <b>Shop SAU không được thừa hưởng cờ của shop TRƯỚC.</b> Cùng lớp lỗi đã cắn với <c>PickupOkShop</c>: quên
    /// reset là shop sạch mang cờ của shop đã in phiếu ⇒ hoặc bỏ oan một shop đáng cứu, hoặc (tệ hơn) một shop đã
    /// in phiếu được chạy lại.
    /// </summary>
    [Fact]
    public async Task ShopSau_CoDaResetVeTat()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var dir = ThuMucTam();
        try
        {
            var flow = Runner(rig, dir);

            // Shop 1: có đơn → cờ bật.
            var shop1 = flow.RunShopOrdersAsync("shop-id-1", "shop1", toShip: 3, CancellationToken.None);
            await NhanLenhAsync(rig, "syncOrders");
            await rig.GuiAsync(LoDonRong());
            await NhanLenhAsync(rig, "setPickupAddress");
            await rig.GuiAsync(new { action = "pickupDone", ok = true });
            await NhanLenhAsync(rig, "prepareNextOrder");
            await rig.GuiAsync(new { action = "noOrder" });
            await NhanLenhAsync(rig, "setPickupAddressToOther");
            await rig.GuiAsync(new { action = "pickupOtherDone", ok = true });
            await shop1;
            Assert.True(flow.DaGuiChuanBiHang);

            // Shop 2: 0 đơn → cờ phải SẠCH lại ngay từ đầu shop.
            var shop2 = flow.RunShopOrdersAsync("shop-id-2", "shop2", toShip: 0, CancellationToken.None);
            await NhanLenhAsync(rig, "syncOrders");
            await rig.GuiAsync(LoDonRong());
            await shop2;

            Assert.False(flow.DaGuiChuanBiHang);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* thư mục tạm */ }
        }
    }

    /// <summary>
    /// Ca ĐÁNG SỢ NHẤT: lệnh <c>prepareNextOrder</c> đã bay rồi cầu nối mới đứt — ta KHÔNG biết extension đã chuẩn
    /// bị hàng tới đâu. Cờ phải BẬT (đặt TRƯỚC lúc gửi, không phải sau khi nhận trả lời), nếu không shop này sẽ
    /// được chạy lại và in phiếu lần hai trên đơn thật.
    /// </summary>
    [Fact]
    public async Task CauNoiDutNGAYSAUKhiGuiChuanBiHang_CoVanBAT()
    {
        await using var rig = await BridgeTestRig.StartAsync(choNoiLai: ChoNoiLaiTest);
        var dir = ThuMucTam();
        try
        {
            var flow = Runner(rig, dir);
            var chay = flow.RunShopOrdersAsync("shop-id-1", "shop1", toShip: 3, CancellationToken.None);

            await NhanLenhAsync(rig, "syncOrders");
            await rig.GuiAsync(LoDonRong());
            await NhanLenhAsync(rig, "setPickupAddress");
            await rig.GuiAsync(new { action = "pickupDone", ok = true });
            await NhanLenhAsync(rig, "prepareNextOrder");

            // 19:46:05 — network service dựng lại, WebSocket sập giữa chặng Chuẩn bị hàng.
            await rig.NgatKetNoiAsync();

            var ex = await Assert.ThrowsAsync<CauNoiRotGiuaChangException>(() => chay);
            Assert.True(OrdersBridgeSession.LaLoiCauNoi(ex));

            Assert.True(flow.DaGuiChuanBiHang);
            Assert.False(OrdersBridgeSession.NenThuLaiShopRoiOan(
                flow.DaGuiChuanBiHang, OrdersBridgeSession.LaLoiCauNoi(ex), daXongViecShop: false,
                daThuLaiShopNay: false, soShopDaXepThuLai: 0));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* thư mục tạm */ }
        }
    }

    /// <summary>
    /// Ca CỦA BẢN VÁ: cầu nối đứt ở chặng <c>syncOrders</c> — trước cả bước địa chỉ, nên chắc chắn chưa gửi lệnh
    /// chuẩn bị hàng nào. Shop này rơi OAN và phải được xếp hàng đợi thử lại.
    /// </summary>
    [Fact]
    public async Task CauNoiDutTruocBuocChuanBiHang_CoTAT_VaDuocXepThuLai()
    {
        await using var rig = await BridgeTestRig.StartAsync(choNoiLai: ChoNoiLaiTest);
        var dir = ThuMucTam();
        try
        {
            var flow = Runner(rig, dir);
            var chay = flow.RunShopOrdersAsync("shop-id-1", "shop1", toShip: 3, CancellationToken.None);

            await NhanLenhAsync(rig, "syncOrders");
            await rig.NgatKetNoiAsync();

            var ex = await Assert.ThrowsAsync<CauNoiRotGiuaChangException>(() => chay);

            Assert.False(flow.DaGuiChuanBiHang);
            Assert.True(OrdersBridgeSession.NenThuLaiShopRoiOan(
                flow.DaGuiChuanBiHang, OrdersBridgeSession.LaLoiCauNoi(ex), daXongViecShop: false,
                daThuLaiShopNay: false, soShopDaXepThuLai: 0));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* thư mục tạm */ }
        }
    }
}
