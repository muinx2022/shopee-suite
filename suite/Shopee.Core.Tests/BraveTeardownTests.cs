using System.Diagnostics;
using Shopee.Core.Browser;

namespace Shopee.Core.Tests;

/// <summary>
/// <see cref="BraveTeardown"/> — kịch bản dừng Brave dùng chung của 4 luồng. Test ở mức KHÔNG cần Brave:
/// nhánh không có tiến trình / tiến trình đã thoát, và các bước tuỳ chọn (đóng êm) chỉ chạy đúng lúc.
/// (Phần giết tiến trình Brave thật do <see cref="BraveProcessReaper"/> lo — cần Brave nên không test ở đây.)
/// </summary>
public sealed class BraveTeardownTests
{
    [Fact]
    public void KillAndReap_KhongCoTienTrinh_KhongProfile_KhongNem_Tra0()
    {
        Process? process = null;
        Assert.Equal(0, BraveTeardown.KillAndReap(ref process, userDataDir: null));
        Assert.Null(process);
    }

    [Fact]
    public void KillAndReap_KhongCoTienTrinh_KhongGoiDongEm()
    {
        var gracefulCalled = false;
        Process? process = null;

        BraveTeardown.KillAndReap(ref process, userDataDir: "   ", new BraveTeardownOptions
        {
            GracefulClose = () => gracefulCalled = true,
            WaitForExitMs = 1500,
        });

        Assert.False(gracefulCalled);
    }

    [Fact]
    public void KillAndReap_TienTrinhDaThoat_KhongDongEm_VanDisposeVaDatNull()
    {
        if (!OperatingSystem.IsWindows())
            return;   // test dựa vào cmd.exe

        var exited = StartExitedProcess();
        var gracefulCalled = false;
        var process = exited;

        BraveTeardown.KillAndReap(ref process, userDataDir: null, new BraveTeardownOptions
        {
            GracefulClose = () => gracefulCalled = true,
        });

        Assert.False(gracefulCalled);   // đã thoát → không cần đóng êm
        Assert.Null(process);
        // Đã Dispose: đụng vào handle sau đó phải ném (tham chiếu tiến trình đã giải phóng).
        Assert.Throws<InvalidOperationException>(() => _ = exited.Handle);
    }

    [Fact]
    public void Reap_ProfileRong_Tra0_KhongNem()
    {
        Assert.Equal(0, BraveTeardown.Reap(null));
        Assert.Equal(0, BraveTeardown.Reap("   ", includeCrashpadOrphans: true, sleepAfterReapMs: 400));
    }

    [Fact]
    public void Reap_KhongGietDuocAi_KhongNgu()
    {
        // Không có Brave nào của profile tưởng tượng này → killed = 0 → BỎ QUA sleep 400ms.
        var dir = Path.Combine(Path.GetTempPath(), "shopee-core-tests", Guid.NewGuid().ToString("N"));
        var sw = Stopwatch.StartNew();

        Assert.Equal(0, BraveTeardown.Reap(dir, includeCrashpadOrphans: true, sleepAfterReapMs: 5_000));

        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 5_000, $"Không được ngủ khi không giết được ai (mất {sw.ElapsedMilliseconds}ms).");
    }

    private static Process StartExitedProcess()
    {
        var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        process.WaitForExit();
        return process;
    }
}
