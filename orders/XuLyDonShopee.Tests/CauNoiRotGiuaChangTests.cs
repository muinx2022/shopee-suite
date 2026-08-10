using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// CẦU NỐI RỚT GIỮA MỘT CHẶNG ĐANG CHỜ — không được chờ mù hết hạn gốc.
/// <para>
/// Bối cảnh (đo thật 10/08/2026): network service của trình duyệt bị dựng lại đều đặn mỗi 240s, mỗi lần kéo sập
/// WebSocket; extension nối lại trong 0–1 giây. 14/15 nhịp vô hại vì không có lệnh nào đang bay. Nhịp thứ 15 rơi
/// trúng một lệnh <c>prepareNextOrder</c> đang bay: lệnh mất hẳn, C# nằm chờ trọn 300s (hạn chặng
/// <c>ChoChang.Prepare</c>) rồi mới ném — và ngoại lệ đó giết CẢ VÒNG, còn 6/12 shop chưa chạy.
/// </para>
/// <para>
/// Thuốc: socket đứt giữa chặng thì RÚT NGẮN hạn còn lại xuống tối đa <c>ChoNoiLai</c> rồi ném
/// <see cref="CauNoiRotGiuaChangException"/> (phân biệt được với timeout thường). CỐ Ý <b>không</b> fault ngay
/// lúc đứt và <b>không</b> tự gửi lại lệnh — hai bài dưới đây cố định đúng hai điều đó.
/// </para>
/// </summary>
public class CauNoiRotGiuaChangTests
{
    /// <summary>Hạn rút gọn cho test — production là 90s, chờ ngần ấy trong test là vô nghĩa.</summary>
    private static readonly TimeSpan ChoNoiLaiTest = TimeSpan.FromMilliseconds(400);

    /// <summary>Hạn chặng "dài" đóng vai <c>ChoChang.Prepare</c> (300s thật): bài test phải kết thúc SỚM hơn hẳn
    /// con số này, nếu không thì chính là bug đang sửa.</summary>
    private static readonly TimeSpan HanChangDai = TimeSpan.FromSeconds(20);

    private static TaskCompletionSource<bool> NewTcs()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ===== Mức đơn vị: StageWaiter =====

    [Fact]
    public async Task RutNganHanKhiDut_ChangHanDai_NemSomVaDungKieuNgoaiLe()
    {
        var w = new StageWaiter();
        var tcs = NewTcs();

        var t = w.AwaitAsync(tcs, HanChangDai, CancellationToken.None);
        Assert.True(w.RutNganHanKhiDut(ChoNoiLaiTest)); // có chặng đang chờ

        var dongHo = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<CauNoiRotGiuaChangException>(() => t);
        dongHo.Stop();

        Assert.Contains("lệnh đã gửi bị mất", ex.Message, StringComparison.Ordinal);
        Assert.True(dongHo.Elapsed < TimeSpan.FromSeconds(5),
            $"Phải ném theo hạn RÚT NGẮN, không chờ hết hạn gốc — thực tế {dongHo.Elapsed.TotalSeconds:0.0}s.");
    }

    [Fact]
    public async Task RutNganHan_KhongBaoGioNOI_ChangVonNganGiuHanGoc()
    {
        // Chặng ngắn (đọc DOM 30s ngoài đời) mà bị "rút" bằng một hạn DÀI hơn thì phải giữ NGUYÊN hạn gốc.
        var w = new StageWaiter();
        var tcs = NewTcs();

        var t = w.AwaitAsync(tcs, TimeSpan.FromMilliseconds(200), CancellationToken.None);
        w.RutNganHanKhiDut(HanChangDai);

        var dongHo = Stopwatch.StartNew();
        await Assert.ThrowsAsync<CauNoiRotGiuaChangException>(() => t);
        dongHo.Stop();

        Assert.True(dongHo.Elapsed < TimeSpan.FromSeconds(5),
            $"Hạn gốc 200ms bị NỚI thành {HanChangDai.TotalSeconds:0}s — thực tế {dongHo.Elapsed.TotalSeconds:0.0}s.");
    }

    [Fact]
    public void RutNganHanKhiDut_KhongAiDangCho_ThiKhongLamGi()
    {
        // Đứt lúc rảnh (14/15 nhịp ngày 10/08/2026) — không có chặng nào để rút, và tuyệt đối không được ném.
        var w = new StageWaiter();
        Assert.False(w.RutNganHanKhiDut(ChoNoiLaiTest));
    }

    [Fact]
    public async Task ChangXongTruocHan_ThiLuotRutHanSauDoKhongDungToiAi()
    {
        var w = new StageWaiter();
        var tcs = NewTcs();

        var t = w.AwaitAsync(tcs, HanChangDai, CancellationToken.None);
        tcs.TrySetResult(true);
        Assert.True(await t);

        Assert.False(w.RutNganHanKhiDut(ChoNoiLaiTest)); // đã gỡ đăng ký ở finally
    }

    [Fact]
    public async Task NguoiDungBamDung_VanLaOperationCanceled_DuSocketVuaDut()
    {
        // Hủy phải THẮNG: gói nó thành lỗi cầu nối là nuốt mất tín hiệu "user bấm Dừng" mà cả vòng dựa vào.
        var w = new StageWaiter();
        var tcs = NewTcs();
        using var cts = new CancellationTokenSource();

        var t = w.AwaitAsync(tcs, HanChangDai, cts.Token);
        w.RutNganHanKhiDut(TimeSpan.FromSeconds(10)); // đứt, nhưng hạn rút vẫn còn xa
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t);
    }

    // ===== Mức cầu nối thật: socket rơi rồi nối lại (BridgeTestRig) =====

    [Fact]
    public async Task SocketDutGiuaChang_NemLoiRotGiuaChang_KhongChoHetHanGoc()
    {
        await using var rig = await BridgeTestRig.StartAsync(choNoiLai: ChoNoiLaiTest);

        // Đúng cảnh 13:32:16 — lệnh "Chuẩn bị hàng" đã bay, chặng đang chờ với hạn dài.
        var prepare = rig.Channel.ArmPrepare();
        var cho = rig.Channel.AwaitAsync(prepare, HanChangDai, CancellationToken.None);

        await rig.NgatKetNoiAsync(); // 13:32:31 — network service dựng lại, WebSocket sập

        var dongHo = Stopwatch.StartNew();
        await Assert.ThrowsAsync<CauNoiRotGiuaChangException>(() => cho);
        dongHo.Stop();

        Assert.True(dongHo.Elapsed < TimeSpan.FromSeconds(5),
            $"Vẫn chờ mù gần hết hạn gốc — {dongHo.Elapsed.TotalSeconds:0.0}s.");
        Assert.True(await rig.ChoLogAsync("rớt GIỮA một chặng đang chờ"));
    }

    [Fact]
    public async Task SocketDutRoiNoiLaiVaTraLoiKip_ThiChangVanNhanKetQua()
    {
        // KHÔNG fault ngay lúc đứt: 14/15 nhịp ngày 10/08/2026 là extension nối lại trong 1 giây và vẫn trả lời.
        await using var rig = await BridgeTestRig.StartAsync(choNoiLai: TimeSpan.FromSeconds(10));

        var returns = rig.Channel.ArmReturns();
        var cho = rig.Channel.AwaitAsync(returns, HanChangDai, CancellationToken.None);

        await rig.NgatKetNoiAsync();
        await rig.NoiLaiAsync();
        await rig.GuiAsync(new { action = "pageData", kind = "returns", data = "{\"list\":[]}" });

        Assert.Contains("\"list\"", await cho);
    }

    [Fact]
    public async Task ChangQuaHanMaSocketVANNOI_ThiVanLaTimeoutThuong()
    {
        // Extension còn nối mà im lặng = nó đang KẸT, không phải mất lệnh — phải giữ TimeoutException trần để
        // chỗ gọi (và người đọc log) không đi chữa nhầm bệnh.
        await using var rig = await BridgeTestRig.StartAsync(choNoiLai: ChoNoiLaiTest);

        var toShip = rig.Channel.ArmToShip();
        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => rig.Channel.AwaitAsync(toShip, TimeSpan.FromMilliseconds(200), CancellationToken.None));
        Assert.IsNotType<CauNoiRotGiuaChangException>(ex);
    }
}
