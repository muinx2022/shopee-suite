using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// <b>T3 (review 11/08): kết quả chuẩn bị hàng KHÔNG kèm mã đơn thì DỪNG, không đi tiếp mù.</b>
/// <para>Bối cảnh: <c>.order-sn</c> đổi markup ⇒ extension đọc ra mã rỗng. Bản trước C# vẫn +1 đếm chuẩn bị cho
/// chuỗi rỗng, lưu phiếu vào tên file suy từ chuỗi rỗng ("phieu.pdf" — các đơn ghi đè lẫn nhau) và log
/// "lưu phiếu OK". Extension nay chặn ở nguồn (không bấm khi thiếu mã); lớp C# này đỡ bản extension đời cũ —
/// và phải DỪNG vòng vì với extension cũ, mỗi lượt lặp nữa là thêm một đơn thật bị arrange mù.</para>
/// </summary>
public class ChuanBiHangThieuMaTests
{
    private const string Tinh = "Thanh Hóa";

    private static ShopFlowRunner Runner(BridgeTestRig rig, string invoiceDir, Action<string, string> onOrderPrepared)
        => new(rig.Channel, rig.Log, invoiceDir, Tinh, syncCallback: null, finalDoneSns: null,
            onOrderPrepared: onOrderPrepared, returnCountLast: null, saveReturnCount: null, saveReturnCodes: null,
            layDonThieuPhieu: null, demMaTraChuaBiet: null, dangCoCanhBaoDiaChi: null);

    private static string ThuMucTam() =>
        Path.Combine(Path.GetTempPath(), "xlds_thieuma_" + Guid.NewGuid().ToString("N"));

    private static object LoDonRong() => new { action = "pageData", kind = "orders", data = Array.Empty<object>() };

    private static async Task NhanLenhAsync(BridgeTestRig rig, string action)
    {
        using var lenh = await rig.NhanLenhAsync();
        Assert.Equal(action, lenh.RootElement.GetProperty("action").GetString());
    }

    [Fact]
    public async Task OrderPreparedThieuMa_DungVong_KhongLuuPhieu_KhongDem()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var dir = ThuMucTam();
        try
        {
            var daDem = new List<string>();
            var flow = Runner(rig, dir, (shop, sn) => daDem.Add(sn));
            var chay = flow.RunShopOrdersAsync("shop-id-1", "shop1", toShip: 3, CancellationToken.None);

            await NhanLenhAsync(rig, "syncOrders");
            await rig.GuiAsync(LoDonRong());
            await NhanLenhAsync(rig, "setPickupAddress");
            await rig.GuiAsync(new { action = "pickupDone", ok = true });
            await NhanLenhAsync(rig, "prepareNextOrder");
            await rig.GuiAsync(new { action = "orderPrepared", orderCode = "", slipBase64 = "JVBERi0xLjQK", tracking = "SPXVN000" });

            // DỪNG vòng: lệnh kế phải là bước trả địa chỉ, KHÔNG phải prepareNextOrder lần nữa.
            await NhanLenhAsync(rig, "setPickupAddressToOther");
            await rig.GuiAsync(new { action = "pickupOtherDone", ok = true });
            await chay;

            Assert.Empty(daDem); // không +1 đếm cho chuỗi rỗng
            Assert.Empty(Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                : Array.Empty<string>()); // không ghi "phieu.pdf"
            Assert.True(rig.CoLog("KHÔNG kèm mã đơn"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* thư mục tạm */ }
        }
    }

    /// <summary>Extension MỚI báo <c>prepareBlocked</c> (mọi card có nút đều không đọc được mã): C# phải log
    /// ĐÚNG BỆNH ("KHÔNG đọc được mã đơn … KHÔNG phải hết đơn") chứ không được in "Hết đơn cần Chuẩn bị hàng"
    /// y hệt shop khỏe — selector hỏng cả fleet mà nhật ký trông bình thường thì không ai biết (phản biện
    /// 11/08). Flow vẫn đi tiếp các bước sau của shop (trả địa chỉ, check trả hàng).</summary>
    [Fact]
    public async Task PrepareBlocked_LogDungBenh_KhongPhaiHetDon_FlowVanDiTiep()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var dir = ThuMucTam();
        try
        {
            var daDem = new List<string>();
            var flow = Runner(rig, dir, (shop, sn) => daDem.Add(sn));
            var chay = flow.RunShopOrdersAsync("shop-id-1", "shop1", toShip: 3, CancellationToken.None);

            await NhanLenhAsync(rig, "syncOrders");
            await rig.GuiAsync(LoDonRong());
            await NhanLenhAsync(rig, "setPickupAddress");
            await rig.GuiAsync(new { action = "pickupDone", ok = true });
            await NhanLenhAsync(rig, "prepareNextOrder");
            await rig.GuiAsync(new { action = "prepareBlocked" });

            await NhanLenhAsync(rig, "setPickupAddressToOther"); // flow vẫn đi tiếp, không đổ shop
            await rig.GuiAsync(new { action = "pickupOtherDone", ok = true });
            await chay;

            Assert.Empty(daDem);
            Assert.True(rig.CoLog("KHÔNG đọc được mã đơn"));
            Assert.False(rig.CoLog("Hết đơn cần Chuẩn bị hàng")); // không được nói dối là hết đơn
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* thư mục tạm */ }
        }
    }

    /// <summary>ĐỐI CHỨNG: có mã đơn thì vòng đi tiếp như cũ — đếm đúng đơn, lưu phiếu, hỏi tiếp đơn kế.</summary>
    [Fact]
    public async Task OrderPreparedCoMa_VongTiepTuc_DemVaLuuPhieu()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var dir = ThuMucTam();
        try
        {
            var daDem = new List<string>();
            var flow = Runner(rig, dir, (shop, sn) => daDem.Add(sn));
            var chay = flow.RunShopOrdersAsync("shop-id-1", "shop1", toShip: 3, CancellationToken.None);

            await NhanLenhAsync(rig, "syncOrders");
            await rig.GuiAsync(LoDonRong());
            await NhanLenhAsync(rig, "setPickupAddress");
            await rig.GuiAsync(new { action = "pickupDone", ok = true });
            await NhanLenhAsync(rig, "prepareNextOrder");
            await rig.GuiAsync(new { action = "orderPrepared", orderCode = "260811AAAA01", slipBase64 = "JVBERi0xLjQK" });
            await NhanLenhAsync(rig, "prepareNextOrder"); // vòng TIẾP TỤC sau đơn hợp lệ
            await rig.GuiAsync(new { action = "noOrder" });
            await NhanLenhAsync(rig, "setPickupAddressToOther");
            await rig.GuiAsync(new { action = "pickupOtherDone", ok = true });
            await chay;

            Assert.Equal("260811AAAA01", Assert.Single(daDem));
            Assert.Single(Directory.GetFiles(dir, "*.pdf", SearchOption.AllDirectories));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* thư mục tạm */ }
        }
    }
}
