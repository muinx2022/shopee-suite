using System;
using System.Threading;
using System.Threading.Tasks;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Hợp đồng MESSAGE của cầu nối Đơn hàng, chạy trên <see cref="OrdersBridgeChannel"/> THẬT (WebSocket loopback,
/// cổng trống) với <see cref="BridgeTestRig"/> đóng vai extension — trước đợt này vùng cầu nối KHÔNG có test nào.
/// <list type="bullet">
/// <item><c>captcha</c> → bật cờ + hoàn tất MỌI chặng (fan-out) để phiên thoát nhanh;</item>
/// <item><c>error</c> → CHỈ fault chặng ĐANG chờ, chặng khác để nguyên (fix 1B.3 — chống UnobservedTaskException);</item>
/// <item>hết giờ một chặng KHÔNG làm hỏng kênh: chặng sau vẫn nhận được dữ liệu;</item>
/// <item><c>pageData</c> về đúng chặng theo <c>kind</c>; gửi lệnh khi CHƯA mở cổng thì ném NGAY.</item>
/// </list>
/// </summary>
public class OrdersBridgeChannelTests
{
    // ===== 1. captcha: bật cờ + fan-out mọi chặng =====

    [Fact]
    public async Task Captcha_BatCo_VaHoanTatMoiChangDangCho()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var orders = rig.Channel.ArmOrders();
        var prepare = rig.Channel.ArmPrepare();
        var doiOrders = rig.Channel.AwaitAsync(orders, TimeSpan.FromSeconds(10), CancellationToken.None);

        await rig.GuiAsync(new { action = "captcha" });

        Assert.Null(await doiOrders);                       // chặng đang chờ: về ngay với null
        Assert.True(rig.Channel.CaptchaSeen);               // cờ để caller phân biệt "captcha" với "không có đơn"
        Assert.Null(await prepare.Task);                    // fan-out: chặng khác cũng được hoàn tất (null = hết đơn/captcha)
    }

    [Fact]
    public async Task Captcha_ResetStages_XoaCo_ChoVongSau()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var orders = rig.Channel.ArmOrders();
        await rig.GuiAsync(new { action = "captcha" });
        await rig.Channel.AwaitAsync(orders, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.True(rig.Channel.CaptchaSeen);

        rig.Channel.ResetStages();

        Assert.False(rig.Channel.CaptchaSeen);
        Assert.False(rig.Channel.Ready.Task.IsCompleted); // chặng đã được thay MỚI (không dính kết quả vòng trước)
    }

    // ===== 2. error: chỉ fault chặng đang chờ =====

    [Fact]
    public async Task Loi_ChiFaultChangDangCho_ChangKhacDeNguyen()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var orders = rig.Channel.ArmOrders();
        var returns = rig.Channel.ArmReturns(); // chặng KHÔNG ai await
        var doiOrders = rig.Channel.AwaitAsync(orders, TimeSpan.FromSeconds(10), CancellationToken.None);

        await rig.GuiAsync(new { action = "error", message = "boom" });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => doiOrders);
        Assert.Contains("boom", ex.Message);
        Assert.False(returns.Task.IsCompleted); // để NGUYÊN (pending) → không đẻ exception mồ côi
    }

    [Fact]
    public async Task Loi_KhongCoAiCho_KhongLamGiCa()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var orders = rig.Channel.ArmOrders();

        await rig.GuiAsync(new { action = "error", message = "lac-nhip" });
        Assert.True(await rig.ChoLogAsync("extension LỖI: lac-nhip"));

        // Không chặng nào bị fault → lệnh sau vẫn chạy bình thường trên chính chặng đó.
        await rig.GuiAsync(new { action = "pageData", kind = "orders", data = "[]" });
        Assert.Equal("[]", await rig.Channel.AwaitAsync(orders, TimeSpan.FromSeconds(10), CancellationToken.None));
    }

    // ===== 3. hết giờ một chặng không phá kênh =====

    [Fact]
    public async Task HetGio_ChiChangDo_KenhVanDungDuocTiep()
    {
        await using var rig = await BridgeTestRig.StartAsync();

        // Chặng 1: extension im lặng → TimeoutException đúng chặng đang chờ.
        var toShip = rig.Channel.ArmToShip();
        await Assert.ThrowsAsync<TimeoutException>(
            () => rig.Channel.AwaitAsync(toShip, TimeSpan.FromMilliseconds(150), CancellationToken.None));

        // Chặng 2 (armed lại) vẫn nhận được dữ liệu — kênh không bị "kẹt" sau lần hết giờ.
        var toShip2 = rig.Channel.ArmToShip();
        await rig.GuiAsync(new { action = "pageData", kind = "toShip", data = "12" });
        Assert.Equal("12", await rig.Channel.AwaitAsync(toShip2, TimeSpan.FromSeconds(10), CancellationToken.None));
    }

    // ===== 4. pageData về đúng chặng theo kind =====

    [Fact]
    public async Task PageData_VeDungChangTheoKind()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var orders = rig.Channel.ArmOrders();
        var finals = rig.Channel.ArmFinals();

        // data là MẢNG (không phải chuỗi) → channel trả JSON thô cho hàm parse thuần.
        await rig.GuiJsonAsync("{\"action\":\"pageData\",\"kind\":\"finals\",\"data\":[{\"orderSn\":\"SN1\"}]}");
        await rig.GuiAsync(new { action = "pageData", kind = "orders", data = "[{\"orderSn\":\"SN2\"}]" });

        Assert.Contains("SN1", await rig.Channel.AwaitAsync(finals, TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.Contains("SN2", await rig.Channel.AwaitAsync(orders, TimeSpan.FromSeconds(10), CancellationToken.None));
    }

    [Fact]
    public async Task GuiLenh_ChuaMoCong_NemNgay_KhongCho()
    {
        using var ch = new OrdersBridgeChannel();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ch.SendAsync(new { action = "readToShip" }));
        Assert.Contains("Cầu nối chưa khởi động", ex.Message);
    }
}
