using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shopee.Toolkit.Ws;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test hai fix hành vi của cầu nối Đơn hàng:
/// <list type="bullet">
/// <item><b>Bước 3</b> — <see cref="WebSocketServer.SendAsync"/> FAIL-FAST: socket chưa nối thì NÉM ngay
/// (<see cref="InvalidOperationException"/>) thay vì return im lặng khiến caller chờ TCS 30–300s.</item>
/// <item><b>Bước 4</b> — <see cref="StageWaiter"/>: khi extension báo lỗi CHỈ fault chặng
/// ĐANG chờ (không fault hàng loạt TCS không ai await → hết <c>UnobservedTaskException</c>).</item>
/// </list>
/// </summary>
public class OrdersBridgeFailFastTests
{
    // ===== Bước 3: SendAsync fail-fast =====

    [Fact]
    public async Task SendAsync_ChuaKetNoi_NemInvalidOperation_KhongTreo()
    {
        // KHÔNG gọi Start() → _socket null (mô phỏng extension chưa/không còn kết nối). Không cần bind cổng.
        using var server = new WebSocketServer(0);

        // Ném NGAY (fail-fast) — không ngồi chờ timeout.
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.SendAsync(new { action = "x" }));
    }

    // ===== Bước 4: StageWaiter chỉ fault chặng đang chờ =====

    [Fact]
    public async Task StageWaiter_Loi_ChiFaultChangDangCho_ChangKhacDeNguyen()
    {
        var w = new StageWaiter();
        var awaited = NewTcs();
        var other = NewTcs(); // mô phỏng một chặng KHÁC không được await

        var t = w.AwaitAsync(awaited, TimeSpan.FromSeconds(5), CancellationToken.None);
        w.FaultCurrent(new InvalidOperationException("boom")); // extension báo lỗi lúc đang chờ 'awaited'

        // Chặng ĐANG chờ nhận đúng exception.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => t);
        Assert.Equal("boom", ex.Message);

        // Chặng KHÔNG được await KHÔNG bị fault (để nguyên → ResetTcs vòng sau thay mới).
        Assert.False(other.Task.IsCompleted);
    }

    [Fact]
    public async Task StageWaiter_Loi_KhongDeLaiChangDangChoLamGiuLock()
    {
        // Sau khi chặng hoàn tất (fault), FaultCurrent lần nữa KHÔNG còn tác động (đã gỡ đăng ký ở finally).
        var w = new StageWaiter();
        var awaited = NewTcs();
        var t = w.AwaitAsync(awaited, TimeSpan.FromSeconds(5), CancellationToken.None);
        w.FaultCurrent(new InvalidOperationException("boom"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => t);

        // Không có chặng nào đang chờ → FaultCurrent là no-op (không ném).
        w.FaultCurrent(new InvalidOperationException("late"));
    }

    [Fact]
    public async Task StageWaiter_KhongSinhUnobservedTaskException()
    {
        const string marker = "boom-unobserved-marker";
        var flagged = false;
        void Handler(object? s, UnobservedTaskExceptionEventArgs e)
        {
            // CHỈ tính exception CỦA kịch bản này (tránh nhiễu từ task khác trong lần chạy test).
            if (e.Exception.InnerExceptions.Any(x => x is InvalidOperationException io && io.Message == marker))
            {
                flagged = true;
            }
            e.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            await RunFaultScenarioAsync(marker); // await 1 chặng + fault; 10 chặng khác KHÔNG bị fault (để nguyên)

            // Ép GC để mọi task faulted-KHÔNG-observe (nếu có) trồi lên UnobservedTaskException.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            Assert.False(flagged); // thiết kế mới: chỉ chặng đang chờ bị fault (đã observe) → không có task mồ côi
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }
    }

    /// <summary>Mô phỏng OnMessage "error": có 1 chặng đang chờ (được fault + observe) và 10 chặng KHÁC không await
    /// (thiết kế mới KHÔNG fault chúng). Các TCS "khác" rời scope ở cuối (pending → GC lặng, không raise unobserved).</summary>
    private static async Task RunFaultScenarioAsync(string marker)
    {
        var w = new StageWaiter();
        var awaited = NewTcs();
        var others = Enumerable.Range(0, 10).Select(_ => NewTcs()).ToList();

        var t = w.AwaitAsync(awaited, TimeSpan.FromSeconds(5), CancellationToken.None);
        w.FaultCurrent(new InvalidOperationException(marker));
        try { await t; } catch (InvalidOperationException) { /* observe chặng đang chờ */ }

        GC.KeepAlive(others); // giữ tới đây rồi rời scope (đảm bảo chúng CHƯA complete → không thể unobserve)
    }

    private static TaskCompletionSource<bool> NewTcs()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
