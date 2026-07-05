using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace Shopee.Core.Platform.Linux;

/// <summary>
/// Thay Job Object trên Linux. KHÔNG có tương đương 1-đối-1 của KILL_ON_JOB_CLOSE, dùng phương án phân lớp:
///
///  1) Nếu có systemd user session: phóng Brave qua
///     `systemd-run --user --scope --collect --unit=shopeesuite-brave-&lt;guid&gt; -- &lt;brave&gt; &lt;args&gt;`.
///     Cả cây tiến trình Chromium (kể cả zygote setsid() thoát process-group) nằm TRONG 1 cgroup scope →
///     dọn triệt để bằng `systemctl --user stop`. Đây là bản gần nhất với Job Object.
///  2) Thoát BÌNH THƯỜNG (ProcessExit): stop mọi scope đã mở → Brave chết theo.
///  3) App bị SIGKILL: Linux KHÔNG bắt được → dựa vào BraveFleet.StartupSweep() ở lần mở sau (giết brave theo
///     managed-root qua /proc finder) — ĐIỂM DUY NHẤT yếu hơn Windows, đã chấp nhận.
///  4) Không có systemd-run: phóng thẳng (Process.Start). Reap dựa vào kill-theo-user-data-dir mà CALLER vẫn
///     gọi (BrowserLauncher.Kill/BraveProcessReaper qua /proc) + StartupSweep. Chế độ degraded (log 1 lần).
///
/// ConfigureLimits: trần tiến trình map best-effort sang systemd `--property=TasksMax=`; RAM-commit bỏ qua
/// (BraveFleet.window-gate đã siết số cửa sổ ở tầng app).
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxProcessLifetimeScope : IProcessLifetimeScope
{
    private readonly bool _systemd = DetectSystemdUserSession();
    private readonly ConcurrentBag<string> _units = new();   // tên scope unit đã mở (để stop lúc thoát)
    private int _activeProcessLimit;
    private int _cleanupHooked;
    private int _degradedLogged;

    public void ConfigureLimits(int activeProcessLimit, ulong jobMemoryLimitBytes)
    {
        _activeProcessLimit = Math.Max(0, activeProcessLimit);
    }

    public Process Start(string fileName, string arguments)
    {
        HookCleanupOnce();

        if (_systemd)
        {
            var unit = "shopeesuite-brave-" + Guid.NewGuid().ToString("N");
            var tasksMax = _activeProcessLimit > 0 ? $"--property=TasksMax={_activeProcessLimit} " : "";
            var runArgs = $"--user --scope --collect --unit={unit} {tasksMax}-- \"{fileName}\" {arguments}";
            try
            {
                var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "systemd-run",
                    Arguments = runArgs,
                    UseShellExecute = false,
                });
                if (p is not null)
                {
                    _units.Add(unit);
                    return p;
                }
            }
            catch { /* systemd-run không có/không chạy được → rơi xuống phóng thẳng */ }
        }

        // Fallback phóng thẳng (không scope). Reap = kill-theo-user-data-dir (caller) + StartupSweep.
        return Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
        })!;
    }

    private void HookCleanupOnce()
    {
        if (Interlocked.Exchange(ref _cleanupHooked, 1) != 0) return;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => StopAllScopes();
        if (!_systemd && Interlocked.Exchange(ref _degradedLogged, 1) == 0)
        {
            // Không có systemd-run → không giữ được "chết theo app" mạnh như Job Object; dựa StartupSweep.
            try { Console.Error.WriteLine("[ShopeeSuite] Không có systemd user session — Brave dọn theo StartupSweep (degraded)."); } catch { }
        }
    }

    private void StopAllScopes()
    {
        while (_units.TryTake(out var unit))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "systemctl",
                    Arguments = $"--user stop {unit}",
                    UseShellExecute = false,
                })?.WaitForExit(3000);
            }
            catch { }
        }
    }

    /// <summary>Có systemd user session không? Heuristic rẻ: XDG_RUNTIME_DIR có + thư mục systemd của user tồn tại.</summary>
    private static bool DetectSystemdUserSession()
    {
        try
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            return !string.IsNullOrWhiteSpace(xdg) && Directory.Exists(Path.Combine(xdg, "systemd"));
        }
        catch { return false; }
    }
}
