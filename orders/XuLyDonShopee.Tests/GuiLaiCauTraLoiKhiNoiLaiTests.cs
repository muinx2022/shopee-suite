using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// LƯỚI CUỐI chống "lệnh đã gửi bị mất": extension GIỮ LẠI câu trả lời không gửi được rồi tự bắn lại ngay khi
/// nối lại (hàng đợi <c>goiTon</c> trong <c>extensions/shopee-orders/core.js</c>).
/// <para>
/// Chuỗi thật đang chữa: C# gửi <c>prepareNextOrder</c> → extension làm 30–90s → cầu nối đứt (240s/lần cả ngày
/// 10/08/2026) → extension tính XONG kết quả nhưng <c>ws.send</c> rơi vào socket đã đóng ⇒ phiếu + mã vận đơn
/// bay mất, C# chờ tới hạn rồi ném <see cref="CauNoiRotGiuaChangException"/> và shop đó rụng.
/// </para>
/// <para>
/// ⚠ Phạm vi bài test: đây là NỬA PHÍA C# — "câu trả lời tới muộn, qua một socket KHÁC, vẫn hoàn tất đúng
/// chặng đang chờ". Nửa kia (hàng đợi + trần tuổi/trần số gói bên JavaScript) KHÔNG có test tự động nào canh
/// trong repo này vì không có bộ chạy test JS.
/// </para>
/// </summary>
public class GuiLaiCauTraLoiKhiNoiLaiTests
{
    /// <summary>Hạn chờ nối lại rút gọn cho test (production 90s).</summary>
    private static readonly TimeSpan ChoNoiLaiTest = TimeSpan.FromSeconds(10);

    /// <summary>Đóng vai <c>ChoChang.Prepare</c> (300s thật) — bài test phải xong sớm hơn hẳn.</summary>
    private static readonly TimeSpan HanChangDai = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task ExtensionGiuLaiPhieuRoiBanLaiSauKhiNoiLai_ChangPrepareVanHoanTat()
    {
        // Chặng ĐẮT NHẤT của cả vòng: mất câu trả lời này là mất phiếu giao của một đơn THẬT.
        await using var rig = await BridgeTestRig.StartAsync(choNoiLai: ChoNoiLaiTest);

        var prepare = rig.Channel.ArmPrepare();
        var cho = rig.Channel.AwaitAsync(prepare, HanChangDai, CancellationToken.None);

        // Cầu nối đứt TRƯỚC khi extension kịp gửi kết quả — đúng cửa sổ đang chữa.
        await rig.NgatKetNoiAsync();

        // Extension vẫn sống, làm nốt, xếp câu trả lời vào hàng đợi, rồi nối lại và XẢ hàng đợi.
        await rig.NoiLaiAsync();
        await rig.GuiAsync(new
        {
            action = "orderPrepared",
            orderCode = "260810ABCDEF",
            slipBase64 = "JVBERi0xLjQK",
            tracking = "SPXVN0123456789",
        });

        var dongHo = Stopwatch.StartNew();
        var kq = await cho;
        dongHo.Stop();

        Assert.NotNull(kq);
        Assert.Equal("260810ABCDEF", kq!.OrderCode);
        Assert.Equal("JVBERi0xLjQK", kq.SlipBase64);
        Assert.Equal("SPXVN0123456789", kq.Tracking);
        Assert.True(dongHo.Elapsed < ChoNoiLaiTest,
            $"Chặng nhận kết quả muộn hơn cả hạn rút ngắn — {dongHo.Elapsed.TotalSeconds:0.0}s.");
    }

    [Fact]
    public async Task KhongBanLaiCauTraLoi_ThiChangPrepareVanChet_DungNhuTruocKhiCoLuoi()
    {
        // Đối chứng: nối lại mà KHÔNG bắn lại thì đúng lỗi cũ trở lại. Không có bài này thì bài trên có thể
        // xanh chỉ vì hạ tầng dễ dãi chứ không phải vì lượt bắn lại có tác dụng.
        await using var rig = await BridgeTestRig.StartAsync(choNoiLai: TimeSpan.FromMilliseconds(400));

        var prepare = rig.Channel.ArmPrepare();
        var cho = rig.Channel.AwaitAsync(prepare, HanChangDai, CancellationToken.None);

        await rig.NgatKetNoiAsync();
        await rig.NoiLaiAsync(); // nối lại nhưng KHÔNG bắn lại câu trả lời nào

        await Assert.ThrowsAsync<CauNoiRotGiuaChangException>(() => cho);
    }
}
