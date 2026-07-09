using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Shopee.Core.Platform;

namespace Shopee.Core.Browser;

/// <summary>
/// Watchdog DÙNG CHUNG hạ mọi cửa sổ Brave AUTOMATION xuống taskbar ngay sau khi phóng, để không cướp
/// focus/màn hình của người dùng. STARTUPINFO (SW_SHOWMINIMIZED) chỉ ép được cửa sổ ĐẦU của tiến trình
/// stub — Brave fork browser THẬT ở PID khác (không thừa hưởng show-state) + các cửa sổ mở LẠI khi
/// relaunch/hồi phục không qua STARTUPINFO → phải quét thêm vài giây. Vì scrape phóng 10–20 Brave gần
/// nhau nên KHÔNG mỗi lượt một task quét riêng: <see cref="Register"/> chỉ ghi profile cần theo dõi vào
/// sổ đăng ký chung, một VÒNG LẶP NỀN DUY NHẤT enumerate brave.exe 1 lần/nhịp rồi minimize đúng cửa sổ.
/// Toàn bộ best-effort — nuốt mọi lỗi, không log (Windows-only; Linux <see cref="Register"/> no-op).
/// </summary>
public static class BraveWindowMinimizer
{
    /// <summary>Theo dõi profile của <paramref name="arguments"/> (đọc cờ <c>--user-data-dir</c>) trong
    /// <paramref name="durationMs"/> ms: mọi cửa sổ top-level của đúng profile đó bị hạ xuống taskbar
    /// (SW_SHOWMINNOACTIVE) và trả foreground về cửa sổ người dùng đang làm việc lúc gọi. No-op ngoài
    /// Windows / khi args không có <c>--user-data-dir</c>. Không bao giờ ném.</summary>
    public static void Register(string arguments, int durationMs = 10_000)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { WindowsImpl.Register(arguments, durationMs); } catch { }
    }

    [SupportedOSPlatform("windows")]
    private static class WindowsImpl
    {
        // Sổ đăng ký chung: key = user-data-dir đã chuẩn hoá, value = hạn theo dõi + cửa sổ foreground lúc
        // Register (để trả focus về). Cùng _gate bảo vệ vòng đời vòng lặp nền (chỉ 1 vòng lặp sống 1 lúc).
        private static readonly object _gate = new();
        private static readonly Dictionary<string, Entry> _watched = new(StringComparer.OrdinalIgnoreCase);
        private static bool _loopAlive;

        private static readonly string[] BraveOnly = ["brave.exe"];

        private readonly record struct Entry(long Deadline, IntPtr PrevForeground);

        internal static void Register(string arguments, int durationMs)
        {
            var dir = BraveProcessReaper.ExtractUserDataDir(arguments);
            if (string.IsNullOrWhiteSpace(dir)) return;
            var key = BraveProcessReaper.NormalizePath(dir);
            if (key.Length == 0) return;

            // Chụp cửa sổ người dùng đang làm việc NGAY lúc Register (trước khi Brave kịp cướp foreground).
            var prevForeground = GetForegroundWindow();
            var deadline = Environment.TickCount64 + durationMs;

            lock (_gate)
            {
                // Cùng profile phóng lại → gia hạn deadline + làm mới prevForeground (ghi đè).
                _watched[key] = new Entry(deadline, prevForeground);
                if (!_loopAlive)
                {
                    _loopAlive = true;
                    Task.Run(RunLoop);
                }
            }
        }

        private static void RunLoop()
        {
            try
            {
                while (true)
                {
                    Thread.Sleep(400);

                    List<KeyValuePair<string, Entry>> live;
                    lock (_gate)
                    {
                        var now = Environment.TickCount64;
                        foreach (var expired in _watched.Where(kv => kv.Value.Deadline <= now)
                                     .Select(kv => kv.Key).ToList())
                            _watched.Remove(expired);

                        if (_watched.Count == 0)
                        {
                            _loopAlive = false;   // sổ rỗng → thoát; Register sau sẽ khởi động lại vòng lặp
                            return;
                        }
                        live = _watched.ToList();
                    }

                    try { Sweep(live); } catch { }   // 1 nhịp lỗi KHÔNG được giết cả vòng lặp
                }
            }
            catch
            {
                lock (_gate) { _loopAlive = false; }
            }
        }

        private static void Sweep(List<KeyValuePair<string, Entry>> live)
        {
            // Enumerate brave.exe MỘT LẦN/nhịp → map pid → key profile đang theo dõi (khớp ĐÚNG giá trị
            // user-data-dir đã chuẩn hoá, tuyệt đối không Contains: acc_1 không được khớp acc_10).
            var byPid = new Dictionary<uint, string>();
            foreach (var p in PlatformServices.ProcessFinder.Enumerate(BraveOnly))
            {
                if (p.Pid <= 0) continue;
                var dir = BraveProcessReaper.ExtractUserDataDir(p.CommandLine);
                if (dir is null) continue;
                var nd = BraveProcessReaper.NormalizePath(dir);
                foreach (var kv in live)
                {
                    if (string.Equals(nd, kv.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        byPid[(uint)p.Pid] = kv.Key;
                        break;
                    }
                }
            }
            if (byPid.Count == 0) return;

            // Foreground hiện tại: nếu nó là một trong các cửa sổ Brave vừa hạ → trả focus về app trước.
            var foreground = GetForegroundWindow();
            string? grabbedKey = null;

            EnumWindows((hwnd, _) =>
            {
                try
                {
                    if (!IsWindowVisible(hwnd)) return true;
                    GetWindowThreadProcessId(hwnd, out var pid);
                    if (!byPid.TryGetValue(pid, out var key)) return true;

                    var cls = new StringBuilder(64);
                    GetClassName(hwnd, cls, cls.Capacity);
                    if (!cls.ToString().StartsWith("Chrome_WidgetWin", StringComparison.Ordinal)) return true;

                    // Cửa sổ ĐÃ tồn tại → SW_SHOWMINNOACTIVE hạ xuống taskbar mà KHÔNG activate (khác cửa sổ
                    // đầu của stub, nơi Chromium coi MINNOACTIVE như 'normal' nên STARTUPINFO phải MINIMIZED).
                    if (!IsIconic(hwnd))
                        ShowWindow(hwnd, SW_SHOWMINNOACTIVE);

                    if (hwnd == foreground) grabbedKey = key;
                }
                catch { }
                return true;
            }, IntPtr.Zero);

            if (grabbedKey is null) return;
            foreach (var kv in live)
            {
                if (!string.Equals(kv.Key, grabbedKey, StringComparison.OrdinalIgnoreCase)) continue;
                var prev = kv.Value.PrevForeground;
                if (prev != IntPtr.Zero && IsWindow(prev))
                    try { SetForegroundWindow(prev); } catch { }   // fail thì thôi — cửa sổ đã minimize nên foreground tự rơi về app trước
                break;
            }
        }

        // ── Hằng số ──
        private const int SW_SHOWMINNOACTIVE = 7;   // hạ xuống taskbar, KHÔNG activate

        // ── P/Invoke (user32) ──
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    }
}
