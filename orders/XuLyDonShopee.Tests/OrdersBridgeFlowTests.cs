using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// FLOW của một shop (<see cref="ShopFlowRunner"/>) chạy qua cầu nối THẬT (<see cref="BridgeTestRig"/>): kiểm hai
/// mắt xích hay hỏng nhất mà trước đây không có test nào — <b>tải lại phiếu</b> (roundtrip lệnh → base64 → file
/// PDF) và <b>check đơn trả hàng</b> (bỏ lượt khi sai tab / không đọc được số; lưu mã + chốt mốc khi đúng tab).
/// Không mở trình duyệt: extension là một client WebSocket giả.
/// </summary>
public class OrdersBridgeFlowTests
{
    private const string Tinh = "Thanh Hóa";

    /// <summary>Runner tối giản: chỉ rót các callback mà test cần (còn lại null = tắt bước tương ứng).</summary>
    private static ShopFlowRunner Runner(
        BridgeTestRig rig,
        string? invoiceDir = null,
        Func<string, int?>? returnCountLast = null,
        Action<string, int>? saveReturnCount = null,
        Func<IReadOnlyList<YeuCauTraHang>, string>? saveReturnCodes = null,
        Func<string, string, IReadOnlyList<SyncedOrder>, CancellationToken, Task>? syncCallback = null)
        => new(rig.Channel, rig.Log, invoiceDir, Tinh, syncCallback, finalDoneSns: null,
            onOrderPrepared: null, returnCountLast, saveReturnCount, saveReturnCodes);

    private static string ThuMucTam() =>
        Path.Combine(Path.GetTempPath(), "xlds_slip_" + Guid.NewGuid().ToString("N"));

    private static string Base64(string noiDung) => Convert.ToBase64String(Encoding.UTF8.GetBytes(noiDung));

    // ===== 1. Tải lại phiếu: roundtrip lệnh → base64 → file PDF =====

    [Fact]
    public async Task RedownloadSlip_NhanBase64PdfThat_LuuFileTheoMaDon()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var dir = ThuMucTam();
        try
        {
            var flow = Runner(rig, invoiceDir: dir);
            var chay = flow.RedownloadSlipAsync("SN-1", CancellationToken.None);

            using (var lenh = await rig.NhanLenhAsync())
            {
                Assert.Equal("redownloadSlip", lenh.RootElement.GetProperty("action").GetString());
                Assert.Equal("SN-1", lenh.RootElement.GetProperty("orderSn").GetString());
            }
            await rig.GuiAsync(new { action = "slipRedownloaded", slipBase64 = Base64("%PDF-1.4 giả lập") });

            Assert.True(await chay);
            Assert.True(File.Exists(Path.Combine(dir, "SN-1.pdf")));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* thư mục tạm */ }
        }
    }

    [Fact]
    public async Task RedownloadSlip_KhongPhaiPdf_TraFalse_KhongGhiFile()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var dir = ThuMucTam();
        try
        {
            var flow = Runner(rig, invoiceDir: dir);
            var chay = flow.RedownloadSlipAsync("SN-2", CancellationToken.None);
            using (await rig.NhanLenhAsync()) { }

            // Extension trả về HTML/rác (không magic %PDF) → KHÔNG được lưu thành .pdf.
            await rig.GuiAsync(new { action = "slipRedownloaded", slipBase64 = Base64("<html>lỗi</html>") });

            Assert.False(await chay);
            Assert.False(File.Exists(Path.Combine(dir, "SN-2.pdf")));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* thư mục tạm */ }
        }
    }

    [Fact]
    public async Task RedownloadSlip_KhongThayDon_ExtensionTraRong_TraFalse()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var dir = ThuMucTam();
        try
        {
            var flow = Runner(rig, invoiceDir: dir);
            var chay = flow.RedownloadSlipAsync("SN-3", CancellationToken.None);
            using (await rig.NhanLenhAsync()) { }

            await rig.GuiAsync(new { action = "slipRedownloaded", slipBase64 = "" });

            Assert.False(await chay);
            Assert.True(rig.CoLog("Không nhận được phiếu đơn SN-3"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* thư mục tạm */ }
        }
    }

    // ===== 2. Check đơn trả hàng =====

    /// <summary>HTML đầu dòng ĐÚNG khuôn trang thật (class <c>order-id</c> / <c>return-id</c> + ô
    /// <c>id-content</c>) để <c>TraHangParser.TachMa</c> ghép được cả hai mã.</summary>
    private static string DongHtml(string maDon, string maYeuCau) =>
        "<div class=\"id order-id\"><span>Mã đơn hàng</span><span class=\"id-content\">" + maDon + "</span></div>"
        + "<div class=\"id return-id\"><span>Mã yêu cầu trả hàng</span><span class=\"id-content\">" + maYeuCau + "</span></div>";

    /// <summary>Message <c>pageData kind=returns</c> y khuôn extension gửi.</summary>
    private static object TrangTraHang(string soYeuCauText, bool tabTraHang, params object[] dong) => new
    {
        action = "pageData",
        kind = "returns",
        data = new { soYeuCauText, sortApplied = true, tabTraHang, list = dong },
    };

    [Fact]
    public async Task CheckTraHang_KhongChonDuocTab_BoLuot_KhongGhiMoc_KhongLuuMa()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var moc = new List<(string Shop, int So)>();
        var daLuu = new List<int>();
        var flow = Runner(rig,
            returnCountLast: _ => 7,
            saveReturnCount: (shop, so) => moc.Add((shop, so)),
            saveReturnCodes: cap => { daLuu.Add(cap.Count); return "ok"; });

        var chay = flow.CheckDonTraHangAsync("shop1", CancellationToken.None);
        using (var lenh = await rig.NhanLenhAsync())
        {
            Assert.Equal("readReturnRequests", lenh.RootElement.GetProperty("action").GetString());
        }

        // 141 là số của tab "Tất cả" (gộp Đơn Hủy) — ghi vào mốc là ĐẦU ĐỘC mốc ⇒ phải BỎ LƯỢT.
        await rig.GuiAsync(TrangTraHang("141 Yêu cầu", tabTraHang: false,
            new { headHtml = DongHtml("260731AAAAAA", MaYeuCauHomNay()), laTraHang = true }));
        await chay;

        Assert.Empty(moc);      // mốc GIỮ NGUYÊN
        Assert.Empty(daLuu);    // không lưu mã nào
        Assert.True(rig.CoLog("BỎ LƯỢT"));
    }

    [Fact]
    public async Task CheckTraHang_KhongDocDuocSo_BoLuot_MocGiuNguyen()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var moc = new List<(string Shop, int So)>();
        var flow = Runner(rig,
            returnCountLast: _ => 7,
            saveReturnCount: (shop, so) => moc.Add((shop, so)),
            saveReturnCodes: _ => "ok");

        var chay = flow.CheckDonTraHangAsync("shop1", CancellationToken.None);
        using (await rig.NhanLenhAsync()) { }

        // Ô tổng chưa render → không có chữ số nào ⇒ SoYeuCau null.
        await rig.GuiAsync(TrangTraHang("Yêu cầu", tabTraHang: true));
        await chay;

        Assert.Empty(moc);
        Assert.True(rig.CoLog("KHÔNG đọc được số yêu cầu"));
    }

    [Fact]
    public async Task CheckTraHang_DungTab_LanDau_LuuMa_VaChotMoc()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var moc = new List<(string Shop, int So)>();
        var capDaLuu = new List<YeuCauTraHang>();
        var flow = Runner(rig,
            returnCountLast: _ => null,                       // shop chưa từng check → nhánh LẦN ĐẦU
            saveReturnCount: (shop, so) => moc.Add((shop, so)),
            saveReturnCodes: cap => { capDaLuu.AddRange(cap); return $"{cap.Count} đơn ghi mã mới"; });

        var maYeuCau = MaYeuCauHomNay();
        var chay = flow.CheckDonTraHangAsync("shop1", CancellationToken.None);
        using (await rig.NhanLenhAsync()) { }

        await rig.GuiAsync(TrangTraHang("3 Yêu cầu", tabTraHang: true,
            new { headHtml = DongHtml("260731AAAAAA", maYeuCau), laTraHang = true }));
        await chay;

        Assert.Equal(("shop1", 3), Assert.Single(moc));                       // chốt mốc = số vừa đọc
        Assert.Equal(new YeuCauTraHang("260731AAAAAA", maYeuCau), Assert.Single(capDaLuu));
    }

    [Fact]
    public async Task CheckTraHang_DaDinhCaptchaTruocDo_BoHanBuoc_KhongGuiLenh()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var moc = new List<(string Shop, int So)>();
        var flow = Runner(rig,
            returnCountLast: _ => 7,
            saveReturnCount: (shop, so) => moc.Add((shop, so)),
            saveReturnCodes: _ => "ok");
        rig.Channel.CaptchaSeen = true; // vòng chuẩn bị hàng vừa break vì captcha

        await flow.CheckDonTraHangAsync("shop1", CancellationToken.None);

        Assert.Empty(moc);
        Assert.True(rig.CoLog("đã dính captcha từ bước trước"));
        // Không gửi lệnh nào (nếu có, NhanLenhAsync sẽ nhận được thay vì hết giờ).
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => rig.NhanLenhAsync(TimeSpan.FromMilliseconds(300)));
    }

    // ===== 3. Vòng đọc đơn của một shop: gọi callback lưu đúng MỘT lần với đơn đã parse =====

    [Fact]
    public async Task RunShopOrders_DocDon_GoiCallbackLuuMotLan()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var luot = new List<(string ShopId, int SoDon)>();
        var flow = Runner(rig, syncCallback: (shopId, shopLogin, orders, ct) =>
        {
            luot.Add((shopId, orders.Count));
            return Task.CompletedTask;
        });

        // toShip = 0 → bỏ hẳn Phần B (không đặt địa chỉ, không in phiếu); callback trả hàng null → bỏ bước cuối.
        var chay = flow.RunShopOrdersAsync("shop-id-1", "shop1", toShip: 0, CancellationToken.None);
        using (var lenh = await rig.NhanLenhAsync())
        {
            Assert.Equal("syncOrders", lenh.RootElement.GetProperty("action").GetString());
        }

        await rig.GuiJsonAsync(JsonSerializer.Serialize(new
        {
            action = "pageData",
            kind = "orders",
            data = new[]
            {
                new { orderSn = "260731AAAAAA", status = "Chờ lấy hàng", buyer = "nguoimua" },
            },
        }));

        var (soDon, soPhieu) = await chay;
        Assert.Equal(1, soDon);
        Assert.Equal(0, soPhieu);
        Assert.Equal(("shop-id-1", 1), Assert.Single(luot));
    }

    // ===== 4. Tín hiệu "shop ĐẶT ĐƯỢC địa chỉ" (PickupOkShop) — vòng ngoài dùng để TỰ GỠ banner cũ =====

    /// <summary>Extension trả về đúng lô đơn RỖNG (bỏ hẳn bước "Số tiền cuối cùng" — không đơn nào cần).</summary>
    private static object LoDonRong() => new { action = "pageData", kind = "orders", data = Array.Empty<object>() };

    /// <summary>Nhận lệnh kế tiếp và chốt đúng tên action (một lệnh = một chặng).</summary>
    private static async Task NhanLenhAsync(BridgeTestRig rig, string action)
    {
        using var lenh = await rig.NhanLenhAsync();
        Assert.Equal(action, lenh.RootElement.GetProperty("action").GetString());
    }

    [Fact]
    public async Task DatDuocDiaChi_DanhDau_PickupOkShop_BangNhanShop()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var dir = ThuMucTam();
        try
        {
            var flow = Runner(rig, invoiceDir: dir);
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

            // Đây là bằng chứng DUY NHẤT "shop này hết lỗi địa chỉ" — thiếu nó thì banner cũ không bao giờ tự gỡ.
            Assert.Equal("shop1", flow.PickupOkShop);
            Assert.Null(flow.PickupFailedShop);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* thư mục tạm */ }
        }
    }

    [Fact]
    public async Task KhongDatDuocDiaChi_PickupOkShop_VanNull()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var dir = ThuMucTam();
        try
        {
            var flow = Runner(rig, invoiceDir: dir);
            var chay = flow.RunShopOrdersAsync("shop-id-1", "shop1", toShip: 3, CancellationToken.None);

            await NhanLenhAsync(rig, "syncOrders");
            await rig.GuiAsync(LoDonRong());

            await NhanLenhAsync(rig, "setPickupAddress");
            await rig.GuiAsync(new { action = "pickupDone", ok = false });

            var (_, slips) = await chay;

            Assert.Equal(0, slips);                        // không in phiếu (hành vi cũ, không đổi)
            Assert.Equal("shop1", flow.PickupFailedShop);
            Assert.Null(flow.PickupOkShop);                // gỡ banner lúc này là xoá đúng cảnh báo đang đúng
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* thư mục tạm */ }
        }
    }

    /// <summary>
    /// Shop 0 đơn Chờ Lấy Hàng: bước đặt địa chỉ KHÔNG chạy ⇒ chưa chứng minh được gì ⇒ <c>PickupOkShop</c> phải
    /// null. Trả nhãn ở đây là gỡ banner của shop chưa hề thử đặt địa chỉ.
    /// </summary>
    [Fact]
    public async Task ShopKhongCoDonChoLayHang_KhongChayBuocDiaChi_PickupOkShop_Null()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var dir = ThuMucTam();
        try
        {
            var flow = Runner(rig, invoiceDir: dir);
            var chay = flow.RunShopOrdersAsync("shop-id-1", "shop1", toShip: 0, CancellationToken.None);

            await NhanLenhAsync(rig, "syncOrders");
            await rig.GuiAsync(LoDonRong());
            await chay;

            Assert.Null(flow.PickupOkShop);
            // Và KHÔNG có lệnh nào nữa được gửi (setPickupAddress phải không chạy).
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => rig.NhanLenhAsync(TimeSpan.FromMilliseconds(300)));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* thư mục tạm */ }
        }
    }

    // ===== Nút "Check" trên banner lỗi địa chỉ: KiemTraLaiDiaChiAsync =====

    /// <summary>
    /// Lượt Check ĐẶT ĐƯỢC địa chỉ ⇒ trả true + đặt <c>PickupOkShop</c> (đó là căn cứ DUY NHẤT để vòng ngoài gỡ
    /// banner + báo Hub), và tuyệt đối KHÔNG được gửi lệnh nào khác: người dùng bấm nút này lúc đang nhìn banner
    /// đỏ, một lượt in phiếu ngoài ý muốn ở đây là hỏng việc thật.
    /// </summary>
    [Fact]
    public async Task KiemTraLaiDiaChi_DatDuoc_TraTrue_DatPickupOkShop_VaKhongLamGiThem()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var flow = Runner(rig);
        var chay = flow.KiemTraLaiDiaChiAsync("shop1", CancellationToken.None);

        await NhanLenhAsync(rig, "setPickupAddress");
        await rig.GuiAsync(new { action = "pickupDone", ok = true });

        Assert.True(await chay);
        Assert.Equal("shop1", flow.PickupOkShop);
        Assert.Null(flow.PickupFailedShop);

        // KHÔNG đọc đơn, KHÔNG chuẩn bị hàng, KHÔNG trả địa chỉ về chỗ khác.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => rig.NhanLenhAsync(TimeSpan.FromMilliseconds(300)));
    }

    /// <summary>Vẫn không đặt được ⇒ false + <c>PickupFailedShop</c>; banner phải Ở LẠI.</summary>
    [Fact]
    public async Task KiemTraLaiDiaChi_VanLoi_TraFalse_GiuBanner()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var flow = Runner(rig);
        var chay = flow.KiemTraLaiDiaChiAsync("shop1", CancellationToken.None);

        await NhanLenhAsync(rig, "setPickupAddress");
        await rig.GuiAsync(new { action = "pickupDone", ok = false });

        Assert.False(await chay);
        Assert.Equal("shop1", flow.PickupFailedShop);
        Assert.Null(flow.PickupOkShop);   // gỡ banner lúc này là xoá đúng cảnh báo đang ĐÚNG
    }

    /// <summary>
    /// Captcha ⇒ <b>KHÔNG kết luận</b>: cả hai cờ đều null. Coi là "vẫn lỗi" thì giữ banner oan; coi là "hết lỗi"
    /// thì gỡ banner của shop chưa hề kiểm được. Không biết thì phải nói không biết.
    /// </summary>
    [Fact]
    public async Task KiemTraLaiDiaChi_Captcha_KhongKetLuan_CaHaiCoDeuNull()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var flow = Runner(rig);
        var chay = flow.KiemTraLaiDiaChiAsync("shop1", CancellationToken.None);

        await NhanLenhAsync(rig, "setPickupAddress");
        rig.Channel.CaptchaSeen = true;
        await rig.GuiAsync(new { action = "pickupDone", ok = false });

        Assert.False(await chay);
        Assert.Null(flow.PickupOkShop);
        Assert.Null(flow.PickupFailedShop);
    }

    /// <summary>Nhãn shop RỖNG (picker không đọc được tên) vẫn phải ra chuỗi KHÁC null — y như vòng shop thường,
    /// kẻo tín hiệu gỡ banner mất theo cái nhãn.</summary>
    [Fact]
    public async Task KiemTraLaiDiaChi_NhanShopRong_VanRaChuoiKhacNull()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var flow = Runner(rig);
        var chay = flow.KiemTraLaiDiaChiAsync("   ", CancellationToken.None);

        await NhanLenhAsync(rig, "setPickupAddress");
        await rig.GuiAsync(new { action = "pickupDone", ok = true });

        Assert.True(await chay);
        Assert.Equal("(không rõ shop)", flow.PickupOkShop);
    }

    /// <summary>Mã yêu cầu trả hàng của HÔM NAY (<c>yyMMdd</c> + đuôi) — cửa sổ lọc theo ngày yêu cầu nên mã cứng
    /// sẽ hết hạn theo thời gian, phải sinh động.</summary>
    private static string MaYeuCauHomNay() => DateTime.Now.ToString("yyMMdd") + "0TS2VYAW3";
}
