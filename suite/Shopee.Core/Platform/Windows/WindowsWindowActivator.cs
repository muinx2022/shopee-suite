using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Shopee.Core.Platform.Windows;

/// <summary>Đưa cửa sổ chính của 1 tiến trình lên trước (foreground) qua user32 — dùng khi click dòng process.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsWindowActivator : IWindowActivator
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private const int SW_RESTORE = 9;

    public void BringProcessWindowToFront(Process? process)
    {
        if (process is null) return;
        try
        {
            process.Refresh();
            if (process.HasExited) return;
        }
        catch { return; }

        var h = process.MainWindowHandle;
        if (h == IntPtr.Zero)
            h = FindMainWindow((uint)process.Id);
        if (h == IntPtr.Zero) return;

        ShowWindow(h, SW_RESTORE);   // bỏ minimize nếu đang thu nhỏ
        SetForegroundWindow(h);
    }

    /// <summary>Tìm cửa sổ top-level đầu tiên (hiển thị + có tiêu đề) của pid — dự phòng khi MainWindowHandle = 0.</summary>
    private static IntPtr FindMainWindow(uint pid)
    {
        var found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var wpid);
            if (wpid == pid && IsWindowVisible(h) && GetWindowTextLength(h) > 0)
            {
                found = h;
                return false;   // dừng duyệt
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
