using System.Diagnostics;

namespace OpenMultiBraveLauncherV3;

/// <summary>Đưa cửa sổ chính của 1 tiến trình Brave lên trước (foreground) — dùng khi click dòng process.
/// Uỷ cho <see cref="Shopee.Core.Platform.PlatformServices.WindowActivator"/> (Windows = user32;
/// Linux GĐ3 = no-op/wmctrl).</summary>
internal static class WindowFocus
{
    public static void BringProcessWindowToFront(Process? process) =>
        Shopee.Core.Platform.PlatformServices.WindowActivator.BringProcessWindowToFront(process);
}
