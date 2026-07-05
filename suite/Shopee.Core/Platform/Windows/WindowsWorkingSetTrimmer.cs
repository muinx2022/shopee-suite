using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Shopee.Core.Platform.Windows;

/// <summary>Trả working set của tiến trình app về OS qua psapi EmptyWorkingSet.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsWorkingSetTrimmer : IWorkingSetTrimmer
{
    public void TrimCurrentProcess()
    {
        try { EmptyWorkingSet(Process.GetCurrentProcess().Handle); } catch { }
    }

    [DllImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);
}
