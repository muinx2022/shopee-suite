using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// <b>Shop đang treo BANNER lỗi địa chỉ ⇒ vòng chạy vẫn thử lại bước đặt địa chỉ, dù lượt đó không xử đơn.</b>
/// <para>
/// Lỗ hổng vá 10/08/2026: <c>PickupOkShop</c> — tín hiệu DUY NHẤT để vòng ngoài gỡ banner + báo Hub + gỡ ở máy
/// khác — chỉ được đặt khi bước đặt địa chỉ THỰC SỰ chạy, mà bước đó xưa nay chỉ chạy khi <c>toShip &gt; 0</c>.
/// Shop ít đơn (nhật ký thật: <c>piko.store1</c>, "Chờ Lấy Hàng: 0" nhiều vòng liền) vì thế treo banner vĩnh viễn,
/// dù địa chỉ có thể đã đặt được từ lâu. Không test nào cũ bắt được: chúng chỉ canh nhánh CÓ đơn.
/// </para>
/// </summary>
public class ThuLaiDiaChiChoBannerTests
{
    private const string Tinh = "Thanh Hóa";

    /// <summary>Runner chỉ rót đúng thứ cần: thư mục phiếu + callback "shop này có banner không".
    /// Mọi callback khác null ⇒ tắt bước trả hàng và bước tải lại phiếu thiếu, để test chỉ còn nhánh địa chỉ.</summary>
    private static ShopFlowRunner Runner(BridgeTestRig rig, string? invoiceDir, Func<string, bool>? coBanner)
        => new(rig.Channel, rig.Log, invoiceDir, Tinh, syncCallback: null, finalDoneSns: null,
            onOrderPrepared: null, returnCountLast: null, saveReturnCount: null, saveReturnCodes: null,
            layDonThieuPhieu: null, demMaTraChuaBiet: null, dangCoCanhBaoDiaChi: coBanner);

    private static string ThuMucTam() =>
        Path.Combine(Path.GetTempPath(), "xlds_banner_" + Guid.NewGuid().ToString("N"));

    private static object LoDonRong() => new { action = "pageData", kind = "orders", data = Array.Empty<object>() };

    private static async Task NhanLenhAsync(BridgeTestRig rig, string action)
    {
        using var lenh = await rig.NhanLenhAsync();
        Assert.Equal(action, lenh.RootElement.GetProperty("action").GetString());
    }

    // ===== 1. Luật THUẦN: lượt này có chạy bước địa chỉ không, và để làm gì =====

    // Nhánh mong đợi truyền bằng TÊN (nameof — hằng biên dịch, sai tên là đỏ ngay): enum BuocDiaChi là internal
    // nên không đặt thẳng vào chữ ký test public được (CS0051).
    [Theory]
    // Có đơn + có thư mục phiếu ⇒ đường cũ, không đổi (dù có banner hay không).
    [InlineData(3, true, false, nameof(BuocDiaChi.DatRoiXuDon))]
    [InlineData(3, true, true, nameof(BuocDiaChi.DatRoiXuDon))]
    // 0 đơn: chỉ chạy khi ĐANG có banner cần gỡ.
    [InlineData(0, true, true, nameof(BuocDiaChi.ThuLaiChoCanhBao))]
    [InlineData(0, true, false, nameof(BuocDiaChi.Bo))]
    // Có đơn nhưng THIẾU thư mục phiếu (in không nổi) ⇒ vẫn không bỏ cơ hội gỡ banner.
    [InlineData(3, false, true, nameof(BuocDiaChi.ThuLaiChoCanhBao))]
    [InlineData(3, false, false, nameof(BuocDiaChi.Bo))]
    public void QuyetDinhBuocDiaChi_MaTran(int toShip, bool coThuMucPhieu, bool coBanner, string mong)
        => Assert.Equal(mong, ShopFlowRunner.QuyetDinhBuocDiaChi(toShip, coThuMucPhieu, coBanner).ToString());

    // ===== 2. Chạy thật qua cầu nối =====

    /// <summary>
    /// Shop 0 đơn Chờ Lấy Hàng nhưng ĐANG treo banner ⇒ vẫn gửi <c>setPickupAddress</c>; đặt được thì
    /// <c>PickupOkShop</c> = nhãn shop (vòng ngoài gỡ banner + báo Hub) và địa chỉ được TRẢ VỀ địa chỉ khác — kết
    /// thúc y hệt một shop chạy trọn vòng bình thường, không để shop treo tag lấy hàng ở địa chỉ tỉnh.
    /// Tuyệt đối KHÔNG có <c>prepareNextOrder</c>: không có đơn nào để chuẩn bị.
    /// </summary>
    [Fact]
    public async Task ShopKhongDon_CoBanner_DatDuocDiaChi_DatPickupOkShop_VaTraDiaChiVeKhac()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var dir = ThuMucTam();
        try
        {
            var flow = Runner(rig, dir, coBanner: _ => true);
            var chay = flow.RunShopOrdersAsync("shop-id-1", "shop1", toShip: 0, CancellationToken.None);

            await NhanLenhAsync(rig, "syncOrders");
            await rig.GuiAsync(LoDonRong());

            await NhanLenhAsync(rig, "setPickupAddress");
            await rig.GuiAsync(new { action = "pickupDone", ok = true });

            await NhanLenhAsync(rig, "setPickupAddressToOther");
            await rig.GuiAsync(new { action = "pickupOtherDone", ok = true });

            var (_, slips) = await chay;

            Assert.Equal("shop1", flow.PickupOkShop);
            Assert.Null(flow.PickupFailedShop);
            Assert.Equal(0, slips);

            // Không lệnh nào nữa — nhất là KHÔNG prepareNextOrder.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => rig.NhanLenhAsync(TimeSpan.FromMilliseconds(300)));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* thư mục tạm */ }
        }
    }

    /// <summary>
    /// Vẫn không đặt được ⇒ banner Ở LẠI (<c>PickupOkShop</c> null) NHƯNG cũng KHÔNG đặt <c>PickupFailedShop</c>:
    /// banner đã treo sẵn rồi. Đặt cờ ở đây là đếm một shop khỏe thành shop hỏng, bắn lại tin Slack và đẩy Hub
    /// một lượt vô ích ở MỖI vòng — mà lượt này chẳng có đơn nào để "bỏ qua".
    /// </summary>
    [Fact]
    public async Task ShopKhongDon_CoBanner_VanLoi_GiuBanner_KhongDatCoLoi()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var dir = ThuMucTam();
        try
        {
            var flow = Runner(rig, dir, coBanner: _ => true);
            var chay = flow.RunShopOrdersAsync("shop-id-1", "shop1", toShip: 0, CancellationToken.None);

            await NhanLenhAsync(rig, "syncOrders");
            await rig.GuiAsync(LoDonRong());

            await NhanLenhAsync(rig, "setPickupAddress");
            await rig.GuiAsync(new { action = "pickupDone", ok = false });

            await chay;

            Assert.Null(flow.PickupOkShop);
            Assert.Null(flow.PickupFailedShop);
            // KHÔNG trả địa chỉ về chỗ khác: có đặt được đâu mà trả.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => rig.NhanLenhAsync(TimeSpan.FromMilliseconds(300)));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* thư mục tạm */ }
        }
    }

    /// <summary>
    /// Shop KHÔNG có banner + 0 đơn ⇒ tuyệt đối không đụng vào địa chỉ của nó. Mỗi lượt đặt địa chỉ là một thao
    /// tác GHI thật trên Seller Centre; chạy cho mọi shop là tự đẻ ra rủi ro ở chỗ chẳng ai cần.
    /// </summary>
    [Fact]
    public async Task ShopKhongDon_KhongBanner_KhongDungToiDiaChi()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var dir = ThuMucTam();
        try
        {
            var flow = Runner(rig, dir, coBanner: _ => false);
            var chay = flow.RunShopOrdersAsync("shop-id-1", "shop1", toShip: 0, CancellationToken.None);

            await NhanLenhAsync(rig, "syncOrders");
            await rig.GuiAsync(LoDonRong());
            await chay;

            Assert.Null(flow.PickupOkShop);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => rig.NhanLenhAsync(TimeSpan.FromMilliseconds(300)));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* thư mục tạm */ }
        }
    }

    /// <summary>
    /// Callback đọc DB NÉM lỗi ⇒ coi như shop không có banner, shop vẫn chạy trọn. Thà bỏ một lượt thử lại còn hơn
    /// để một lỗi SQLite giết cả vòng shop.
    /// </summary>
    [Fact]
    public async Task CallbackNemLoi_CoiNhuKhongCoBanner_ShopVanChayTron()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var dir = ThuMucTam();
        try
        {
            var flow = Runner(rig, dir, coBanner: _ => throw new InvalidOperationException("DB hỏng giả lập"));
            var chay = flow.RunShopOrdersAsync("shop-id-1", "shop1", toShip: 0, CancellationToken.None);

            await NhanLenhAsync(rig, "syncOrders");
            await rig.GuiAsync(LoDonRong());
            var (don, _) = await chay;

            Assert.Equal(0, don);
            Assert.Null(flow.PickupOkShop);
            Assert.True(rig.CoLog("Không đọc được danh sách cảnh báo địa chỉ"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* thư mục tạm */ }
        }
    }

    /// <summary>Nhãn shop RỖNG (picker không đọc được tên) vẫn phải ra chuỗi KHÁC null — y như vòng shop thường,
    /// kẻo tín hiệu gỡ banner mất theo cái nhãn. Nhãn rỗng thì cũng KHÔNG hỏi callback (không tra được banner nào).</summary>
    [Fact]
    public async Task NhanShopRong_KhongHoiCallback_KhongChayBuocDiaChi()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var dir = ThuMucTam();
        var soLanHoi = 0;
        try
        {
            var flow = Runner(rig, dir, coBanner: _ => { soLanHoi++; return true; });
            var chay = flow.RunShopOrdersAsync("shop-id-1", "   ", toShip: 0, CancellationToken.None);

            await NhanLenhAsync(rig, "syncOrders");
            await rig.GuiAsync(LoDonRong());
            await chay;

            Assert.Equal(0, soLanHoi);
            Assert.Null(flow.PickupOkShop);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => rig.NhanLenhAsync(TimeSpan.FromMilliseconds(300)));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* thư mục tạm */ }
        }
    }

    /// <summary>Nhánh CÓ đơn không được đổi tí nào: vẫn đặt địa chỉ → chuẩn bị hàng → trả địa chỉ về chỗ khác,
    /// kể cả khi shop đang treo banner (callback trả true).</summary>
    [Fact]
    public async Task ShopCoDon_CoBanner_VanDiDuongCu_CoChuanBiHang()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var dir = ThuMucTam();
        try
        {
            var flow = Runner(rig, dir, coBanner: _ => true);
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
            Assert.Equal("shop1", flow.PickupOkShop);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* thư mục tạm */ }
        }
    }
}
