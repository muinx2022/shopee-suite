using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Shopee.Core.Platform.Windows;

/// <summary>Bộ nhớ vật lý qua kernel32 GlobalMemoryStatusEx.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsSystemMemoryInfo : ISystemMemoryInfo
{
    public ulong TotalPhysicalBytes() => Mem().ullTotalPhys;
    public ulong AvailablePhysicalBytes() => Mem().ullAvailPhys;

    private static MEMORYSTATUSEX Mem()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        try { GlobalMemoryStatusEx(ref m); } catch { }
        return m;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
