using Shopee.Core.Platform.Windows;

namespace Shopee.Core.Platform;

/// <summary>
/// Điểm phân giải impl theo HĐH cho MỌI thứ chỉ-Windows của engine (Job Object, WMI quét tiến trình, bộ nhớ
/// hệ thống, định vị Brave, focus cửa sổ, DPAPI). Mọi call site đi qua đây → không còn P/Invoke/WMI/registry/
/// DPAPI rải rác. Windows impl nằm sau <see cref="OperatingSystem.IsWindows()"/> (thoả CA1416); Linux impl
/// lắp ở GĐ3.
/// </summary>
public static class PlatformServices
{
    public static IProcessLifetimeScope ProcessLifetime { get; }
    public static ISystemMemoryInfo Memory { get; }
    public static IWorkingSetTrimmer WorkingSet { get; }
    public static IBrowserProcessFinder ProcessFinder { get; }
    public static IBrowserLocator BrowserLocator { get; }
    public static IWindowActivator WindowActivator { get; }
    public static IOsCrypt OsCrypt { get; }

    static PlatformServices()
    {
        if (OperatingSystem.IsWindows())
        {
            ProcessLifetime = new WindowsProcessLifetimeScope();
            Memory = new WindowsSystemMemoryInfo();
            WorkingSet = new WindowsWorkingSetTrimmer();
            ProcessFinder = new WindowsBrowserProcessFinder();
            BrowserLocator = new WindowsBrowserLocator();
            WindowActivator = new WindowsWindowActivator();
            OsCrypt = new WindowsOsCrypt();
        }
        else
        {
            // GĐ3 lắp impl Linux (systemd-run scope, /proc finder, /proc/meminfo, locator, no-op focus/DPAPI).
            // GĐ1–2 chỉ chạy Windows nên nhánh này không bao giờ tới lúc runtime.
            throw new PlatformNotSupportedException(
                "Impl Linux chưa lắp (GĐ3). Bản hiện tại chỉ chạy trên Windows.");
        }
    }
}
