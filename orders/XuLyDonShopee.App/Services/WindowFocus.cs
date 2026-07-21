using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XuLyDonShopee.App.Services;

/// <summary>
/// Đưa cửa sổ chính của một tiến trình (Brave/Chromium của phiên) ra trước — dùng khi người dùng bấm vào
/// một tài khoản để "nhảy" tới cửa sổ shop đang chạy của nó. Windows-only (P/Invoke user32); OS khác no-op.
/// <para>
/// Best-effort: null/đã thoát/handle = 0/lỗi P/Invoke đều BỎ QUA, KHÔNG ném — để không phá luồng chọn/nạp
/// form. Brave fork tiến trình con nên <see cref="Process.MainWindowHandle"/> của tiến trình launcher đôi
/// khi = 0; ta thử <see cref="Process.Refresh"/> một lần rồi bỏ cuộc nếu vẫn 0.
/// </para>
/// </summary>
public static class WindowFocus
{
    /// <summary>Phục hồi cửa sổ nếu đang minimize (ShowWindow nCmdShow).</summary>
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>
    /// Cố đưa cửa sổ chính của <paramref name="process"/> ra trước. No-op nếu null/đã thoát, không phải
    /// Windows, hoặc chưa có <see cref="Process.MainWindowHandle"/>. Nuốt mọi lỗi.
    /// </summary>
    public static void BringToFront(Process? process)
    {
        try
        {
            if (process is null || process.HasExited)
            {
                return;
            }

            if (!OperatingSystem.IsWindows())
            {
                return; // chỉ hỗ trợ Windows (OS khác: no-op)
            }

            var handle = process.MainWindowHandle;
            if (handle == IntPtr.Zero)
            {
                // Brave có thể vừa fork xong, handle chưa cập nhật → refresh rồi thử lại một lần.
                process.Refresh();
                handle = process.MainWindowHandle;
            }

            if (handle == IntPtr.Zero)
            {
                return; // không có cửa sổ để đưa ra trước → bỏ qua
            }

            ShowWindow(handle, SW_RESTORE);
            SetForegroundWindow(handle);
        }
        catch
        {
            // Best-effort: mọi lỗi (đã thoát giữa chừng, P/Invoke fail...) đều bỏ qua, không phá luồng.
        }
    }
}
